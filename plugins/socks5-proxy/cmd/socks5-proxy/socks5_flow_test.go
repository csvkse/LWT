package main

import (
	"bufio"
	"encoding/binary"
	"io"
	"log/slog"
	"net"
	"strconv"
	"testing"
	"time"
)

func TestHandleConnSocks5ConnectForwardsPipelinedPayload(t *testing.T) {
	upstreamListener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("listen for upstream: %v", err)
	}
	defer upstreamListener.Close()
	upstreamDone := make(chan string, 1)
	go func() {
		conn, acceptErr := upstreamListener.Accept()
		if acceptErr != nil {
			upstreamDone <- "accept error: " + acceptErr.Error()
			return
		}
		defer conn.Close()
		payload := make([]byte, len("first application payload"))
		if _, readErr := io.ReadFull(conn, payload); readErr != nil {
			upstreamDone <- "read error: " + readErr.Error()
			return
		}
		if _, writeErr := io.WriteString(conn, "upstream response"); writeErr != nil {
			upstreamDone <- "write error: " + writeErr.Error()
			return
		}
		_, _ = io.Copy(io.Discard, conn)
		upstreamDone <- string(payload)
	}()

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
		t.Fatalf("listen for SOCKS client: %v", err)
	}
	defer proxyListener.Close()
	proxyDone := make(chan error, 1)
	go func() {
		proxyConn, acceptErr := proxyListener.Accept()
		if acceptErr != nil {
			proxyDone <- acceptErr
			return
		}
		proxyDone <- srv.handleConn(proxyConn)
	}()

	client, err := net.Dial("tcp", proxyListener.Addr().String())
	if err != nil {
		t.Fatalf("connect to SOCKS proxy: %v", err)
	}
	defer client.Close()
	_ = client.SetDeadline(time.Now().Add(5 * time.Second))
	clientReader := bufio.NewReader(client)
	if _, err := client.Write([]byte{0x05, 0x01, 0x00}); err != nil {
		t.Fatalf("write SOCKS greeting: %v", err)
	}
	methodReply := make([]byte, 2)
	if _, err := io.ReadFull(clientReader, methodReply); err != nil {
		t.Fatalf("read SOCKS method reply: %v", err)
	}
	if methodReply[0] != 0x05 || methodReply[1] != 0x00 {
		t.Fatalf("method reply = %v, want [5 0]", methodReply)
	}

	_, portText, err := net.SplitHostPort(upstreamListener.Addr().String())
	if err != nil {
		t.Fatalf("parse upstream address: %v", err)
	}
	port, err := strconv.Atoi(portText)
	if err != nil {
		t.Fatalf("parse upstream port: %v", err)
	}
	connectRequest := []byte{0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, 0, 0}
	binary.BigEndian.PutUint16(connectRequest[8:], uint16(port))
	coalescedRequest := append(connectRequest, []byte("first application payload")...)
	if _, err := client.Write(coalescedRequest); err != nil {
		t.Fatalf("write SOCKS CONNECT and first payload: %v", err)
	}

	connectReply := make([]byte, 10)
	if _, err := io.ReadFull(clientReader, connectReply); err != nil {
		t.Fatalf("read SOCKS CONNECT reply: %v", err)
	}
	if connectReply[0] != 0x05 || connectReply[1] != 0x00 {
		t.Fatalf("CONNECT reply = %v, want SOCKS success", connectReply)
	}
	response := make([]byte, len("upstream response"))
	if _, err := io.ReadFull(clientReader, response); err != nil {
		t.Fatalf("read relayed upstream response: %v", err)
	}
	if string(response) != "upstream response" {
		t.Fatalf("relayed response = %q, want upstream response", response)
	}

	if err := client.(*net.TCPConn).CloseWrite(); err != nil {
		t.Fatalf("half-close SOCKS client: %v", err)
	}
	select {
	case got := <-upstreamDone:
		if got != "first application payload" {
			t.Fatalf("upstream payload = %q, want first application payload", got)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("upstream did not receive the pipelined application payload")
	}
	select {
	case err := <-proxyDone:
		if err != nil {
			t.Fatalf("SOCKS proxy flow failed: %v", err)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("SOCKS proxy flow did not finish after both half-closes")
	}
}
