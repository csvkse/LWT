package main

import (
	"bufio"
	"errors"
	"io"
	"net"
	"strings"
	"sync"
	"testing"
	"time"
)

var errBufferedRelayWrite = errors.New("buffered relay write failed")

type blockedRelayTarget struct {
	closed chan struct{}
	once   sync.Once
}

func newBlockedRelayTarget() *blockedRelayTarget {
	return &blockedRelayTarget{closed: make(chan struct{})}
}

func (c *blockedRelayTarget) Read([]byte) (int, error) {
	<-c.closed
	return 0, net.ErrClosed
}

func (c *blockedRelayTarget) Write([]byte) (int, error) { return 0, errBufferedRelayWrite }

func (c *blockedRelayTarget) Close() error {
	c.once.Do(func() { close(c.closed) })
	return nil
}

func (*blockedRelayTarget) LocalAddr() net.Addr              { return &net.TCPAddr{} }
func (*blockedRelayTarget) RemoteAddr() net.Addr             { return &net.TCPAddr{} }
func (*blockedRelayTarget) SetDeadline(time.Time) error      { return nil }
func (*blockedRelayTarget) SetReadDeadline(time.Time) error  { return nil }
func (*blockedRelayTarget) SetWriteDeadline(time.Time) error { return nil }

func newTCPConnPair(t *testing.T) (*net.TCPConn, *net.TCPConn) {
	t.Helper()
	listener, err := net.ListenTCP("tcp", &net.TCPAddr{IP: net.IPv4(127, 0, 0, 1)})
	if err != nil {
		t.Fatalf("listen for TCP pair: %v", err)
	}
	t.Cleanup(func() { _ = listener.Close() })

	peer, err := net.DialTCP("tcp", nil, listener.Addr().(*net.TCPAddr))
	if err != nil {
		t.Fatalf("dial TCP pair: %v", err)
	}
	accepted, err := listener.AcceptTCP()
	if err != nil {
		_ = peer.Close()
		t.Fatalf("accept TCP pair: %v", err)
	}
	t.Cleanup(func() {
		_ = accepted.Close()
		_ = peer.Close()
	})
	return accepted, peer
}

func TestRelayHardReadErrorClosesBothConnections(t *testing.T) {
	client, clientPeer := newTCPConnPair(t)
	upstream, upstreamPeer := newTCPConnPair(t)
	t.Cleanup(func() { _ = client.Close(); _ = upstream.Close() })

	srv := &Server{}
	done := make(chan error, 1)
	go func() { done <- srv.relay(client, upstream) }()

	// An RST makes one relay direction fail while the other direction has no
	// data and remains blocked in Read. relay must close both sides to release it.
	if err := clientPeer.SetLinger(0); err != nil {
		t.Fatalf("set RST behavior: %v", err)
	}
	if err := clientPeer.Close(); err != nil {
		t.Fatalf("close client peer with RST: %v", err)
	}

	select {
	case err := <-done:
		if err == nil {
			t.Fatal("relay should report the hard copy error")
		}
	case <-time.After(750 * time.Millisecond):
		_ = upstreamPeer.Close()
		t.Fatal("relay did not close the opposite connection after a hard copy error")
	}
}

func TestRelayReturnsBufferedDrainWriteErrorBeforeStartingCopies(t *testing.T) {
	client, clientPeer := net.Pipe()
	target := newBlockedRelayTarget()
	t.Cleanup(func() { _ = client.Close(); _ = clientPeer.Close(); _ = target.Close() })
	bufferedClient := &BufferedConn{Conn: client, r: bufio.NewReader(strings.NewReader("already buffered"))}
	if _, err := bufferedClient.r.(*bufio.Reader).Peek(1); err != nil {
		t.Fatalf("prime buffered client reader: %v", err)
	}

	done := make(chan error, 1)
	go func() { done <- (&Server{}).relay(bufferedClient, target) }()

	select {
	case err := <-done:
		if !errors.Is(err, errBufferedRelayWrite) {
			t.Fatalf("relay error = %v, want buffered write failure", err)
		}
	case <-time.After(500 * time.Millisecond):
		t.Fatal("relay ignored the buffered write failure and blocked in copy goroutines")
	}
}

func TestRelayClientHalfClosePreservesDelayedUpstreamResponse(t *testing.T) {
	client, clientPeer := newTCPConnPair(t)
	upstream, upstreamPeer := newTCPConnPair(t)
	t.Cleanup(func() { _ = client.Close(); _ = clientPeer.Close(); _ = upstream.Close(); _ = upstreamPeer.Close() })

	srv := &Server{}
	done := make(chan error, 1)
	go func() { done <- srv.relay(client, upstream) }()

	request := []byte("request body")
	if _, err := clientPeer.Write(request); err != nil {
		t.Fatalf("write request: %v", err)
	}
	if err := clientPeer.CloseWrite(); err != nil {
		t.Fatalf("half-close client peer: %v", err)
	}

	gotRequest, err := io.ReadAll(upstreamPeer)
	if err != nil {
		t.Fatalf("read relayed request: %v", err)
	}
	if string(gotRequest) != string(request) {
		t.Fatalf("relayed request = %q, want %q", gotRequest, request)
	}

	// A valid upstream is allowed to respond after the client has half-closed.
	time.Sleep(50 * time.Millisecond)
	response := []byte("delayed response")
	if _, err := upstreamPeer.Write(response); err != nil {
		t.Fatalf("write delayed response: %v", err)
	}
	if err := upstreamPeer.CloseWrite(); err != nil {
		t.Fatalf("half-close upstream peer: %v", err)
	}

	gotResponse, err := io.ReadAll(clientPeer)
	if err != nil {
		t.Fatalf("read relayed response: %v", err)
	}
	if string(gotResponse) != string(response) {
		t.Fatalf("relayed response = %q, want %q", gotResponse, response)
	}

	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("relay returned error after clean half-close: %v", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("relay did not finish after both peers half-closed")
	}
}
