package main

import (
	"bufio"
	"bytes"
	"encoding/base64"
	"errors"
	"io"
	"log/slog"
	"net"
	"strings"
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

	err := srv.handleHttpProxy(mem, br)
	if err == nil {
		t.Fatal("expected error when auth required but missing")
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

	err = srv.handleHttpProxy(mem, br)
	if err == nil {
		t.Fatal("expected error when target is forbidden by ACL")
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

	reqRaw := "CONNECT 127.0.0.1:" + upstreamPortStr + " HTTP/1.1\r\nHost: 127.0.0.1:" + upstreamPortStr + "\r\n\r\n"
	mem := &memoryConn{
		in:  bytes.NewBufferString(reqRaw),
		out: new(bytes.Buffer),
	}
	br := bufio.NewReader(mem)

	err = srv.handleHttpProxy(mem, br)
	if err != nil && !errors.Is(err, io.EOF) {
		t.Logf("handleHttpProxy finished with: %v", err)
	}

	respStr := mem.out.String()
	if !strings.Contains(respStr, "200 Connection Established") {
		t.Fatalf("expected 200 Connection Established, got: %s", respStr)
	}
}
