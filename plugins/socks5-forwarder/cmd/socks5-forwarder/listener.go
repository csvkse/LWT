package main

import (
	"context"
	"fmt"
	"log"
	"net"
	"sync"
	"sync/atomic"
	"time"
)

// InboundListener 组合入站监听实例（支持单规则绑定多端口）
type InboundListener struct {
	profile    InboundProfile
	listeners  []net.Listener
	acl        *ACLManager
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
		profile:   p,
		listeners: make([]net.Listener, 0),
		acl:       NewACLManager(p.AllowedCIDR),
		mgr:       mgr,
		ctx:       ctx,
		cancel:    cancel,
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
		log.Printf("[Listener:%s] 成功监听在 %s (协议: %s, ACL 规则数: %d)",
			l.profile.Name, addr, l.profile.Protocol, len(l.profile.AllowedCIDR))

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

	// 根据负载策略挑选上游服务器
	node, err := l.mgr.SelectUpstreamForProfile(l.profile, &l.rrCounter)
	if err != nil {
		log.Printf("[Listener:%s] 调度上游失败: %v", l.profile.Name, err)
		return
	}

	// 建立与远端 LWT Ingress 的 WSS 伪装加密隧道
	dialCtx, dialCancel := context.WithTimeout(l.ctx, 8*time.Second)
	defer dialCancel()

	upstreamConn, err := DialWebSocket(dialCtx, node.URL, node.Token, "lwt-token.", node.SkipTLSVerify, node.SNI)
	if err != nil {
		log.Printf("[Listener:%s] 连接上游 [%s] 失败: %v", l.profile.Name, node.Name, err)
		return
	}
	defer upstreamConn.Close()

	// 全双工双向对拷流
	BridgeDuplexStream(clientConn, upstreamConn, &l.profile.TotalBytesTx, &l.profile.TotalBytesRx)
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
