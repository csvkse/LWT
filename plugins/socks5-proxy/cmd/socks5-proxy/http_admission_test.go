package main

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestHTTPHostRequestLimiterBoundsPendingRequests(t *testing.T) {
	limiter := NewHTTPHostRequestLimiter(1, 1)
	first, err := limiter.Acquire(context.Background(), "example.com:80")
	if err != nil {
		t.Fatalf("acquire first request: %v", err)
	}

	type acquireResult struct {
		permit *HTTPHostRequestPermit
		err    error
	}
	secondResult := make(chan acquireResult, 1)
	go func() {
		permit, err := limiter.Acquire(context.Background(), "example.com:80")
		secondResult <- acquireResult{permit: permit, err: err}
	}()

	waitForHTTPPending(t, limiter, 1)
	if _, err := limiter.Acquire(context.Background(), "example.com:80"); !errors.Is(err, ErrHTTPHostQueueFull) {
		t.Fatalf("third request error = %v, want ErrHTTPHostQueueFull", err)
	}
	if got := limiter.Snapshot().Rejected; got != 1 {
		t.Fatalf("rejected queue requests = %d, want 1", got)
	}

	first.Release()
	first.Release()
	select {
	case result := <-secondResult:
		if result.err != nil {
			t.Fatalf("second request acquire: %v", result.err)
		}
		result.permit.Release()
	case <-time.After(time.Second):
		t.Fatal("queued request was not admitted after capacity was released")
	}
	if snapshot := limiter.Snapshot(); snapshot.Active != 0 || snapshot.Pending != 0 {
		t.Fatalf("limiter did not drain after release: %+v", snapshot)
	}
}

func TestHTTPHostRequestLimiterTracksCapacityPerHost(t *testing.T) {
	limiter := NewHTTPHostRequestLimiter(1, 0)
	first, err := limiter.Acquire(context.Background(), "one.example:80")
	if err != nil {
		t.Fatalf("acquire first host: %v", err)
	}
	second, err := limiter.Acquire(context.Background(), "two.example:80")
	if err != nil {
		first.Release()
		t.Fatalf("different host was blocked by first host: %v", err)
	}
	first.Release()
	second.Release()
	if snapshot := limiter.Snapshot(); snapshot.Active != 0 || snapshot.Pending != 0 {
		t.Fatalf("limiter did not drain after releasing different hosts: %+v", snapshot)
	}
}

func TestHTTPHostRequestLimiterSupportsMoreThanDefaultSessionCountInOneShard(t *testing.T) {
	const activeHosts = 513
	limiter := NewHTTPHostRequestLimiter(1, 0)
	targetShard := limiter.shardFor("target-shard.example:80")
	permits := make([]*HTTPHostRequestPermit, 0, activeHosts)
	defer func() {
		for _, permit := range permits {
			permit.Release()
		}
	}()

	for candidate := 0; len(permits) < activeHosts && candidate < activeHosts*128; candidate++ {
		host := fmt.Sprintf("host-%d.example:80", candidate)
		if limiter.shardFor(host) != targetShard {
			continue
		}
		permit, err := limiter.Acquire(context.Background(), host)
		if err != nil {
			t.Fatalf("acquire host %d in the same shard: %v", candidate, err)
		}
		permits = append(permits, permit)
	}
	if len(permits) != activeHosts {
		t.Fatalf("found %d hosts for one shard, want %d", len(permits), activeHosts)
	}
	if got := limiter.Snapshot().Active; got != activeHosts {
		t.Fatalf("active HTTP requests = %d, want %d", got, activeHosts)
	}
}

func TestHTTPHostRequestLimiterConcurrentWaitersRespectActiveLimit(t *testing.T) {
	const (
		activeLimit = 8
		workers     = 128
	)
	limiter := NewHTTPHostRequestLimiter(activeLimit, workers-activeLimit)
	start := make(chan struct{})
	var active atomic.Int64
	var peak atomic.Int64
	var accepted atomic.Int64
	var wg sync.WaitGroup

	for worker := 0; worker < workers; worker++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			permit, err := limiter.Acquire(context.Background(), "hot.example:80")
			if err != nil {
				t.Errorf("acquire request permit: %v", err)
				return
			}
			accepted.Add(1)
			current := active.Add(1)
			for observed := peak.Load(); current > observed; observed = peak.Load() {
				if peak.CompareAndSwap(observed, current) {
					break
				}
			}
			time.Sleep(time.Millisecond)
			active.Add(-1)
			permit.Release()
		}()
	}
	close(start)
	wg.Wait()
	if got := accepted.Load(); got != workers {
		t.Fatalf("accepted requests = %d, want %d", got, workers)
	}
	if got := peak.Load(); got > activeLimit || got == 0 {
		t.Fatalf("peak active requests = %d, want 1..%d", got, activeLimit)
	}
	if snapshot := limiter.Snapshot(); snapshot.Active != 0 || snapshot.Pending != 0 {
		t.Fatalf("limiter did not drain after concurrent load: %+v", snapshot)
	}
}

func TestHTTPHostRequestLimiterCancelsTimedOutWaiter(t *testing.T) {
	limiter := NewHTTPHostRequestLimiter(1, 1)
	first, err := limiter.Acquire(context.Background(), "example.com:80")
	if err != nil {
		t.Fatalf("acquire first request: %v", err)
	}
	defer first.Release()

	ctx, cancel := context.WithTimeout(context.Background(), 40*time.Millisecond)
	defer cancel()
	if _, err := limiter.Acquire(ctx, "example.com:80"); !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("queued request error = %v, want context.DeadlineExceeded", err)
	}
	if snapshot := limiter.Snapshot(); snapshot.Pending != 0 || snapshot.TimedOut != 1 {
		t.Fatalf("timed out waiter was not removed: %+v", snapshot)
	}
}

func TestHTTPForwardRejectsWhenPerHostPendingQueueIsFull(t *testing.T) {
	started := make(chan struct{}, 2)
	release := make(chan struct{})
	var releaseOnce sync.Once
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		started <- struct{}{}
		<-release
		_, _ = io.WriteString(w, "ok")
	}))
	t.Cleanup(func() {
		releaseOnce.Do(func() { close(release) })
		upstream.Close()
	})

	proxy := &Server{
		cfg: Config{
			ConnectTimeout:            time.Second,
			MaxHTTPConnsPerHost:       1,
			MaxHTTPPendingPerHost:     1,
			HTTPQueueTimeout:          time.Second,
			HTTPResponseHeaderTimeout: 3 * time.Second,
		},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	first := startHTTPForwardExchange(t, proxy, upstream.URL)
	waitForSignal(t, started, "first upstream request")
	second := startHTTPForwardExchange(t, proxy, upstream.URL)
	waitForHTTPProxyPending(t, proxy, 1)
	third := startHTTPForwardExchange(t, proxy, upstream.URL)

	thirdResult := waitForHTTPExchange(t, third, "overloaded request")
	if thirdResult.status != http.StatusServiceUnavailable {
		t.Fatalf("overloaded request status = %d, want %d", thirdResult.status, http.StatusServiceUnavailable)
	}
	if thirdResult.elapsed > 500*time.Millisecond {
		t.Fatalf("overloaded request took %s, want fast rejection", thirdResult.elapsed)
	}

	releaseOnce.Do(func() { close(release) })
	for name, resultCh := range map[string]<-chan httpForwardExchangeResult{"first": first, "second": second} {
		result := waitForHTTPExchange(t, resultCh, name+" request")
		if result.status != http.StatusOK {
			t.Fatalf("%s request status = %d, want %d", name, result.status, http.StatusOK)
		}
	}
	if snapshot := proxy.getHTTPForwardRequestLimiter().Snapshot(); snapshot.Active != 0 || snapshot.Pending != 0 || snapshot.Rejected != 1 {
		t.Fatalf("unexpected HTTP admission counters after drain: %+v", snapshot)
	}
}

func TestHTTPForwardReturnsGatewayTimeoutWhenPerHostQueueWaitExpires(t *testing.T) {
	started := make(chan struct{}, 1)
	release := make(chan struct{})
	var releaseOnce sync.Once
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		started <- struct{}{}
		<-release
		_, _ = io.WriteString(w, "ok")
	}))
	t.Cleanup(func() {
		releaseOnce.Do(func() { close(release) })
		upstream.Close()
	})

	proxy := &Server{
		cfg: Config{
			ConnectTimeout:            time.Second,
			MaxHTTPConnsPerHost:       1,
			MaxHTTPPendingPerHost:     1,
			HTTPQueueTimeout:          60 * time.Millisecond,
			HTTPResponseHeaderTimeout: 3 * time.Second,
		},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	first := startHTTPForwardExchange(t, proxy, upstream.URL)
	waitForSignal(t, started, "blocked upstream request")
	second := startHTTPForwardExchange(t, proxy, upstream.URL)
	waitForHTTPProxyPending(t, proxy, 1)
	result := waitForHTTPExchange(t, second, "queued request")
	if result.status != http.StatusGatewayTimeout {
		t.Fatalf("queued request status = %d, want %d", result.status, http.StatusGatewayTimeout)
	}
	if result.elapsed > time.Second {
		t.Fatalf("queue timeout response took %s, want < 1s", result.elapsed)
	}

	releaseOnce.Do(func() { close(release) })
	if result := waitForHTTPExchange(t, first, "upstream request"); result.status != http.StatusOK {
		t.Fatalf("upstream request status = %d, want %d", result.status, http.StatusOK)
	}
	if snapshot := proxy.getHTTPForwardRequestLimiter().Snapshot(); snapshot.Pending != 0 || snapshot.TimedOut != 1 {
		t.Fatalf("timed out waiter was not drained: %+v", snapshot)
	}
}

type httpForwardExchangeResult struct {
	status  int
	elapsed time.Duration
	err     error
}

func startHTTPForwardExchange(t *testing.T, proxy *Server, upstreamURL string) <-chan httpForwardExchangeResult {
	t.Helper()
	client, proxyConn := net.Pipe()
	result := make(chan httpForwardExchangeResult, 1)
	go func() {
		defer client.Close()
		defer proxyConn.Close()
		startedAt := time.Now()
		_ = client.SetDeadline(startedAt.Add(4 * time.Second))
		proxyDone := make(chan error, 1)
		go func() { proxyDone <- proxy.handleHttpProxy(proxyConn, bufio.NewReader(proxyConn)) }()
		if _, err := fmt.Fprintf(client, "GET %s/queued HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n", upstreamURL, strings.TrimPrefix(upstreamURL, "http://")); err != nil {
			result <- httpForwardExchangeResult{elapsed: time.Since(startedAt), err: fmt.Errorf("write request: %w", err)}
			return
		}
		response, err := http.ReadResponse(bufio.NewReader(client), nil)
		if err != nil {
			result <- httpForwardExchangeResult{elapsed: time.Since(startedAt), err: fmt.Errorf("read response: %w", err)}
			return
		}
		_, bodyErr := io.Copy(io.Discard, response.Body)
		_ = response.Body.Close()
		if bodyErr != nil {
			result <- httpForwardExchangeResult{status: response.StatusCode, elapsed: time.Since(startedAt), err: fmt.Errorf("read response body: %w", bodyErr)}
			return
		}
		select {
		case <-proxyDone:
		case <-time.After(time.Second):
			result <- httpForwardExchangeResult{status: response.StatusCode, elapsed: time.Since(startedAt), err: errors.New("proxy handler did not stop")}
			return
		}
		result <- httpForwardExchangeResult{status: response.StatusCode, elapsed: time.Since(startedAt)}
	}()
	return result
}

func waitForHTTPExchange(t *testing.T, result <-chan httpForwardExchangeResult, name string) httpForwardExchangeResult {
	t.Helper()
	select {
	case value := <-result:
		if value.err != nil {
			t.Fatalf("%s: %v", name, value.err)
		}
		return value
	case <-time.After(2 * time.Second):
		t.Fatalf("%s did not finish", name)
		return httpForwardExchangeResult{}
	}
}

func waitForSignal(t *testing.T, signal <-chan struct{}, name string) {
	t.Helper()
	select {
	case <-signal:
	case <-time.After(2 * time.Second):
		t.Fatalf("%s did not start", name)
	}
}

func waitForHTTPPending(t *testing.T, limiter *HTTPHostRequestLimiter, want int64) {
	t.Helper()
	deadline := time.Now().Add(time.Second)
	for time.Now().Before(deadline) {
		if limiter.Snapshot().Pending == want {
			return
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatalf("pending HTTP requests = %d, want %d", limiter.Snapshot().Pending, want)
}

func waitForHTTPProxyPending(t *testing.T, proxy *Server, want int64) {
	t.Helper()
	waitForHTTPPending(t, proxy.getHTTPForwardRequestLimiter(), want)
}

func BenchmarkHTTPHostRequestLimiterParallel(b *testing.B) {
	benchmarkHTTPHostRequestLimiterParallel(b, false)
}

func BenchmarkHTTPHostRequestLimiterParallelHosts(b *testing.B) {
	benchmarkHTTPHostRequestLimiterParallel(b, true)
}

func benchmarkHTTPHostRequestLimiterParallel(b *testing.B, manyHosts bool) {
	limiter := NewHTTPHostRequestLimiter(128, 128)
	hosts := []string{"benchmark.example:80"}
	if manyHosts {
		hosts = make([]string, 32)
		for i := range hosts {
			hosts[i] = fmt.Sprintf("benchmark-%d.example:80", i)
		}
	}
	var nextHost atomic.Uint64
	b.ReportAllocs()
	b.RunParallel(func(pb *testing.PB) {
		host := hosts[int(nextHost.Add(1)-1)%len(hosts)]
		for pb.Next() {
			permit, err := limiter.Acquire(context.Background(), host)
			if err != nil {
				b.Error(err)
				return
			}
			permit.Release()
		}
	})
}
