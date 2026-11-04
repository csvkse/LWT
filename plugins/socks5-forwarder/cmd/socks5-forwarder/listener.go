package main

import (
	"bufio"
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"log"
	"net"
	"strconv"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

// BufferedConn 包装带缓存的 Reader 与底层套接字
type BufferedConn struct {
	r io.Reader
	net.Conn
}

func (c *BufferedConn) Read(p []byte) (int, error) {
	return c.r.Read(p)
}

// InboundListener 组合入站监听实例（支持单规则绑定多端口、多协议嗅探、分流路由与多跳链式代理）
type InboundListener struct {
	profile    InboundProfile
	listeners  []net.Listener
	acl        *ACLManager
	dispatcher *RuleRoutingDispatcher
	mgr        *ProfileManager
	ctx        context.Context
	cancel     context.CancelFunc
	wg         sync.WaitGroup
	rrCounter  uint64
	isStopping bool
	mu         sync.Mutex
}

// NewInboundListener 实例化入站监听器
func NewInboundListener(p InboundProfile, mgr *ProfileManager) *InboundListener {
	ctx, cancel := context.WithCancel(context.Background())
	return &InboundListener{
		profile:    p,
		listeners:  make([]net.Listener, 0),
		acl:        NewACLManager(p.AllowedCIDR),
		dispatcher: NewRuleRoutingDispatcher(p.RoutePolicy),
		mgr:        mgr,
		ctx:        ctx,
		cancel:     cancel,
	}
}

// Start 启动 TCP 监听套接字（主端口 + 所有附加端口）并开始循环接收客户端连接
func (l *InboundListener) Start() error {
	ports := l.profile.GetEffectivePorts()
	if len(ports) == 0 {
		return fmt.Errorf("入站规则未配置任何有效监听端口")
	}

	l.listeners = make([]net.Listener, 0, len(ports))
	for _, port := range ports {
		addr := fmt.Sprintf("0.0.0.0:%d", port)
		ln, err := net.Listen("tcp", addr)
		if err != nil {
			// 回滚已启动的 listener
			for _, prev := range l.listeners {
				_ = prev.Close()
			}
			l.listeners = nil
			return fmt.Errorf("绑定端口 :%d 失败: %w", port, err)
		}

		l.listeners = append(l.listeners, ln)
		log.Printf("[Listener:%s] 成功监听在 %s (协议: %s, ACL 规则数: %d, 分流规则数: %d)",
			l.profile.Name, addr, l.profile.Protocol, len(l.profile.AllowedCIDR), len(l.profile.RoutePolicy.Rules))

		l.wg.Add(1)
		go l.acceptLoop(ln, port)
	}

	return nil
}

func (l *InboundListener) acceptLoop(ln net.Listener, port int) {
	defer l.wg.Done()

	for {
		conn, err := ln.Accept()
		if err != nil {
			l.mu.Lock()
			stopping := l.isStopping
			l.mu.Unlock()
			if stopping {
				return
			}
			log.Printf("[Listener:%s] 端口 :%d Accept 异常: %v", l.profile.Name, port, err)
			return
		}

		// 维度二防护：握手前立即核验入站客户端 IP
		if !l.acl.IsAllowed(conn.RemoteAddr()) {
			log.Printf("[Listener:%s] 端口 :%d 拒绝非白名单客户端连接: %s", l.profile.Name, port, conn.RemoteAddr())
			_ = conn.Close() // 0 字节掐断，消除指纹
			continue
		}

		go l.handleClientConn(conn)
	}
}

func (l *InboundListener) handleClientConn(clientConn net.Conn) {
	defer clientConn.Close()

	// 追踪活跃连接数
	atomic.AddInt64(&l.profile.ActiveConns, 1)
	defer atomic.AddInt64(&l.profile.ActiveConns, -1)

	r := bufio.NewReader(clientConn)
	firstByte, err := r.Peek(1)
	if err != nil {
		return
	}

	dialCtx, dialCancel := context.WithTimeout(l.ctx, 8*time.Second)
	defer dialCancel()

	// 协议嗅探 1: SOCKS5 握手
	if firstByte[0] == 0x05 {
		l.handleSocks5Conn(dialCtx, clientConn, r)
		return
	}

	// 协议嗅探 2: HTTP CONNECT 握手
	peekBuf, _ := r.Peek(8)
	if strings.HasPrefix(string(peekBuf), "CONNECT ") {
		l.handleHttpConnectConn(dialCtx, clientConn, r)
		return
	}

	// 协议嗅探 3: 原始四层透传 (Legacy L4 fallback)
	l.handleLegacyRawConn(dialCtx, clientConn, r)
}

func (l *InboundListener) handleSocks5Conn(ctx context.Context, clientConn net.Conn, r *bufio.Reader) {
	// 1. 读取 SOCKS5 认证方法
	ver, err := r.ReadByte()
	if err != nil || ver != 0x05 {
		return
	}
	nmethods, err := r.ReadByte()
	if err != nil {
		return
	}
	methods := make([]byte, nmethods)
	if _, err := io.ReadFull(r, methods); err != nil {
		return
	}

	// 2. 响应 NO AUTH (0x00)
	if _, err := clientConn.Write([]byte{0x05, 0x00}); err != nil {
		return
	}

	// 3. 读取 CONNECT 请求
	reqHeader := make([]byte, 4)
	if _, err := io.ReadFull(r, reqHeader); err != nil {
		return
	}
	if reqHeader[0] != 0x05 || reqHeader[1] != 0x01 { // 仅支持 CONNECT (0x01)
		_, _ = clientConn.Write([]byte{0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}

	var targetHost string
	switch reqHeader[3] {
	case 0x01: // IPv4
		ip := make([]byte, 4)
		if _, err := io.ReadFull(r, ip); err != nil {
			return
		}
		targetHost = net.IP(ip).String()
	case 0x03: // 域名
		dLen, err := r.ReadByte()
		if err != nil {
			return
		}
		dBuf := make([]byte, dLen)
		if _, err := io.ReadFull(r, dBuf); err != nil {
			return
		}
		targetHost = string(dBuf)
	case 0x04: // IPv6
		ip := make([]byte, 16)
		if _, err := io.ReadFull(r, ip); err != nil {
			return
		}
		targetHost = net.IP(ip).String()
	default:
		_, _ = clientConn.Write([]byte{0x05, 0x08, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}

	portBuf := make([]byte, 2)
	if _, err := io.ReadFull(r, portBuf); err != nil {
		return
	}
	targetPort := int(binary.BigEndian.Uint16(portBuf))
	targetAddr := net.JoinHostPort(targetHost, strconv.Itoa(targetPort))

	// 4. 责任链规则分流判定
	meta := &SessionMetadata{
		Host:       targetHost,
		Port:       targetPort,
		Network:    "tcp",
		SourceAddr: clientConn.RemoteAddr(),
	}
	action, target := l.dispatcher.Route(meta)

	// 5. 执行路由动作
	switch action {
	case "reject":
		_, _ = clientConn.Write([]byte{0x05, 0x02, 0x00, 0x01, 0, 0, 0, 0, 0, 0}) // Connection not allowed by ruleset
		return

	case "direct":
		dialer := &net.Dialer{Timeout: 8 * time.Second}
		directConn, err := dialer.DialContext(ctx, "tcp", targetAddr)
		if err != nil {
			_, _ = clientConn.Write([]byte{0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0}) // Host unreachable
			return
		}
		defer directConn.Close()

		_, _ = clientConn.Write([]byte{0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		wrappedClient := &BufferedConn{r: r, Conn: clientConn}
		BridgeDuplexStream(wrappedClient, directConn, &l.profile.TotalBytesTx, &l.profile.TotalBytesRx)
		return

	case "chain":
		chainCfg, found := l.mgr.GetChain(target)
		if !found {
			l.dialProxyAndBridge(ctx, clientConn, r, targetAddr, target)
			return
		}
		var hopDialers []IOutboundDialer
		var hopNodes []UpstreamNode
		for _, hopID := range chainCfg.Hops {
			if node, ok := l.mgr.GetUpstreamNode(hopID); ok {
				d, err := GlobalOutboundRegistry.CreateDialer(*node)
				if err == nil {
					hopDialers = append(hopDialers, d)
					hopNodes = append(hopNodes, *node)
				}
			}
		}
		if len(hopDialers) == 0 {
			_, _ = clientConn.Write([]byte{0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
			return
		}
		chained := NewChainedCompositeOutbound(chainCfg.ID, chainCfg.Name, hopDialers, hopNodes)
		outConn, err := chained.DialContext(ctx, "tcp", targetAddr)
		if err != nil {
			_, _ = clientConn.Write([]byte{0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
			return
		}
		defer outConn.Close()

		_, _ = clientConn.Write([]byte{0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		wrappedClient := &BufferedConn{r: r, Conn: clientConn}
		BridgeDuplexStream(wrappedClient, outConn, &l.profile.TotalBytesTx, &l.profile.TotalBytesRx)
		return

	case "proxy":
		fallthrough
	default:
		l.dialProxyAndBridge(ctx, clientConn, r, targetAddr, target)
		return
	}
}

func (l *InboundListener) dialProxyAndBridge(ctx context.Context, clientConn net.Conn, r *bufio.Reader, targetAddr, targetUpstreamID string) {
	var node *UpstreamNode
	if targetUpstreamID != "" {
		if n, ok := l.mgr.GetUpstreamNode(targetUpstreamID); ok {
			node = n
		}
	}
	if node == nil {
		var err error
		node, err = l.mgr.SelectUpstreamForProfile(l.profile, &l.rrCounter)
		if err != nil {
			_, _ = clientConn.Write([]byte{0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
			return
		}
	}

	dialer, err := GlobalOutboundRegistry.CreateDialer(*node)
	if err != nil {
		_, _ = clientConn.Write([]byte{0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}

	upstreamConn, err := dialer.DialContext(ctx, "tcp", targetAddr)
	if err != nil {
		_, _ = clientConn.Write([]byte{0x05, 0x04, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}
	defer upstreamConn.Close()

	_, _ = clientConn.Write([]byte{0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
	wrappedClient := &BufferedConn{r: r, Conn: clientConn}
	BridgeDuplexStream(wrappedClient, upstreamConn, &l.profile.TotalBytesTx, &l.profile.TotalBytesRx)
}

func (l *InboundListener) handleHttpConnectConn(ctx context.Context, clientConn net.Conn, r *bufio.Reader) {
	line, err := r.ReadString('\n')
	if err != nil {
		return
	}
	parts := strings.Fields(line)
	if len(parts) < 2 || parts[0] != "CONNECT" {
		return
	}
	targetAddr := parts[1]
	for {
		headerLine, err := r.ReadString('\n')
		if err != nil || headerLine == "\r\n" || headerLine == "\n" {
			break
		}
	}

	targetHost, targetPortStr, _ := net.SplitHostPort(targetAddr)
	targetPort, _ := strconv.Atoi(targetPortStr)
	if targetPort <= 0 {
		targetPort = 443
	}

	meta := &SessionMetadata{
		Host:       targetHost,
		Port:       targetPort,
		Network:    "tcp",
		SourceAddr: clientConn.RemoteAddr(),
	}
	action, target := l.dispatcher.Route(meta)

	var outConn net.Conn
	switch action {
	case "reject":
		_, _ = clientConn.Write([]byte("HTTP/1.1 403 Forbidden\r\n\r\n"))
		return
	case "direct":
		dialer := &net.Dialer{Timeout: 8 * time.Second}
		outConn, err = dialer.DialContext(ctx, "tcp", targetAddr)
	case "proxy":
		fallthrough
	default:
		var node *UpstreamNode
		if target != "" {
			node, _ = l.mgr.GetUpstreamNode(target)
		}
		if node == nil {
			node, _ = l.mgr.SelectUpstreamForProfile(l.profile, &l.rrCounter)
		}
		if node != nil {
			dialer, dErr := GlobalOutboundRegistry.CreateDialer(*node)
			if dErr == nil {
				outConn, err = dialer.DialContext(ctx, "tcp", targetAddr)
			}
		}
	}

	if err != nil || outConn == nil {
		_, _ = clientConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\n\r\n"))
		return
	}
	defer outConn.Close()

	_, _ = clientConn.Write([]byte("HTTP/1.1 200 Connection Established\r\n\r\n"))
	wrappedClient := &BufferedConn{r: r, Conn: clientConn}
	BridgeDuplexStream(wrappedClient, outConn, &l.profile.TotalBytesTx, &l.profile.TotalBytesRx)
}

func (l *InboundListener) handleLegacyRawConn(ctx context.Context, clientConn net.Conn, r *bufio.Reader) {
	node, err := l.mgr.SelectUpstreamForProfile(l.profile, &l.rrCounter)
	if err != nil {
		log.Printf("[Listener:%s] 调度上游失败: %v", l.profile.Name, err)
		return
	}

	dialer, err := GlobalOutboundRegistry.CreateDialer(*node)
	if err != nil {
		log.Printf("[Listener:%s] 创建出站拨号器失败: %v", l.profile.Name, err)
		return
	}

	upstreamConn, err := dialer.DialContext(ctx, "tcp", "")
	if err != nil {
		log.Printf("[Listener:%s] 连接上游 [%s] 失败: %v", l.profile.Name, node.Name, err)
		return
	}
	defer upstreamConn.Close()

	wrappedClient := &BufferedConn{r: r, Conn: clientConn}
	BridgeDuplexStream(wrappedClient, upstreamConn, &l.profile.TotalBytesTx, &l.profile.TotalBytesRx)
}

// Stop 优雅关闭监听器并断开连接
func (l *InboundListener) Stop() {
	l.mu.Lock()
	if l.isStopping {
		l.mu.Unlock()
		return
	}
	l.isStopping = true
	l.mu.Unlock()

	l.cancel()
	for _, ln := range l.listeners {
		if ln != nil {
			_ = ln.Close()
		}
	}
	l.wg.Wait()
	log.Printf("[Listener:%s] 已停止监听所有端口: %v", l.profile.Name, l.profile.GetEffectivePorts())
}
