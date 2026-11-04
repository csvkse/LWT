package main

import (
	"bufio"
	"crypto/aes"
	"crypto/cipher"
	"crypto/sha256"
	"crypto/tls"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"fmt"
	"io"
	"net"
	"strings"
	"sync"
	"testing"
	"time"
)

// StartMockEchoServer 启动标准 TCP Echo 服务端，用于端到端回声与哈希验证
func StartMockEchoServer(t *testing.T) (string, func()) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("启动 Mock Echo Server 失败: %v", err)
	}

	var wg sync.WaitGroup
	quit := make(chan struct{})

	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				select {
				case <-quit:
					return
				default:
					return
				}
			}
			wg.Add(1)
			go func(c net.Conn) {
				defer wg.Done()
				defer c.Close()
				_, _ = io.Copy(c, c)
			}(conn)
		}
	}()

	teardown := func() {
		close(quit)
		_ = ln.Close()
		wg.Wait()
	}

	return ln.Addr().String(), teardown
}

// StartMockSocks5Server 启动本地 SOCKS5 Mock 仿真服务端
func StartMockSocks5Server(t *testing.T, username, password string) (string, func()) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("启动 Mock SOCKS5 失败: %v", err)
	}

	var wg sync.WaitGroup
	quit := make(chan struct{})

	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			wg.Add(1)
			go func(c net.Conn) {
				defer wg.Done()
				defer c.Close()

				// 1. 握手
				buf := make([]byte, 2)
				if _, err := io.ReadFull(c, buf); err != nil {
					return
				}
				methods := make([]byte, buf[1])
				if _, err := io.ReadFull(c, methods); err != nil {
					return
				}

				if username != "" {
					// 要求认证
					_, _ = c.Write([]byte{0x05, 0x02})
					authHeader := make([]byte, 2)
					if _, err := io.ReadFull(c, authHeader); err != nil {
						return
					}
					uLen := int(authHeader[1])
					uBuf := make([]byte, uLen)
					if _, err := io.ReadFull(c, uBuf); err != nil {
						return
					}
					pLenBuf := make([]byte, 1)
					if _, err := io.ReadFull(c, pLenBuf); err != nil {
						return
					}
					pBuf := make([]byte, int(pLenBuf[0]))
					if _, err := io.ReadFull(c, pBuf); err != nil {
						return
					}

					if string(uBuf) != username || string(pBuf) != password {
						_, _ = c.Write([]byte{0x01, 0x01}) // 认证失败
						return
					}
					_, _ = c.Write([]byte{0x01, 0x00}) // 认证成功
				} else {
					_, _ = c.Write([]byte{0x05, 0x00})
				}

				// 2. CONNECT 请求
				req := make([]byte, 4)
				if _, err := io.ReadFull(c, req); err != nil {
					return
				}
				var host string
				switch req[3] {
				case 0x01: // IPv4
					ip := make([]byte, 4)
					_, _ = io.ReadFull(c, ip)
					host = net.IP(ip).String()
				case 0x03: // 域名
					dLen := make([]byte, 1)
					_, _ = io.ReadFull(c, dLen)
					dBuf := make([]byte, int(dLen[0]))
					_, _ = io.ReadFull(c, dBuf)
					host = string(dBuf)
				case 0x04: // IPv6
					ip := make([]byte, 16)
					_, _ = io.ReadFull(c, ip)
					host = net.IP(ip).String()
				}
				pBuf := make([]byte, 2)
				_, _ = io.ReadFull(c, pBuf)
				port := binary.BigEndian.Uint16(pBuf)

				targetAddr := fmt.Sprintf("%s:%d", host, port)
				remote, err := net.DialTimeout("tcp", targetAddr, 3*time.Second)
				if err != nil {
					_, _ = c.Write([]byte{0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
					return
				}
				defer remote.Close()

				_, _ = c.Write([]byte{0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0x10, 0x00})

				// 双向流对拷
				var bridgeWg sync.WaitGroup
				bridgeWg.Add(2)
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(remote, c)
					_ = remote.Close()
				}()
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(c, remote)
					_ = c.Close()
				}()
				bridgeWg.Wait()
			}(conn)
		}
	}()

	teardown := func() {
		close(quit)
		_ = ln.Close()
		wg.Wait()
	}
	return ln.Addr().String(), teardown
}

// StartMockHttpConnectServer 启动本地 HTTP CONNECT Mock 仿真服务端
func StartMockHttpConnectServer(t *testing.T, username, password string) (string, func()) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("启动 Mock HTTP 失败: %v", err)
	}

	var wg sync.WaitGroup
	quit := make(chan struct{})

	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			wg.Add(1)
			go func(c net.Conn) {
				defer wg.Done()
				defer c.Close()

				reader := bufio.NewReader(c)
				reqLine, err := reader.ReadString('\n')
				if err != nil {
					return
				}
				parts := strings.Split(strings.TrimSpace(reqLine), " ")
				if len(parts) < 2 || parts[0] != "CONNECT" {
					_, _ = c.Write([]byte("HTTP/1.1 400 Bad Request\r\n\r\n"))
					return
				}
				targetAddr := parts[1]

				// 读 header 直至空行
				authHeaderFound := ""
				for {
					line, err := reader.ReadString('\n')
					if err != nil || line == "\r\n" || line == "\n" {
						break
					}
					if strings.HasPrefix(strings.ToLower(line), "proxy-authorization:") {
						authHeaderFound = strings.TrimSpace(line[len("proxy-authorization:"):])
					}
				}

				if username != "" {
					expected := "Basic " + base64.StdEncoding.EncodeToString([]byte(username+":"+password))
					if authHeaderFound != expected {
						_, _ = c.Write([]byte("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n"))
						return
					}
				}

				remote, err := net.DialTimeout("tcp", targetAddr, 3*time.Second)
				if err != nil {
					_, _ = c.Write([]byte("HTTP/1.1 502 Bad Gateway\r\n\r\n"))
					return
				}
				defer remote.Close()

				_, _ = c.Write([]byte("HTTP/1.1 200 Connection Established\r\n\r\n"))

				var bridgeWg sync.WaitGroup
				bridgeWg.Add(2)
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(remote, reader)
					_ = remote.Close()
				}()
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(c, remote)
					_ = c.Close()
				}()
				bridgeWg.Wait()
			}(conn)
		}
	}()

	teardown := func() {
		close(quit)
		_ = ln.Close()
		wg.Wait()
	}
	return ln.Addr().String(), teardown
}

// StartMockTrojanServer 启动本地 Trojan Mock 服务端 (TLS + SHA224 鉴权)
func StartMockTrojanServer(t *testing.T, password string) (string, func()) {
	tlsConfig, _, err := GenerateInMemoryTLSConfig("127.0.0.1")
	if err != nil {
		t.Fatalf("生成 TLS 失败: %v", err)
	}

	ln, err := tls.Listen("tcp", "127.0.0.1:0", tlsConfig)
	if err != nil {
		t.Fatalf("启动 Mock Trojan 失败: %v", err)
	}

	expectedHash := hex.EncodeToString(func() []byte {
		h := sha256.Sum224([]byte(password))
		return h[:]
	}())

	var wg sync.WaitGroup
	quit := make(chan struct{})

	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			wg.Add(1)
			go func(c net.Conn) {
				defer wg.Done()
				defer c.Close()

				reader := bufio.NewReader(c)
				line, err := reader.ReadString('\n')
				if err != nil {
					return
				}
				gotHash := strings.TrimSpace(line)
				if gotHash != expectedHash {
					return // 密码错误掐断
				}

				// CMD
				cmd, err := reader.ReadByte()
				if err != nil || cmd != 0x01 {
					return
				}

				// ATYP
				atyp, err := reader.ReadByte()
				if err != nil {
					return
				}
				var host string
				switch atyp {
				case 0x01: // IPv4
					ip := make([]byte, 4)
					_, _ = io.ReadFull(reader, ip)
					host = net.IP(ip).String()
				case 0x03: // 域名
					dLen, _ := reader.ReadByte()
					dBuf := make([]byte, int(dLen))
					_, _ = io.ReadFull(reader, dBuf)
					host = string(dBuf)
				case 0x04: // IPv6
					ip := make([]byte, 16)
					_, _ = io.ReadFull(reader, ip)
					host = net.IP(ip).String()
				}
				pBuf := make([]byte, 2)
				_, _ = io.ReadFull(reader, pBuf)
				port := binary.BigEndian.Uint16(pBuf)

				// CRLF
				_, _ = reader.ReadString('\n')

				targetAddr := fmt.Sprintf("%s:%d", host, port)
				remote, err := net.DialTimeout("tcp", targetAddr, 3*time.Second)
				if err != nil {
					return
				}
				defer remote.Close()

				var bridgeWg sync.WaitGroup
				bridgeWg.Add(2)
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(remote, reader)
					_ = remote.Close()
				}()
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(c, remote)
					_ = c.Close()
				}()
				bridgeWg.Wait()
			}(conn)
		}
	}()

	teardown := func() {
		close(quit)
		_ = ln.Close()
		wg.Wait()
	}
	return ln.Addr().String(), teardown
}

// StartMockVlessServer 启动本地 VLESS Mock 服务端 (TLS + UUID 校验)
func StartMockVlessServer(t *testing.T, uuidStr string) (string, func()) {
	tlsConfig, _, err := GenerateInMemoryTLSConfig("127.0.0.1")
	if err != nil {
		t.Fatalf("生成 TLS 失败: %v", err)
	}

	ln, err := tls.Listen("tcp", "127.0.0.1:0", tlsConfig)
	if err != nil {
		t.Fatalf("启动 Mock VLESS 失败: %v", err)
	}

	cleanUUID := strings.ReplaceAll(uuidStr, "-", "")
	expectedUUID, _ := hex.DecodeString(cleanUUID)

	var wg sync.WaitGroup
	quit := make(chan struct{})

	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			wg.Add(1)
			go func(c net.Conn) {
				defer wg.Done()
				defer c.Close()

				// 1 byte Version
				ver := make([]byte, 1)
				if _, err := io.ReadFull(c, ver); err != nil || ver[0] != 0x00 {
					return
				}

				// 16 bytes UUID
				gotUUID := make([]byte, 16)
				if _, err := io.ReadFull(c, gotUUID); err != nil {
					return
				}
				if string(gotUUID) != string(expectedUUID) {
					return // UUID 不匹配掐断
				}

				// 1 byte AddonsLen
				addonLen := make([]byte, 1)
				if _, err := io.ReadFull(c, addonLen); err != nil {
					return
				}
				if addonLen[0] > 0 {
					addonData := make([]byte, int(addonLen[0]))
					_, _ = io.ReadFull(c, addonData)
				}

				// 1 byte CMD
				cmd := make([]byte, 1)
				if _, err := io.ReadFull(c, cmd); err != nil || cmd[0] != 0x01 {
					return
				}

				// 2 bytes Port
				pBuf := make([]byte, 2)
				if _, err := io.ReadFull(c, pBuf); err != nil {
					return
				}
				port := binary.BigEndian.Uint16(pBuf)

				// 1 byte ATYP
				atyp := make([]byte, 1)
				if _, err := io.ReadFull(c, atyp); err != nil {
					return
				}
				var host string
				switch atyp[0] {
				case 0x01: // IPv4
					ip := make([]byte, 4)
					_, _ = io.ReadFull(c, ip)
					host = net.IP(ip).String()
				case 0x02: // 域名
					dLen := make([]byte, 1)
					_, _ = io.ReadFull(c, dLen)
					dBuf := make([]byte, int(dLen[0]))
					_, _ = io.ReadFull(c, dBuf)
					host = string(dBuf)
				case 0x03: // IPv6
					ip := make([]byte, 16)
					_, _ = io.ReadFull(c, ip)
					host = net.IP(ip).String()
				}

				targetAddr := fmt.Sprintf("%s:%d", host, port)
				remote, err := net.DialTimeout("tcp", targetAddr, 3*time.Second)
				if err != nil {
					return
				}
				defer remote.Close()

				// 回复 VLESS 响应头: version 0x00, addons len 0x00
				if _, err := c.Write([]byte{0x00, 0x00}); err != nil {
					return
				}

				var bridgeWg sync.WaitGroup
				bridgeWg.Add(2)
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(remote, c)
					_ = remote.Close()
				}()
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(c, remote)
					_ = c.Close()
				}()
				bridgeWg.Wait()
			}(conn)
		}
	}()

	teardown := func() {
		close(quit)
		_ = ln.Close()
		wg.Wait()
	}
	return ln.Addr().String(), teardown
}

// StartMockShadowsocksServer 启动本地 Shadowsocks AEAD Mock 服务端
func StartMockShadowsocksServer(t *testing.T, password string) (string, func()) {
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("启动 Mock SS 失败: %v", err)
	}

	rawKey := []byte(password)
	if len(rawKey) < 16 {
		padded := make([]byte, 16)
		copy(padded, rawKey)
		rawKey = padded
	} else if len(rawKey) > 16 && len(rawKey) < 32 {
		padded := make([]byte, 32)
		copy(padded, rawKey)
		rawKey = padded
	} else if len(rawKey) > 32 {
		rawKey = rawKey[:32]
	}

	var wg sync.WaitGroup
	quit := make(chan struct{})

	go func() {
		for {
			conn, err := ln.Accept()
			if err != nil {
				return
			}
			wg.Add(1)
			go func(c net.Conn) {
				defer wg.Done()
				defer c.Close()

				salt := make([]byte, len(rawKey))
				if _, err := io.ReadFull(c, salt); err != nil {
					return
				}

				block, err := aes.NewCipher(rawKey)
				if err != nil {
					return
				}
				gcm, err := cipher.NewGCM(block)
				if err != nil {
					return
				}

				lenBuf := make([]byte, 2)
				if _, err := io.ReadFull(c, lenBuf); err != nil {
					return
				}
				encLen := binary.BigEndian.Uint16(lenBuf)
				if encLen == 0 || encLen > 4096 {
					return
				}

				encrypted := make([]byte, encLen)
				if _, err := io.ReadFull(c, encrypted); err != nil {
					return
				}

				nonce := make([]byte, gcm.NonceSize())
				decrypted, err := gcm.Open(nil, nonce, encrypted, nil)
				if err != nil {
					return // 解密失败 (密码不正确)
				}

				if len(decrypted) < 3 {
					return
				}

				var host string
				var portOffset int
				switch decrypted[0] {
				case 0x01: // IPv4
					host = net.IP(decrypted[1:5]).String()
					portOffset = 5
				case 0x03: // 域名
					dLen := int(decrypted[1])
					host = string(decrypted[2 : 2+dLen])
					portOffset = 2 + dLen
				case 0x04: // IPv6
					host = net.IP(decrypted[1:17]).String()
					portOffset = 17
				}
				port := binary.BigEndian.Uint16(decrypted[portOffset : portOffset+2])

				targetAddr := fmt.Sprintf("%s:%d", host, port)
				remote, err := net.DialTimeout("tcp", targetAddr, 3*time.Second)
				if err != nil {
					return
				}
				defer remote.Close()

				var bridgeWg sync.WaitGroup
				bridgeWg.Add(2)
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(remote, c)
					_ = remote.Close()
				}()
				go func() {
					defer bridgeWg.Done()
					_, _ = io.Copy(c, remote)
					_ = c.Close()
				}()
				bridgeWg.Wait()
			}(conn)
		}
	}()

	teardown := func() {
		close(quit)
		_ = ln.Close()
		wg.Wait()
	}
	return ln.Addr().String(), teardown
}
