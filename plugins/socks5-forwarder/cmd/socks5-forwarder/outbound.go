package main

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net"
	"strconv"
	"strings"
	"sync"
	"time"
)

// IOutboundDialer 抽象所有出站协议的统一拨号行为
type IOutboundDialer interface {
	Protocol() string
	// DialContext 建立直达目标的出站连接
	DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error)
	// DialThrough 基于现有流进行下一级协议封装握手（链式代理核心）
	DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error)
}

// OutboundFactory 出站适配器构造函数签名
type OutboundFactory func(node UpstreamNode) (IOutboundDialer, error)

// OutboundRegistry 线程安全的出站适配器注册表
type OutboundRegistry struct {
	mu        sync.RWMutex
	factories map[string]OutboundFactory
}

// GlobalOutboundRegistry 全局出站注册表
var GlobalOutboundRegistry = NewOutboundRegistry()

func NewOutboundRegistry() *OutboundRegistry {
	r := &OutboundRegistry{
		factories: make(map[string]OutboundFactory),
	}
	// 默认注册出站协议
	r.Register("lwt_wss", NewLwtWssOutbound)
	r.Register("socks5", NewSocks5Outbound)
	r.Register("http", NewHttpConnectOutbound)
	r.Register("trojan", NewTrojanOutbound)
	r.Register("vless", NewVlessOutbound)
	r.Register("shadowsocks", NewShadowsocksOutbound)
	r.Register("direct", NewDirectOutbound)
	r.Register("reject", NewRejectOutbound)
	return r
}

func (r *OutboundRegistry) Register(protocol string, factory OutboundFactory) {
	r.mu.Lock()
	defer r.mu.Unlock()
	r.factories[strings.ToLower(protocol)] = factory
}

func (r *OutboundRegistry) CreateDialer(node UpstreamNode) (IOutboundDialer, error) {
	r.mu.RLock()
	defer r.mu.RUnlock()

	proto := strings.ToLower(node.Type)
	if proto == "" {
		// 根据 URL 智能推断
		if strings.HasPrefix(node.URL, "ws://") || strings.HasPrefix(node.URL, "wss://") {
			proto = "lwt_wss"
		} else if strings.HasPrefix(node.URL, "socks5://") {
			proto = "socks5"
		} else if strings.HasPrefix(node.URL, "http://") || strings.HasPrefix(node.URL, "https://") {
			proto = "http"
		} else if strings.HasPrefix(node.URL, "trojan://") {
			proto = "trojan"
		} else if strings.HasPrefix(node.URL, "vless://") {
			proto = "vless"
		} else if strings.HasPrefix(node.URL, "ss://") {
			proto = "shadowsocks"
		} else {
			proto = "lwt_wss"
		}
	}

	factory, exists := r.factories[proto]
	if !exists {
		return nil, fmt.Errorf("不支持的出站协议类型: '%s'", proto)
	}
	return factory(node)
}

// -----------------------------------------------------------------------------
// 1. LWT WSS Ingress 伪装隧道出站
// -----------------------------------------------------------------------------

type LwtWssOutbound struct {
	node UpstreamNode
}

func NewLwtWssOutbound(node UpstreamNode) (IOutboundDialer, error) {
	return &LwtWssOutbound{node: node}, nil
}

func (d *LwtWssOutbound) Protocol() string { return "lwt_wss" }

func (d *LwtWssOutbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	conn, err := DialWebSocket(ctx, d.node.URL, d.node.Token, "lwt-tunnel-protocol-v1", d.node.SkipTLSVerify, d.node.SNI)
	if err != nil {
		return nil, fmt.Errorf("建立 LWT WSS 隧道失败: %w", err)
	}

	// 执行目标握手
	targetHost, targetPortStr, err := net.SplitHostPort(targetAddr)
	if err != nil {
		_ = conn.Close()
		return nil, fmt.Errorf("解析目标地址失败: %w", err)
	}
	targetPort, _ := strconv.Atoi(targetPortStr)

	// SOCKS5 握手认证与请求
	if _, err := conn.Write([]byte{0x05, 0x01, 0x00}); err != nil {
		_ = conn.Close()
		return nil, err
	}
	authResp := make([]byte, 2)
	if _, err := io.ReadFull(conn, authResp); err != nil {
		_ = conn.Close()
		return nil, err
	}

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
	portBytes := make([]byte, 2)
	portBytes[0] = byte(targetPort >> 8)
	portBytes[1] = byte(targetPort & 0xff)
	req = append(req, portBytes...)

	if _, err := conn.Write(req); err != nil {
		_ = conn.Close()
		return nil, err
	}

	resp := make([]byte, 4)
	if _, err := io.ReadFull(conn, resp); err != nil {
		_ = conn.Close()
		return nil, err
	}
	if resp[1] != 0x00 {
		_ = conn.Close()
		return nil, fmt.Errorf("LWT WSS 隧道拒绝代理请求 (0x%02x)", resp[1])
	}

	var skipLen int
	switch resp[3] {
	case 0x01:
		skipLen = 4
	case 0x04:
		skipLen = 16
	case 0x03:
		dLen := make([]byte, 1)
		if _, err := io.ReadFull(conn, dLen); err != nil {
			_ = conn.Close()
			return nil, err
		}
		skipLen = int(dLen[0])
	}
	skipBuf := make([]byte, skipLen+2)
	if _, err := io.ReadFull(conn, skipBuf); err != nil {
		_ = conn.Close()
		return nil, err
	}

	return conn, nil
}

func (d *LwtWssOutbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	// WSS 隧道目前直接基于底层物理流升级
	return d.DialContext(ctx, "tcp", targetAddr)
}

// -----------------------------------------------------------------------------
// 2. Direct 直连出站
// -----------------------------------------------------------------------------

type DirectOutbound struct{}

func NewDirectOutbound(node UpstreamNode) (IOutboundDialer, error) {
	return &DirectOutbound{}, nil
}

func (d *DirectOutbound) Protocol() string { return "direct" }

func (d *DirectOutbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	dialer := &net.Dialer{Timeout: 8 * time.Second}
	return dialer.DialContext(ctx, network, targetAddr)
}

func (d *DirectOutbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	return baseConn, nil
}

// -----------------------------------------------------------------------------
// 3. Reject 阻断出站
// -----------------------------------------------------------------------------

type RejectOutbound struct{}

func NewRejectOutbound(node UpstreamNode) (IOutboundDialer, error) {
	return &RejectOutbound{}, nil
}

func (d *RejectOutbound) Protocol() string { return "reject" }

func (d *RejectOutbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	return nil, errors.New("connection blocked by reject rule")
}

func (d *RejectOutbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	_ = baseConn.Close()
	return nil, errors.New("connection blocked by reject rule")
}
