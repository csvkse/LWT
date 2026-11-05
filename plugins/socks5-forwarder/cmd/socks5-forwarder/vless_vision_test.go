package main

import (
	"bytes"
	"encoding/binary"
	"io"
	"net"
	"testing"
	"time"
)

// TestVlessConn_Standard 测试标准 VLESS 模式下首包响应头的剥离与裸流透传
func TestVlessConn_Standard(t *testing.T) {
	clientConn, serverConn := net.Pipe()
	defer clientConn.Close()
	defer serverConn.Close()

	var dummyUUID [16]byte
	vConn := newVlessConn(clientConn, dummyUUID, false)

	go func() {
		// 服务端响应: VLESS Header (0x00 version, 0x00 addonsLen) + 业务数据
		var buf bytes.Buffer
		buf.Write([]byte{0x00, 0x00})
		buf.WriteString("HTTP/1.1 200 OK\r\n\r\nHello Standard VLESS")
		_, _ = serverConn.Write(buf.Bytes())
	}()

	recvBuf := make([]byte, 128)
	n, err := vConn.Read(recvBuf)
	if err != nil && err != io.EOF {
		t.Fatalf("vConn.Read failed: %v", err)
	}

	result := string(recvBuf[:n])
	expected := "HTTP/1.1 200 OK\r\n\r\nHello Standard VLESS"
	if result != expected {
		t.Errorf("expected '%s', got '%s'", expected, result)
	}
}

// TestVlessConn_VisionDeframe 测试 XTLS-Vision 模式下 UUID 消费、解帧过滤 Padding 以及 DirectCopy 裸流切换
func TestVlessConn_VisionDeframe(t *testing.T) {
	clientConn, serverConn := net.Pipe()
	defer clientConn.Close()
	defer serverConn.Close()

	var dummyUUID [16]byte
	copy(dummyUUID[:], []byte("0123456789abcdef"))
	vConn := newVlessConn(clientConn, dummyUUID, true)

	go func() {
		var buf bytes.Buffer
		// 1. VLESS Header (0x00 version, 0x00 addonsLen)
		buf.Write([]byte{0x00, 0x00})
		// 2. Vision 16-byte UUID
		buf.Write(dummyUUID[:])

		// 3. Vision Frame 1: cmd=0x00, contentLen=5 ("HELLO"), paddingLen=3 ("XXX")
		frame1Hdr := make([]byte, 5)
		frame1Hdr[0] = 0x00 // CommandPaddingContinue
		binary.BigEndian.PutUint16(frame1Hdr[1:3], 5)
		binary.BigEndian.PutUint16(frame1Hdr[3:5], 3)
		buf.Write(frame1Hdr)
		buf.WriteString("HELLO")
		buf.WriteString("XXX") // Padding to be discarded

		// 4. Vision Frame 2: cmd=0x02 (CommandPaddingDirect), contentLen=6 (" WORLD"), paddingLen=2 ("YY")
		frame2Hdr := make([]byte, 5)
		frame2Hdr[0] = 0x02 // CommandPaddingDirect
		binary.BigEndian.PutUint16(frame2Hdr[1:3], 6)
		binary.BigEndian.PutUint16(frame2Hdr[3:5], 2)
		buf.Write(frame2Hdr)
		buf.WriteString(" WORLD")
		buf.WriteString("YY") // Padding to be discarded

		// 5. Subsequent raw TCP bytes after DirectCopy enabled
		buf.WriteString(" DIRECT_STREAM")

		_, _ = serverConn.Write(buf.Bytes())
	}()

	var received bytes.Buffer
	buf := make([]byte, 32)
	deadline := time.Now().Add(2 * time.Second)
	_ = clientConn.SetReadDeadline(deadline)

	for received.Len() < len("HELLO WORLD DIRECT_STREAM") {
		n, err := vConn.Read(buf)
		if n > 0 {
			received.Write(buf[:n])
		}
		if err != nil {
			break
		}
	}

	result := received.String()
	expected := "HELLO WORLD DIRECT_STREAM"
	if result != expected {
		t.Errorf("expected '%s', got '%s'", expected, result)
	}
}
