package main

import (
	"bufio"
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

func TestHTTPForwardLimitsConcurrentConnectionsPerHost(t *testing.T) {
	const (
		limit    = 2
		requests = 4
	)
	entered := make(chan struct{}, requests)
	release := make(chan struct{})
	var releaseOnce sync.Once
	var active atomic.Int64
	var peak atomic.Int64

	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		current := active.Add(1)
		for observed := peak.Load(); current > observed; observed = peak.Load() {
			if peak.CompareAndSwap(observed, current) {
				break
			}
		}
		entered <- struct{}{}
		select {
		case <-release:
		case <-r.Context().Done():
		}
		active.Add(-1)
		_, _ = io.WriteString(w, "ok")
	}))
	t.Cleanup(func() {
		releaseOnce.Do(func() { close(release) })
		upstream.Close()
	})

	proxy := &Server{
		cfg:    Config{ConnectTimeout: time.Second, MaxHTTPConnsPerHost: limit, HTTPResponseHeaderTimeout: 3 * time.Second},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	results := make(chan error, requests)
	for i := 0; i < requests; i++ {
		client, proxyConn := net.Pipe()
		go func() {
			defer client.Close()
			defer proxyConn.Close()
			_ = client.SetDeadline(time.Now().Add(5 * time.Second))
			proxyDone := make(chan error, 1)
			go func() { proxyDone <- proxy.handleHttpProxy(proxyConn, bufio.NewReader(proxyConn)) }()
			if _, err := fmt.Fprintf(client, "GET %s/slow HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n", upstream.URL, strings.TrimPrefix(upstream.URL, "http://")); err != nil {
				results <- fmt.Errorf("write proxy request: %w", err)
				return
			}
			response, err := http.ReadResponse(bufio.NewReader(client), nil)
			if err != nil {
				results <- fmt.Errorf("read proxy response: %w", err)
				return
			}
			_, bodyErr := io.Copy(io.Discard, response.Body)
			_ = response.Body.Close()
			if bodyErr != nil {
				results <- fmt.Errorf("read proxy response body: %w", bodyErr)
				return
			}
			if response.StatusCode != http.StatusOK {
				results <- fmt.Errorf("proxy response status = %d, want 200", response.StatusCode)
				return
			}
			select {
			case err := <-proxyDone:
				if err != nil {
					results <- fmt.Errorf("proxy handler: %w", err)
					return
				}
			case <-time.After(time.Second):
				results <- fmt.Errorf("proxy handler did not stop")
				return
			}
			results <- nil
		}()
	}

	for i := 0; i < limit; i++ {
		select {
		case <-entered:
		case <-time.After(2 * time.Second):
			t.Fatal("upstream did not reach configured concurrency")
		}
	}
	select {
	case <-entered:
		t.Fatalf("upstream concurrency exceeded limit %d (peak %d)", limit, peak.Load())
	case <-time.After(100 * time.Millisecond):
	}
	if got := peak.Load(); got != limit {
		t.Fatalf("peak upstream concurrency = %d, want %d", got, limit)
	}
	releaseOnce.Do(func() { close(release) })
	for i := 0; i < requests; i++ {
		select {
		case err := <-results:
			if err != nil {
				t.Fatal(err)
			}
		case <-time.After(4 * time.Second):
			t.Fatal("proxy requests did not finish after upstream release")
		}
	}
}

func TestHTTPForwardResponseHeaderTimeoutReleasesBlockedUpstream(t *testing.T) {
	started := make(chan struct{}, 1)
	upstreamStopped := make(chan struct{}, 1)
	release := make(chan struct{})
	var releaseOnce sync.Once
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		started <- struct{}{}
		select {
		case <-r.Context().Done():
		case <-release:
		}
		upstreamStopped <- struct{}{}
		_, _ = io.WriteString(w, "late")
	}))
	t.Cleanup(func() {
		releaseOnce.Do(func() { close(release) })
		upstream.Close()
	})

	proxy := &Server{
		cfg:    Config{ConnectTimeout: time.Second, MaxHTTPConnsPerHost: 1, HTTPResponseHeaderTimeout: 120 * time.Millisecond},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	client, proxyConn := net.Pipe()
	t.Cleanup(func() { _ = client.Close(); _ = proxyConn.Close() })
	proxyDone := make(chan error, 1)
	go func() { proxyDone <- proxy.handleHttpProxy(proxyConn, bufio.NewReader(proxyConn)) }()
	_ = client.SetDeadline(time.Now().Add(2 * time.Second))
	if _, err := fmt.Fprintf(client, "GET %s/never-headers HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n", upstream.URL, strings.TrimPrefix(upstream.URL, "http://")); err != nil {
		t.Fatalf("write proxy request: %v", err)
	}
	request, err := http.NewRequest(http.MethodGet, upstream.URL+"/never-headers", nil)
	if err != nil {
		t.Fatalf("create response context request: %v", err)
	}
	startedAt := time.Now()
	response, err := http.ReadResponse(bufio.NewReader(client), request)
	if err != nil {
		t.Fatalf("read timeout response: %v", err)
	}
	_, bodyErr := io.Copy(io.Discard, response.Body)
	_ = response.Body.Close()
	if bodyErr != nil {
		t.Fatalf("read timeout response body: %v", bodyErr)
	}
	if response.StatusCode != http.StatusBadGateway {
		t.Fatalf("proxy response status = %d, want %d", response.StatusCode, http.StatusBadGateway)
	}
	if elapsed := time.Since(startedAt); elapsed > time.Second {
		t.Fatalf("response header timeout took %s, want < 1s", elapsed)
	}
	select {
	case <-started:
	case <-time.After(time.Second):
		t.Fatal("upstream request did not start")
	}
	select {
	case <-upstreamStopped:
	case <-time.After(time.Second):
		t.Fatal("upstream did not observe proxy cancellation")
	}
	select {
	case <-proxyDone:
	case <-time.After(time.Second):
		t.Fatal("proxy handler did not return after response header timeout")
	}
}

func TestHTTPForwardClientDisconnectCancelsActiveUpstreamRequest(t *testing.T) {
	upstreamStarted := make(chan struct{})
	upstreamCanceled := make(chan struct{})
	upstream := httptest.NewServer(http.HandlerFunc(func(_ http.ResponseWriter, r *http.Request) {
		close(upstreamStarted)
		<-r.Context().Done()
		close(upstreamCanceled)
	}))
	defer upstream.Close()

	proxy := &Server{
		cfg:    Config{ConnectTimeout: time.Second, HTTPResponseHeaderTimeout: 3 * time.Second},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	client, proxyConn := net.Pipe()
	proxyDone := make(chan error, 1)
	go func() { proxyDone <- proxy.handleConn(proxyConn) }()

	upstreamHost := strings.TrimPrefix(upstream.URL, "http://")
	if _, err := fmt.Fprintf(client, "GET %s/active HTTP/1.1\r\nHost: %s\r\n\r\n", upstream.URL, upstreamHost); err != nil {
		_ = client.Close()
		_ = proxyConn.Close()
		t.Fatalf("write proxy request: %v", err)
	}
	select {
	case <-upstreamStarted:
	case <-time.After(time.Second):
		_ = client.Close()
		_ = proxyConn.Close()
		t.Fatal("upstream request did not start")
	}

	started := time.Now()
	_ = client.Close()
	select {
	case <-upstreamCanceled:
		if elapsed := time.Since(started); elapsed > 500*time.Millisecond {
			t.Fatalf("upstream cancellation took %s after client disconnect, want < 500ms", elapsed)
		}
	case <-time.After(500 * time.Millisecond):
		_ = proxyConn.Close()
		t.Fatal("active upstream request was not canceled promptly after client disconnect")
	}

	select {
	case <-proxyDone:
	case <-time.After(time.Second):
		_ = proxyConn.Close()
		t.Fatal("proxy handler did not finish after client disconnect")
	}
	_ = proxyConn.Close()
}

func TestHTTPProxyServerHandlesPipelinedKeepAliveRequests(t *testing.T) {
	upstream := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("X-Upstream-Forwarded", r.Header.Get("X-Forwarded-For"))
		_, _ = io.WriteString(w, r.URL.Path)
	}))
	defer upstream.Close()

	proxy := &Server{
		cfg:    Config{ConnectTimeout: time.Second},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	client, proxyConn := net.Pipe()
	proxyDone := make(chan error, 1)
	go func() { proxyDone <- proxy.handleConn(proxyConn) }()
	defer client.Close()
	defer proxyConn.Close()
	_ = client.SetDeadline(time.Now().Add(5 * time.Second))

	upstreamHost := strings.TrimPrefix(upstream.URL, "http://")
	requests := "GET " + upstream.URL + "/first HTTP/1.1\r\nHost: " + upstreamHost + "\r\nX-Forwarded-For: 192.0.2.15\r\n\r\n" +
		"GET " + upstream.URL + "/second HTTP/1.1\r\nHost: " + upstreamHost + "\r\nX-Forwarded-For: 192.0.2.15\r\nConnection: close\r\n\r\n"
	if _, err := io.WriteString(client, requests); err != nil {
		t.Fatalf("write pipelined HTTP requests: %v", err)
	}
	reader := bufio.NewReader(client)
	for _, expectedPath := range []string{"/first", "/second"} {
		response, err := http.ReadResponse(reader, nil)
		if err != nil {
			t.Fatalf("read %s response: %v", expectedPath, err)
		}
		body, readErr := io.ReadAll(response.Body)
		_ = response.Body.Close()
		if readErr != nil {
			t.Fatalf("read %s body: %v", expectedPath, readErr)
		}
		if response.StatusCode != http.StatusOK || string(body) != expectedPath {
			t.Fatalf("response for %s = status %d body %q", expectedPath, response.StatusCode, body)
		}
		if got := response.Header.Get("X-Upstream-Forwarded"); got != "192.0.2.15" {
			t.Fatalf("forwarded header = %q, want preserved client value", got)
		}
	}
	select {
	case err := <-proxyDone:
		if err != nil {
			t.Fatalf("proxy handler returned error: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("proxy handler did not close after Connection: close")
	}
}

func TestHTTPProxyServerConnectPreservesBufferedTunnelPayload(t *testing.T) {
	upstreamListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen for CONNECT upstream: %v", err)
	}
	defer upstreamListener.Close()

	upstreamDone := make(chan error, 1)
	go func() {
		upstream, acceptErr := upstreamListener.Accept()
		if acceptErr != nil {
			upstreamDone <- acceptErr
			return
		}
		defer upstream.Close()
		payload := make([]byte, 5)
		if _, readErr := io.ReadFull(upstream, payload); readErr != nil {
			upstreamDone <- readErr
			return
		}
		if string(payload) != "hello" {
			upstreamDone <- fmt.Errorf("upstream payload = %q, want hello", payload)
			return
		}
		_, writeErr := io.WriteString(upstream, "world")
		upstreamDone <- writeErr
	}()

	proxy := &Server{
		cfg:    Config{ConnectTimeout: time.Second},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	client, proxyConn := net.Pipe()
	proxyDone := make(chan error, 1)
	go func() { proxyDone <- proxy.handleConn(proxyConn) }()
	defer client.Close()
	defer proxyConn.Close()
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))

	target := upstreamListener.Addr().String()
	if _, err := fmt.Fprintf(client, "CONNECT %s HTTP/1.1\r\nHost: %s\r\n\r\nhello", target, target); err != nil {
		t.Fatalf("write CONNECT request and first tunnel payload: %v", err)
	}
	reader := bufio.NewReader(client)
	statusLine, err := reader.ReadString('\n')
	if err != nil {
		t.Fatalf("read CONNECT status: %v", err)
	}
	if !strings.Contains(statusLine, "200 Connection Established") {
		t.Fatalf("CONNECT status = %q, want 200", strings.TrimSpace(statusLine))
	}
	for {
		line, readErr := reader.ReadString('\n')
		if readErr != nil {
			t.Fatalf("read CONNECT headers: %v", readErr)
		}
		if line == "\r\n" {
			break
		}
	}

	echo := make([]byte, 5)
	if _, err := io.ReadFull(reader, echo); err != nil {
		t.Fatalf("read tunneled response: %v", err)
	}
	if string(echo) != "world" {
		t.Fatalf("tunneled response = %q, want world", echo)
	}
	select {
	case err := <-upstreamDone:
		if err != nil {
			t.Fatalf("upstream tunnel handler: %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("upstream tunnel handler did not finish")
	}
	_ = client.Close()
	select {
	case <-proxyDone:
	case <-time.After(time.Second):
		t.Fatal("HTTP CONNECT proxy handler did not finish")
	}
}

func TestHTTPProxyServerRequiresAuthentication(t *testing.T) {
	proxy := &Server{
		cfg: Config{
			AuthMode:       "password",
			Username:       "test-user",
			Password:       "test-password",
			ConnectTimeout: time.Second,
		},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	client, proxyConn := net.Pipe()
	proxyDone := make(chan error, 1)
	go func() { proxyDone <- proxy.handleConn(proxyConn) }()
	defer client.Close()
	defer proxyConn.Close()
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))

	if _, err := io.WriteString(client, "GET http://example.com/ HTTP/1.1\r\nHost: example.com\r\nConnection: close\r\n\r\n"); err != nil {
		t.Fatalf("write unauthenticated proxy request: %v", err)
	}
	response, err := http.ReadResponse(bufio.NewReader(client), nil)
	if err != nil {
		t.Fatalf("read authentication response: %v", err)
	}
	_ = response.Body.Close()
	if response.StatusCode != http.StatusProxyAuthRequired {
		t.Fatalf("HTTP status = %d, want %d", response.StatusCode, http.StatusProxyAuthRequired)
	}
	if got := response.Header.Get("Proxy-Authenticate"); !strings.HasPrefix(got, "Basic ") {
		t.Fatalf("Proxy-Authenticate = %q, want Basic challenge", got)
	}
	select {
	case <-proxyDone:
	case <-time.After(time.Second):
		t.Fatal("proxy handler did not finish after authentication rejection")
	}
}
