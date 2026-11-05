package main

import (
	"bufio"
	"bytes"
	"encoding/base64"
	"fmt"
	"io"
	"log/slog"
	"net"
	"net/http"
	"strings"
	"sync/atomic"
	"testing"
	"time"
)

func TestIsHttpProxyMethod(t *testing.T) {
	methods := []byte{'C', 'G', 'P', 'H', 'D', 'O', 'T'}
	for _, m := range methods {
		if !isHttpProxyMethod(m) {
			t.Errorf("expected %c to be recognized as HTTP proxy method", m)
		}
	}

	if isHttpProxyMethod(0x05) {
		t.Errorf("0x05 (SOCKS5) must NOT be recognized as HTTP method")
	}
	if isHttpProxyMethod(0x04) {
		t.Errorf("0x04 (SOCKS4) must NOT be recognized as HTTP method")
	}
}

func TestValidateBasicAuth(t *testing.T) {
	user := "adminuser"
	pass := "secretpassword"

	validPayload := base64.StdEncoding.EncodeToString([]byte(user + ":" + pass))
	validHeader := "Basic " + validPayload

	if !validateBasicAuth(validHeader, user, pass) {
		t.Fatal("expected valid basic auth to succeed")
	}

	// Wrong password
	wrongPassPayload := base64.StdEncoding.EncodeToString([]byte(user + ":wrongpass"))
	if validateBasicAuth("Basic "+wrongPassPayload, user, pass) {
		t.Fatal("expected wrong password to fail")
	}

	// Invalid format (no colon)
	noColonPayload := base64.StdEncoding.EncodeToString([]byte("justusername"))
	if validateBasicAuth("Basic "+noColonPayload, user, pass) {
		t.Fatal("expected payload without colon to fail")
	}

	// Non-basic header
	if validateBasicAuth("Bearer token123", user, pass) {
		t.Fatal("expected bearer token to fail")
	}
}

// memoryConn implements net.Conn for unit testing
type memoryConn struct {
	net.Conn
	in  *bytes.Buffer
	out *bytes.Buffer
}

func (m *memoryConn) Read(b []byte) (n int, err error) {
	return m.in.Read(b)
}

func (m *memoryConn) Write(b []byte) (n int, err error) {
	return m.out.Write(b)
}

func (m *memoryConn) Close() error {
	return nil
}

func (m *memoryConn) RemoteAddr() net.Addr {
	return &net.TCPAddr{IP: net.ParseIP("127.0.0.1"), Port: 54321}
}

func (m *memoryConn) LocalAddr() net.Addr {
	return &net.TCPAddr{IP: net.ParseIP("127.0.0.1"), Port: 18080}
}

func (m *memoryConn) SetDeadline(t time.Time) error      { return nil }
func (m *memoryConn) SetReadDeadline(t time.Time) error  { return nil }
func (m *memoryConn) SetWriteDeadline(t time.Time) error { return nil }

func TestHandleHttpProxy_AuthRequired(t *testing.T) {
	srv := &Server{
		cfg: Config{
			AuthMode:       "password",
			Username:       "testadmin",
			Password:       "superpass",
			ConnectTimeout: 2 * time.Second,
		},
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}

	// Request without Proxy-Authorization
	reqRaw := "CONNECT example.com:443 HTTP/1.1\r\nHost: example.com:443\r\n\r\n"
	mem := &memoryConn{
		in:  bytes.NewBufferString(reqRaw),
		out: new(bytes.Buffer),
	}
	br := bufio.NewReader(mem)

	if err := srv.handleHttpProxy(mem, br); err != nil {
		t.Fatalf("HTTP proxy server returned an error: %v", err)
	}

	respStr := mem.out.String()
	if !strings.Contains(respStr, "407 Proxy Authentication Required") {
		t.Fatalf("expected 407 response, got: %s", respStr)
	}
}

func TestHandleHttpProxy_AclForbidden(t *testing.T) {
	acl, err := NewAclEngine("loopback", "", "allowed.corp", "127.0.0.1")
	if err != nil {
		t.Fatalf("NewAclEngine failed: %v", err)
	}

	srv := &Server{
		cfg: Config{
			AuthMode:       "none",
			ConnectTimeout: 2 * time.Second,
		},
		acl:      acl,
		logger:   slog.New(slog.NewTextHandler(io.Discard, nil)),
		resolver: &mockResolver{resultIPs: []net.IP{net.ParseIP("10.0.0.1")}},
	}

	// Request forbidden host
	reqRaw := "CONNECT forbidden.external.com:443 HTTP/1.1\r\nHost: forbidden.external.com:443\r\n\r\n"
	mem := &memoryConn{
		in:  bytes.NewBufferString(reqRaw),
		out: new(bytes.Buffer),
	}
	br := bufio.NewReader(mem)

	if err := srv.handleHttpProxy(mem, br); err != nil {
		t.Fatalf("HTTP proxy server returned an error: %v", err)
	}

	respStr := mem.out.String()
	if !strings.Contains(respStr, "403 Forbidden") {
		t.Fatalf("expected 403 Forbidden response, got: %s", respStr)
	}
}

func TestHandleHttpProxy_ConnectSuccess(t *testing.T) {
	// Start a local dummy TCP listener as upstream target
	upstreamListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen failed: %v", err)
	}
	defer upstreamListener.Close()

	upstreamAddr := upstreamListener.Addr().String()
	_, upstreamPortStr, _ := net.SplitHostPort(upstreamAddr)

	go func() {
		conn, err := upstreamListener.Accept()
		if err == nil {
			_ = conn.Close()
		}
	}()

	acl, _ := NewAclEngine("all", "", "", "")
	srv := &Server{
		cfg: Config{
			AuthMode:       "none",
			ConnectTimeout: 2 * time.Second,
		},
		acl:    acl,
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
		resolver: &mockResolver{
			resultIPs: []net.IP{net.ParseIP("127.0.0.1")},
		},
	}

	client, proxyConn := net.Pipe()
	proxyDone := make(chan error, 1)
	go func() { proxyDone <- srv.handleHttpProxy(proxyConn, bufio.NewReader(proxyConn)) }()
	defer client.Close()
	defer proxyConn.Close()
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))
	if _, err := fmt.Fprintf(client, "CONNECT 127.0.0.1:%s HTTP/1.1\r\nHost: 127.0.0.1:%s\r\n\r\n", upstreamPortStr, upstreamPortStr); err != nil {
		t.Fatalf("write CONNECT request: %v", err)
	}
	statusLine, err := bufio.NewReader(client).ReadString('\n')
	if err != nil {
		t.Fatalf("read CONNECT status: %v", err)
	}
	if !strings.Contains(statusLine, "200 Connection Established") {
		t.Fatalf("expected 200 Connection Established, got: %s", statusLine)
	}
	_ = client.Close()
	select {
	case <-proxyDone:
	case <-time.After(time.Second):
		t.Fatal("CONNECT handler did not finish after client disconnect")
	}
}

func TestHandleHttpProxyRoutesEachKeepAliveRequestToItsOwnHost(t *testing.T) {
	startUpstream := func(body string) string {
		t.Helper()
		listener, err := net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			t.Fatalf("listen for HTTP upstream: %v", err)
		}
		server := &http.Server{Handler: http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
			_, _ = io.WriteString(w, body)
		})}
		go func() { _ = server.Serve(listener) }()
		t.Cleanup(func() { _ = server.Close() })
		return listener.Addr().String()
	}

	firstUpstream := startUpstream("first upstream")
	secondUpstream := startUpstream("second upstream")
	acl, err := NewAclEngine("all", "", "", "")
	if err != nil {
		t.Fatalf("create ACL: %v", err)
	}
	srv := &Server{
		cfg:    Config{AuthMode: "none", ConnectTimeout: 2 * time.Second},
		acl:    acl,
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}

	proxyListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen for proxy client: %v", err)
	}
	defer proxyListener.Close()
	proxyDone := make(chan error, 1)
	go func() {
		proxyConn, acceptErr := proxyListener.Accept()
		if acceptErr != nil {
			proxyDone <- acceptErr
			return
		}
		proxyDone <- srv.handleHttpProxy(proxyConn, bufio.NewReader(proxyConn))
	}()

	client, err := net.Dial("tcp", proxyListener.Addr().String())
	if err != nil {
		t.Fatalf("connect to proxy: %v", err)
	}
	clientReader := bufio.NewReader(client)
	t.Cleanup(func() {
		_ = client.Close()
		select {
		case <-proxyDone:
		case <-time.After(2 * time.Second):
			t.Error("proxy handler did not stop after client close")
		}
	})

	requestAndRead := func(upstreamAddr, path string) string {
		t.Helper()
		url := "http://" + upstreamAddr + path
		if _, writeErr := fmt.Fprintf(client, "GET %s HTTP/1.1\r\nHost: %s\r\n\r\n", url, upstreamAddr); writeErr != nil {
			t.Fatalf("write proxy request: %v", writeErr)
		}
		request, requestErr := http.NewRequest(http.MethodGet, url, nil)
		if requestErr != nil {
			t.Fatalf("build response context: %v", requestErr)
		}
		response, responseErr := http.ReadResponse(clientReader, request)
		if responseErr != nil {
			t.Fatalf("read proxy response: %v", responseErr)
		}
		defer response.Body.Close()
		body, bodyErr := io.ReadAll(response.Body)
		if bodyErr != nil {
			t.Fatalf("read response body: %v", bodyErr)
		}
		return string(body)
	}

	if got := requestAndRead(firstUpstream, "/first"); got != "first upstream" {
		t.Fatalf("first response = %q, want first upstream", got)
	}
	if got := requestAndRead(secondUpstream, "/second"); got != "second upstream" {
		t.Fatalf("second response = %q, want second upstream", got)
	}
}

func TestHandleHttpProxyClearsHandshakeDeadlineBeforeSlowForwardResponse(t *testing.T) {
	upstreamListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen for HTTP upstream: %v", err)
	}
	upstreamServer := &http.Server{Handler: http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		time.Sleep(250 * time.Millisecond)
		_, _ = io.WriteString(w, "slow upstream")
	})}
	go func() { _ = upstreamServer.Serve(upstreamListener) }()
	t.Cleanup(func() { _ = upstreamServer.Close() })

	acl, err := NewAclEngine("all", "", "", "")
	if err != nil {
		t.Fatalf("create ACL: %v", err)
	}
	srv := &Server{
		cfg:    Config{AuthMode: "none", ConnectTimeout: 2 * time.Second},
		acl:    acl,
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	proxyListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen for proxy client: %v", err)
	}
	defer proxyListener.Close()
	proxyDone := make(chan error, 1)
	go func() {
		proxyConn, acceptErr := proxyListener.Accept()
		if acceptErr != nil {
			proxyDone <- acceptErr
			return
		}
		_ = proxyConn.SetDeadline(time.Now().Add(100 * time.Millisecond))
		proxyDone <- srv.handleHttpProxy(proxyConn, bufio.NewReader(proxyConn))
	}()

	client, err := net.Dial("tcp", proxyListener.Addr().String())
	if err != nil {
		t.Fatalf("connect to proxy: %v", err)
	}
	defer client.Close()
	_ = client.SetDeadline(time.Now().Add(2 * time.Second))
	url := "http://" + upstreamListener.Addr().String() + "/slow"
	if _, err := fmt.Fprintf(client, "GET %s HTTP/1.1\r\nHost: %s\r\nConnection: close\r\n\r\n", url, upstreamListener.Addr().String()); err != nil {
		t.Fatalf("write proxy request: %v", err)
	}
	request, err := http.NewRequest(http.MethodGet, url, nil)
	if err != nil {
		t.Fatalf("build response context: %v", err)
	}
	response, err := http.ReadResponse(bufio.NewReader(client), request)
	if err != nil {
		t.Fatalf("read response after handshake deadline elapsed: %v", err)
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		t.Fatalf("read delayed response body: %v", err)
	}
	if string(body) != "slow upstream" {
		t.Fatalf("response body = %q, want slow upstream", body)
	}

	select {
	case err := <-proxyDone:
		if err != nil {
			t.Fatalf("proxy handler failed: %v", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("proxy handler did not stop after Connection: close")
	}
}

func TestHandleHttpProxyReauthenticatesEachForwardRequestAndStripsCredentials(t *testing.T) {
	var upstreamRequests atomic.Int32
	upstreamListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen for HTTP upstream: %v", err)
	}
	upstreamServer := &http.Server{Handler: http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		upstreamRequests.Add(1)
		if r.Header.Get("Proxy-Authorization") != "" {
			_, _ = io.WriteString(w, "credential leaked")
			return
		}
		_, _ = io.WriteString(w, "ok")
	})}
	go func() { _ = upstreamServer.Serve(upstreamListener) }()
	t.Cleanup(func() { _ = upstreamServer.Close() })

	acl, err := NewAclEngine("all", "", "", "")
	if err != nil {
		t.Fatalf("create ACL: %v", err)
	}
	srv := &Server{
		cfg:    Config{AuthMode: "password", Username: "proxy-user", Password: "proxy-pass", ConnectTimeout: 2 * time.Second},
		acl:    acl,
		logger: slog.New(slog.NewTextHandler(io.Discard, nil)),
	}
	proxyListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen for proxy client: %v", err)
	}
	defer proxyListener.Close()
	proxyDone := make(chan error, 1)
	go func() {
		proxyConn, acceptErr := proxyListener.Accept()
		if acceptErr != nil {
			proxyDone <- acceptErr
			return
		}
		proxyDone <- srv.handleHttpProxy(proxyConn, bufio.NewReader(proxyConn))
	}()

	client, err := net.Dial("tcp", proxyListener.Addr().String())
	if err != nil {
		t.Fatalf("connect to proxy: %v", err)
	}
	defer client.Close()
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))
	clientReader := bufio.NewReader(client)
	url := "http://" + upstreamListener.Addr().String() + "/auth"
	authHeader := "Basic " + base64.StdEncoding.EncodeToString([]byte("proxy-user:proxy-pass"))

	_, _ = fmt.Fprintf(client, "GET %s HTTP/1.1\r\nHost: %s\r\nProxy-Authorization: %s\r\n\r\n", url, upstreamListener.Addr().String(), authHeader)
	firstRequest, _ := http.NewRequest(http.MethodGet, url, nil)
	firstResponse, err := http.ReadResponse(clientReader, firstRequest)
	if err != nil {
		t.Fatalf("read authenticated response: %v", err)
	}
	firstBody, err := io.ReadAll(firstResponse.Body)
	_ = firstResponse.Body.Close()
	if err != nil {
		t.Fatalf("read authenticated response body: %v", err)
	}
	if string(firstBody) != "ok" {
		t.Fatalf("upstream response = %q, want credentials stripped", firstBody)
	}

	_, _ = fmt.Fprintf(client, "GET %s HTTP/1.1\r\nHost: %s\r\n\r\n", url, upstreamListener.Addr().String())
	secondRequest, _ := http.NewRequest(http.MethodGet, url, nil)
	secondResponse, err := http.ReadResponse(clientReader, secondRequest)
	if err != nil {
		t.Fatalf("read authentication challenge: %v", err)
	}
	secondBody, err := io.ReadAll(secondResponse.Body)
	_ = secondResponse.Body.Close()
	if err != nil {
		t.Fatalf("read authentication challenge body: %v", err)
	}
	if secondResponse.StatusCode != http.StatusProxyAuthRequired || string(secondBody) != "Proxy authentication required.\n" {
		t.Fatalf("unauthorized second response = %d %q, want 407 challenge", secondResponse.StatusCode, secondBody)
	}
	if got := secondResponse.Header.Get("Proxy-Authenticate"); !strings.HasPrefix(got, "Basic ") {
		t.Fatalf("Proxy-Authenticate = %q, want Basic challenge", got)
	}
	if got := upstreamRequests.Load(); got != 1 {
		t.Fatalf("upstream request count = %d, want 1", got)
	}

	select {
	case <-proxyDone:
	case <-time.After(2 * time.Second):
		t.Fatal("proxy handler did not stop after rejecting the unauthenticated request")
	}
}
