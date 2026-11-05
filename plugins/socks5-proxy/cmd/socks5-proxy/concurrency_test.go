package main

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"net"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

func TestConnectionAdmissionCapsConcurrentPermitsAndReleasesOnce(t *testing.T) {
	const (
		limit      = 8
		contenders = 128
	)
	admission := NewConnectionAdmission(limit)
	start := make(chan struct{})
	permits := make(chan *ConnectionPermit, contenders)
	var accepted atomic.Int64
	var rejected atomic.Int64
	var wg sync.WaitGroup

	for i := 0; i < contenders; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			permit, ok := admission.TryAcquire()
			if !ok {
				rejected.Add(1)
				return
			}
			accepted.Add(1)
			permits <- permit
		}()
	}
	close(start)
	wg.Wait()

	if got := accepted.Load(); got != limit {
		t.Fatalf("accepted permits = %d, want %d", got, limit)
	}
	if got := rejected.Load(); got != contenders-limit {
		t.Fatalf("rejected permits = %d, want %d", got, contenders-limit)
	}
	if got := admission.Active(); got != limit {
		t.Fatalf("active permits = %d, want %d", got, limit)
	}

	for i := int64(0); i < accepted.Load(); i++ {
		permit := <-permits
		permit.Release()
		permit.Release()
	}
	if got := admission.Active(); got != 0 {
		t.Fatalf("active permits after release = %d, want 0", got)
	}
	if permit, ok := admission.TryAcquire(); !ok {
		t.Fatal("permit was not available after release")
	} else {
		permit.Release()
	}
}

func TestAcceptedSessionRejectsAtCapacityAndReleasesOnClose(t *testing.T) {
	logger := slog.New(slog.NewTextHandler(io.Discard, nil))
	server := &Server{cfg: Config{AuthMode: "none"}, logger: logger}
	admission := NewConnectionAdmission(1)

	firstClient, firstProxy := net.Pipe()
	t.Cleanup(func() { _ = firstClient.Close(); _ = firstProxy.Close() })
	if !server.startAcceptedSession(firstProxy, admission) {
		t.Fatal("first session was unexpectedly rejected")
	}
	waitForInt64(t, func() int64 { return atomic.LoadInt64(&server.metrics.ActiveConns) }, 1)

	secondClient, secondProxy := net.Pipe()
	t.Cleanup(func() { _ = secondClient.Close(); _ = secondProxy.Close() })
	if server.startAcceptedSession(secondProxy, admission) {
		t.Fatal("session above capacity was accepted")
	}
	_ = secondClient.SetReadDeadline(time.Now().Add(time.Second))
	if _, err := secondClient.Read(make([]byte, 1)); err != io.EOF {
		t.Fatalf("rejected client read error = %v, want EOF", err)
	}

	_ = firstClient.Close()
	waitForInt64(t, func() int64 { return atomic.LoadInt64(&server.metrics.ActiveConns) }, 0)
	if got := atomic.LoadInt64(&server.metrics.TotalConns); got != 1 {
		t.Fatalf("total accepted connections = %d, want 1", got)
	}
	if got := atomic.LoadInt64(&server.metrics.RejectedConns); got != 1 {
		t.Fatalf("rejected connections = %d, want 1", got)
	}
	if got := atomic.LoadInt64(&server.metrics.PeakActiveConns); got != 1 {
		t.Fatalf("peak active connections = %d, want 1", got)
	}
}

func TestAcceptedSessionBurstKeepsActiveHandlersAtConfiguredLimit(t *testing.T) {
	const (
		limit     = 8
		burstSize = 128
	)
	server := &Server{
		cfg:    Config{AuthMode: "none"},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	admission := NewConnectionAdmission(limit)
	acceptedClients := make([]net.Conn, 0, limit)
	for i := 0; i < burstSize; i++ {
		client, proxyConn := net.Pipe()
		if server.startAcceptedSession(proxyConn, admission) {
			acceptedClients = append(acceptedClients, client)
		} else {
			_ = client.Close()
		}
	}
	defer func() {
		for _, client := range acceptedClients {
			_ = client.Close()
		}
	}()

	waitForInt64(t, func() int64 { return atomic.LoadInt64(&server.metrics.ActiveConns) }, limit)
	if got := atomic.LoadInt64(&server.metrics.TotalConns); got != limit {
		t.Fatalf("accepted connections = %d, want %d", got, limit)
	}
	if got := atomic.LoadInt64(&server.metrics.RejectedConns); got != burstSize-limit {
		t.Fatalf("rejected connections = %d, want %d", got, burstSize-limit)
	}
	if got := admission.Active(); got != limit {
		t.Fatalf("admission active = %d, want %d", got, limit)
	}
	if got := atomic.LoadInt64(&server.metrics.PeakActiveConns); got != limit {
		t.Fatalf("peak active connections = %d, want %d", got, limit)
	}

	for _, client := range acceptedClients {
		_ = client.Close()
	}
	waitForInt64(t, func() int64 { return atomic.LoadInt64(&server.metrics.ActiveConns) }, 0)
	if got := admission.Active(); got != 0 {
		t.Fatalf("admission active after client close = %d, want 0", got)
	}
}

func TestDnsLruCacheConcurrentReadsAndWritesStayWithinCapacity(t *testing.T) {
	const (
		capacity   = 32
		workers    = 16
		iterations = 500
	)
	cache := NewDnsLruCache(capacity, time.Minute, 5*time.Minute, time.Second)
	start := make(chan struct{})
	var wg sync.WaitGroup
	for worker := 0; worker < workers; worker++ {
		wg.Add(1)
		go func(worker int) {
			defer wg.Done()
			<-start
			for i := 0; i < iterations; i++ {
				key := "host-" + string(rune('a'+(worker+i)%26)) + ".example"
				cache.Set(key, []net.IP{net.ParseIP("192.0.2.10")})
				_, _, _ = cache.Get(key)
			}
		}(worker)
	}
	close(start)
	wg.Wait()
	if got := cache.Len(); got > capacity {
		t.Fatalf("cache size = %d, want <= %d", got, capacity)
	}
}

func TestSingleflightDoAsyncSchedulesOnlyOneWorkerPerKey(t *testing.T) {
	var group SingleflightGroup
	const contenders = 128
	start := make(chan struct{})
	finish := make(chan struct{})
	started := make(chan struct{}, 1)
	var calls atomic.Int64
	var scheduled atomic.Int64
	var wg sync.WaitGroup

	for i := 0; i < contenders; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			if group.DoAsync("refresh:hot.example", func() ([]net.IP, error) {
				calls.Add(1)
				started <- struct{}{}
				<-finish
				return []net.IP{net.ParseIP("192.0.2.11")}, nil
			}) {
				scheduled.Add(1)
			}
		}()
	}
	close(start)
	wg.Wait()

	if got := scheduled.Load(); got != 1 {
		t.Fatalf("scheduled workers = %d, want 1", got)
	}
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("async worker did not start")
	}
	close(finish)
	if got := calls.Load(); got != 1 {
		t.Fatalf("worker calls before release = %d, want 1", got)
	}
}

func TestSingleflightWaiterCanCancelWithoutCancellingSharedLookup(t *testing.T) {
	var group SingleflightGroup
	started := make(chan struct{}, 1)
	release := make(chan struct{})
	firstDone := make(chan error, 1)
	var calls atomic.Int64
	go func() {
		_, err := group.DoContext(context.Background(), "cold.example", func() ([]net.IP, error) {
			calls.Add(1)
			started <- struct{}{}
			<-release
			return []net.IP{net.ParseIP("192.0.2.12")}, nil
		})
		firstDone <- err
	}()
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("shared lookup did not start")
	}

	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	if _, err := group.DoContext(ctx, "cold.example", func() ([]net.IP, error) {
		calls.Add(1)
		return nil, errors.New("duplicate lookup should not run")
	}); !errors.Is(err, context.Canceled) {
		t.Fatalf("cancelled waiter error = %v, want context.Canceled", err)
	}
	if got := calls.Load(); got != 1 {
		t.Fatalf("lookup calls after cancelled waiter = %d, want 1", got)
	}

	close(release)
	select {
	case err := <-firstDone:
		if err != nil {
			t.Fatalf("shared lookup returned error: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("shared lookup did not complete after release")
	}
}

func TestPositiveConfigParsersUseFallbackForInvalidValues(t *testing.T) {
	t.Setenv("SOCKS5_TEST_POSITIVE_INT", "42")
	if got := positiveIntFromEnv("SOCKS5_TEST_POSITIVE_INT", 7); got != 42 {
		t.Fatalf("positive int = %d, want 42", got)
	}
	t.Setenv("SOCKS5_TEST_POSITIVE_INT", "0")
	if got := positiveIntFromEnv("SOCKS5_TEST_POSITIVE_INT", 7); got != 7 {
		t.Fatalf("invalid positive int fallback = %d, want 7", got)
	}
	t.Setenv("SOCKS5_TEST_POSITIVE_DURATION", "250ms")
	if got := positiveDurationFromEnv("SOCKS5_TEST_POSITIVE_DURATION", time.Second); got != 250*time.Millisecond {
		t.Fatalf("positive duration = %s, want 250ms", got)
	}
	t.Setenv("SOCKS5_TEST_POSITIVE_DURATION", "invalid")
	if got := positiveDurationFromEnv("SOCKS5_TEST_POSITIVE_DURATION", time.Second); got != time.Second {
		t.Fatalf("invalid duration fallback = %s, want 1s", got)
	}
}

func waitForInt64(t *testing.T, read func() int64, want int64) {
	t.Helper()
	deadline := time.Now().Add(time.Second)
	for time.Now().Before(deadline) {
		if got := read(); got == want {
			return
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatalf("value = %d, want %d", read(), want)
}
