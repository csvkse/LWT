package main

import (
	"context"
	"encoding/base64"
	"fmt"
	"io"
	"net"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// -----------------------------------------------------------------------------
// 标准 SOCKS5 出站适配器 (原生纯 Go RFC 1928 实现)
// -----------------------------------------------------------------------------

type Socks5Outbound struct {
	node UpstreamNode
	host string
	port int
}

func NewSocks5Outbound(node UpstreamNode) (IOutboundDialer, error) {
	host, portStr := node.Server, strconv.Itoa(node.Port)
	if node.URL != "" && strings.Contains(node.URL, "://") {
		u, err := url.Parse(node.URL)
		if err == nil {
			host = u.Hostname()
			portStr = u.Port()
			if u.User != nil {
				if node.Username == "" {
					node.Username = u.User.Username()
				}
				if node.Password == "" {
					pwd, _ := u.User.Password()
					node.Password = pwd
				}
			}
		}
	}
	port, _ := strconv.Atoi(portStr)
	if port <= 0 {
		port = 1080
	}
	if host == "" {
		host = "127.0.0.1"
	}

	return &Socks5Outbound{
		node: node,
		host: host,
		port: port,
	}, nil
}

func (d *Socks5Outbound) Protocol() string { return "socks5" }

func (d *Socks5Outbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	dialer := &net.Dialer{Timeout: 8 * time.Second}
	serverAddr := fmt.Sprintf("%s:%d", d.host, d.port)
	conn, err := dialer.DialContext(ctx, "tcp", serverAddr)
	if err != nil {
		return nil, fmt.Errorf("连接 SOCKS5 代理 [%s] 失败: %w", serverAddr, err)
	}

	return d.DialThrough(ctx, conn, targetAddr)
}

func (d *Socks5Outbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	// 1. 发送握手认证协商
	if d.node.Username != "" {
		// 支持匿名 (0x00) 与用户名密码 (0x02)
		if _, err := baseConn.Write([]byte{0x05, 0x02, 0x00, 0x02}); err != nil {
			_ = baseConn.Close()
			return nil, fmt.Errorf("SOCKS5 认证协商写入失败: %w", err)
		}
	} else {
		// 仅匿名 (0x00)
		if _, err := baseConn.Write([]byte{0x05, 0x01, 0x00}); err != nil {
			_ = baseConn.Close()
			return nil, fmt.Errorf("SOCKS5 握手写入失败: %w", err)
		}
	}

	// 2. 读取服务端选择的认证方法
	authResp := make([]byte, 2)
	if _, err := io.ReadFull(baseConn, authResp); err != nil {
		_ = baseConn.Close()
		return nil, fmt.Errorf("读取 SOCKS5 协商响应失败: %w", err)
	}

	if authResp[0] != 0x05 {
		_ = baseConn.Close()
		return nil, fmt.Errorf("SOCKS5 协议版本错误: 0x%02x", authResp[0])
	}

	// 3. 处理用户名密码认证 (RFC 1929)
	if authResp[1] == 0x02 {
		userBytes := []byte(d.node.Username)
		passBytes := []byte(d.node.Password)
		authReq := make([]byte, 0, 3+len(userBytes)+len(passBytes))
		authReq = append(authReq, 0x01, byte(len(userBytes)))
		authReq = append(authReq, userBytes...)
		authReq = append(authReq, byte(len(passBytes)))
		authReq = append(authReq, passBytes...)

		if _, err := baseConn.Write(authReq); err != nil {
			_ = baseConn.Close()
			return nil, fmt.Errorf("写入 SOCKS5 用户凭据失败: %w", err)
		}

		userAuthResp := make([]byte, 2)
		if _, err := io.ReadFull(baseConn, userAuthResp); err != nil {
			_ = baseConn.Close()
			return nil, fmt.Errorf("读取 SOCKS5 用户凭据响应失败: %w", err)
		}
		if userAuthResp[1] != 0x00 {
			_ = baseConn.Close()
			return nil, fmt.Errorf("SOCKS5 认证失败: 凭据不正确 (0x%02x)", userAuthResp[1])
		}
	} else if authResp[1] != 0x00 {
		_ = baseConn.Close()
		return nil, fmt.Errorf("不支持的 SOCKS5 认证方法: 0x%02x", authResp[1])
	}

	// 4. 发送 CONNECT 请求
	targetHost, targetPortStr, err := net.SplitHostPort(targetAddr)
	if err != nil {
		_ = baseConn.Close()
		return nil, fmt.Errorf("解析目标地址失败 [%s]: %w", targetAddr, err)
	}
	targetPort, _ := strconv.Atoi(targetPortStr)

	req := []byte{0x05, 0x01, 0x00}
	ip := net.ParseIP(targetHost)
	if ip4 := ip.To4(); ip4 != nil {
		req = append(req, 0x01)
		req = append(req, ip4...)
	} else if ip6 := ip.To16(); ip6 != nil {
		req = append(req, 0x04)
		req = append(req, ip6...)
	} else {
		req = append(req, 0x03, byte(len(targetHost)))
		req = append(req, []byte(targetHost)...)
	}

	portBuf := make([]byte, 2)
	portBuf[0] = byte(targetPort >> 8)
	portBuf[1] = byte(targetPort & 0xff)
	req = append(req, portBuf...)

	if _, err := baseConn.Write(req); err != nil {
		_ = baseConn.Close()
		return nil, fmt.Errorf("写入 SOCKS5 CONNECT 请求失败: %w", err)
	}

	// 5. 读取 CONNECT 响应
	header := make([]byte, 4)
	if _, err := io.ReadFull(baseConn, header); err != nil {
		_ = baseConn.Close()
		return nil, fmt.Errorf("读取 SOCKS5 CONNECT 响应头失败: %w", err)
	}
	if header[1] != 0x00 {
		_ = baseConn.Close()
		return nil, fmt.Errorf("SOCKS5 CONNECT 请求被拒绝，状态码: 0x%02x", header[1])
	}

	// 跳过绑定的地址与端口
	var addrLen int
	switch header[3] {
	case 0x01: // IPv4
		addrLen = 4
	case 0x04: // IPv6
		addrLen = 16
	case 0x03: // 域名
		dLen := make([]byte, 1)
		if _, err := io.ReadFull(baseConn, dLen); err != nil {
			_ = baseConn.Close()
			return nil, err
		}
		addrLen = int(dLen[0])
	default:
		_ = baseConn.Close()
		return nil, fmt.Errorf("SOCKS5 未知绑定地址类型: 0x%02x", header[3])
	}
	skipBuf := make([]byte, addrLen+2) // 地址 + 2字节端口
	if _, err := io.ReadFull(baseConn, skipBuf); err != nil {
		_ = baseConn.Close()
		return nil, err
	}

	return baseConn, nil
}

// -----------------------------------------------------------------------------
// HTTP / HTTPS CONNECT 出站适配器 (原生纯 Go 实现)
// -----------------------------------------------------------------------------

type HttpConnectOutbound struct {
	node UpstreamNode
	host string
	port int
}

func NewHttpConnectOutbound(node UpstreamNode) (IOutboundDialer, error) {
	host, portStr := node.Server, strconv.Itoa(node.Port)
	if node.URL != "" && strings.Contains(node.URL, "://") {
		u, err := url.Parse(node.URL)
		if err == nil {
			host = u.Hostname()
			portStr = u.Port()
			if u.User != nil {
				if node.Username == "" {
					node.Username = u.User.Username()
				}
				if node.Password == "" {
					pwd, _ := u.User.Password()
					node.Password = pwd
				}
			}
		}
	}
	port, _ := strconv.Atoi(portStr)
	if port <= 0 {
		port = 8080
	}
	if host == "" {
		host = "127.0.0.1"
	}

	return &HttpConnectOutbound{
		node: node,
		host: host,
		port: port,
	}, nil
}

func (d *HttpConnectOutbound) Protocol() string { return "http" }

func (d *HttpConnectOutbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	dialer := &net.Dialer{Timeout: 8 * time.Second}
	serverAddr := fmt.Sprintf("%s:%d", d.host, d.port)
	conn, err := dialer.DialContext(ctx, "tcp", serverAddr)
	if err != nil {
		return nil, fmt.Errorf("连接 HTTP 代理 [%s] 失败: %w", serverAddr, err)
	}

	return d.DialThrough(ctx, conn, targetAddr)
}

func (d *HttpConnectOutbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	req := fmt.Sprintf("CONNECT %s HTTP/1.1\r\nHost: %s\r\nProxy-Connection: Keep-Alive\r\n", targetAddr, targetAddr)
	if d.node.Username != "" {
		rawAuth := fmt.Sprintf("%s:%s", d.node.Username, d.node.Password)
		encoded := base64.StdEncoding.EncodeToString([]byte(rawAuth))
		req += fmt.Sprintf("Proxy-Authorization: Basic %s\r\n", encoded)
	}
	req += "\r\n"

	if _, err := baseConn.Write([]byte(req)); err != nil {
		_ = baseConn.Close()
		return nil, fmt.Errorf("发送 HTTP CONNECT 请求失败: %w", err)
	}

	buf := make([]byte, 1024)
	n, err := baseConn.Read(buf)
	if err != nil {
		_ = baseConn.Close()
		return nil, fmt.Errorf("读取 HTTP CONNECT 响应失败: %w", err)
	}

	respStr := string(buf[:n])
	if !strings.Contains(respStr, "200") {
		_ = baseConn.Close()
		return nil, fmt.Errorf("HTTP CONNECT 被拒绝: %s", strings.TrimSpace(respStr))
	}

	return baseConn, nil
}
