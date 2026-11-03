package main

import (
	"bufio"
	"context"
	"crypto/rand"
	"crypto/tls"
	"encoding/base64"
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"
)

// WebSocketStreamConn 封装底层网络连接，对外呈现标准的 net.Conn 接口
// 并在传输时自动包装/解包 RFC 6455 二进制 WebSocket 数据帧
type WebSocketStreamConn struct {
	rawConn    net.Conn
	reader     *bufio.Reader
	writeMu    sync.Mutex
	readBuffer []byte
	isClosed   bool
	closeMu    sync.Mutex
}

// DialWebSocket 发起 WebSocket/WSS 握手，连接远端 LWT Ingress 伪装隧道
func DialWebSocket(ctx context.Context, rawURL, token, subprotocolPrefix string, skipTLSVerify bool, sni string) (net.Conn, error) {
	u, err := url.Parse(rawURL)
	if err != nil {
		return nil, fmt.Errorf("解析上游 URL 失败: %w", err)
	}

	host := u.Host
	if !strings.Contains(host, ":") {
		if u.Scheme == "wss" || u.Scheme == "https" {
			host += ":443"
		} else {
			host += ":80"
		}
	}

	var rawConn net.Conn
	dialer := &net.Dialer{Timeout: 10 * time.Second}

	if u.Scheme == "wss" || u.Scheme == "https" {
		serverName := sni
		if serverName == "" {
			serverName = u.Hostname()
		}
		tlsConfig := &tls.Config{
			InsecureSkipVerify: skipTLSVerify,
			ServerName:         serverName,
		}
		rawConn, err = tls.DialWithDialer(dialer, "tcp", host, tlsConfig)
		if err != nil {
			return nil, fmt.Errorf("TLS 连接上游 %s 失败: %w", host, err)
		}
	} else {
		rawConn, err = dialer.DialContext(ctx, "tcp", host)
		if err != nil {
			return nil, fmt.Errorf("TCP 连接上游 %s 失败: %w", host, err)
		}
	}

	// 构造 RFC 6455 握手秘钥
	keyBytes := make([]byte, 16)
	_, _ = rand.Read(keyBytes)
	secWebSocketKey := base64.StdEncoding.EncodeToString(keyBytes)

	reqPath := u.RequestURI()
	if reqPath == "" {
		reqPath = "/"
	}

	// 组装 HTTP 101 Switching Protocols 升级请求
	req, err := http.NewRequestWithContext(ctx, "GET", rawURL, nil)
	if err != nil {
		rawConn.Close()
		return nil, err
	}

	req.Header.Set("Host", u.Host)
	req.Header.Set("Upgrade", "websocket")
	req.Header.Set("Connection", "Upgrade")
	req.Header.Set("Sec-WebSocket-Key", secWebSocketKey)
	req.Header.Set("Sec-WebSocket-Version", "13")

	if token != "" {
		// 子协议无痕鉴权 (Sec-WebSocket-Protocol)
		prefix := subprotocolPrefix
		if prefix == "" {
			prefix = "lwt-token."
		}
		req.Header.Set("Sec-WebSocket-Protocol", prefix+token)
		req.Header.Set("X-LWT-Token", token)
	}

	if err := req.Write(rawConn); err != nil {
		rawConn.Close()
		return nil, fmt.Errorf("发送 WebSocket 握手头失败: %w", err)
	}

	reader := bufio.NewReader(rawConn)
	resp, err := http.ReadResponse(reader, req)
	if err != nil {
		rawConn.Close()
		return nil, fmt.Errorf("读取 WebSocket 握手响应失败: %w", err)
	}

	if resp.StatusCode != http.StatusSwitchingProtocols {
		rawConn.Close()
		return nil, fmt.Errorf("上游拒绝 WebSocket 升级: HTTP %d %s", resp.StatusCode, resp.Status)
	}

	return &WebSocketStreamConn{
		rawConn: rawConn,
		reader:  reader,
	}, nil
}

// Write 将原始字节流打包为 RFC 6455 掩码二进制帧并发送给远端
func (w *WebSocketStreamConn) Write(p []byte) (int, error) {
	if len(p) == 0 {
		return 0, nil
	}

	w.writeMu.Lock()
	defer w.writeMu.Unlock()

	payloadLen := len(p)
	var header []byte

	// 客户端到服务端必须掩码 (Mask bit = 1)
	if payloadLen < 126 {
		header = make([]byte, 2+4)
		header[0] = 0x82 // FIN (0x80) | Binary Opcode (0x02)
		header[1] = 0x80 | byte(payloadLen)
	} else if payloadLen <= 65535 {
		header = make([]byte, 4+4)
		header[0] = 0x82
		header[1] = 0x80 | 126
		binary.BigEndian.PutUint16(header[2:4], uint16(payloadLen))
	} else {
		header = make([]byte, 10+4)
		header[0] = 0x82
		header[1] = 0x80 | 127
		binary.BigEndian.PutUint64(header[2:10], uint64(payloadLen))
	}

	// 生成 4 字节掩码秘钥 (Masking Key)
	maskKeyOffset := len(header) - 4
	maskKey := header[maskKeyOffset:]
	if _, err := rand.Read(maskKey); err != nil {
		return 0, err
	}

	// 对负载数据执行 XOR 掩码转换
	maskedPayload := make([]byte, payloadLen)
	for i := 0; i < payloadLen; i++ {
		maskedPayload[i] = p[i] ^ maskKey[i%4]
	}

	// 组合发送：Header (含 Mask) + Masked Payload
	fullPacket := append(header, maskedPayload...)
	_, err := w.rawConn.Write(fullPacket)
	if err != nil {
		return 0, err
	}

	return payloadLen, nil
}

// Read 从底层接收并解包 WebSocket 帧负载，透明呈现为普通流
func (w *WebSocketStreamConn) Read(b []byte) (int, error) {
	if len(w.readBuffer) > 0 {
		n := copy(b, w.readBuffer)
		w.readBuffer = w.readBuffer[n:]
		return n, nil
	}

	for {
		// 1. 读取帧头部 2 字节
		var header [2]byte
		if _, err := io.ReadFull(w.reader, header[:]); err != nil {
			return 0, err
		}

		opcode := header[0] & 0x0F
		isMasked := (header[1] & 0x80) != 0
		lenByte := header[1] & 0x7F

		var payloadLen int64
		if lenByte < 126 {
			payloadLen = int64(lenByte)
		} else if lenByte == 126 {
			var extLen [2]byte
			if _, err := io.ReadFull(w.reader, extLen[:]); err != nil {
				return 0, err
			}
			payloadLen = int64(binary.BigEndian.Uint16(extLen[:]))
		} else {
			var extLen [8]byte
			if _, err := io.ReadFull(w.reader, extLen[:]); err != nil {
				return 0, err
			}
			payloadLen = int64(binary.BigEndian.Uint64(extLen[:]))
		}

		var maskKey [4]byte
		if isMasked {
			if _, err := io.ReadFull(w.reader, maskKey[:]); err != nil {
				return 0, err
			}
		}

		// 处理不同类型的帧
		switch opcode {
		case 0x08: // Close
			_ = w.Close()
			return 0, io.EOF

		case 0x09: // Ping: 发送 Pong 回应
			pingPayload := make([]byte, payloadLen)
			if _, err := io.ReadFull(w.reader, pingPayload); err != nil {
				return 0, err
			}
			w.sendPong(pingPayload)
			continue

		case 0x0A: // Pong: 丢弃并继续
			if payloadLen > 0 {
				_, _ = io.CopyN(io.Discard, w.reader, payloadLen)
			}
			continue

		case 0x01, 0x02, 0x00: // Text (0x01), Binary (0x02), Continuation (0x00)
			payload := make([]byte, payloadLen)
			if _, err := io.ReadFull(w.reader, payload); err != nil {
				return 0, err
			}

			if isMasked {
				for i := int64(0); i < payloadLen; i++ {
					payload[i] ^= maskKey[i%4]
				}
			}

			n := copy(b, payload)
			if int64(n) < payloadLen {
				w.readBuffer = payload[n:]
			}
			return n, nil

		default:
			if payloadLen > 0 {
				_, _ = io.CopyN(io.Discard, w.reader, payloadLen)
			}
			return 0, fmt.Errorf("不支持的 WebSocket 操作码: 0x%X", opcode)
		}
	}
}

func (w *WebSocketStreamConn) sendPong(payload []byte) {
	w.writeMu.Lock()
	defer w.writeMu.Unlock()

	header := []byte{0x8A, 0x80 | byte(len(payload))} // FIN | Pong (0x0A) | Masked
	mask := []byte{0x12, 0x34, 0x56, 0x78}
	header = append(header, mask...)

	masked := make([]byte, len(payload))
	for i := 0; i < len(payload); i++ {
		masked[i] = payload[i] ^ mask[i%4]
	}
	_, _ = w.rawConn.Write(append(header, masked...))
}

// Close 关闭连接
func (w *WebSocketStreamConn) Close() error {
	w.closeMu.Lock()
	defer w.closeMu.Unlock()
	if w.isClosed {
		return nil
	}
	w.isClosed = true
	return w.rawConn.Close()
}

func (w *WebSocketStreamConn) LocalAddr() net.Addr                { return w.rawConn.LocalAddr() }
func (w *WebSocketStreamConn) RemoteAddr() net.Addr               { return w.rawConn.RemoteAddr() }
func (w *WebSocketStreamConn) SetDeadline(t time.Time) error      { return w.rawConn.SetDeadline(t) }
func (w *WebSocketStreamConn) SetReadDeadline(t time.Time) error  { return w.rawConn.SetReadDeadline(t) }
func (w *WebSocketStreamConn) SetWriteDeadline(t time.Time) error { return w.rawConn.SetWriteDeadline(t) }

var _ net.Conn = (*WebSocketStreamConn)(nil)
