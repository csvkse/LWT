package main

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/sha256"
	"fmt"
	"io"
	"net"
	"strconv"
	"sync"
	"testing"
	"time"
)

// IProtocolTestFixture 抽象任意协议在测试环境下的行为
type IProtocolTestFixture interface {
	Protocol() string
	StartServer(t *testing.T) (serverAddr string, teardown func())
	BuildClientConfig(serverAddr string) UpstreamNode
	BuildInvalidClientConfig(serverAddr string) UpstreamNode
	SupportsChaining() bool
}

// -----------------------------------------------------------------------------
// 各协议的具体 Fixture 实现
// -----------------------------------------------------------------------------

type Socks5Fixture struct{}

func (f *Socks5Fixture) Protocol() string { return "socks5" }
func (f *Socks5Fixture) SupportsChaining() bool { return true }
func (f *Socks5Fixture) StartServer(t *testing.T) (string, func()) {
	return StartMockSocks5Server(t, "user-test", "pass-secret")
}
func (f *Socks5Fixture) BuildClientConfig(serverAddr string) UpstreamNode {
	host, portStr, _ := net.SplitHostPort(serverAddr)
	port, _ := strconv.Atoi(portStr)
	return UpstreamNode{
		ID:       "test-socks5",
		Name:     "Mock SOCKS5",
		Type:     "socks5",
		Server:   host,
		Port:     port,
		Username: "user-test",
		Password: "pass-secret",
	}
}
func (f *Socks5Fixture) BuildInvalidClientConfig(serverAddr string) UpstreamNode {
	cfg := f.BuildClientConfig(serverAddr)
	cfg.Password = "wrong-password"
	return cfg
}

type HttpConnectFixture struct{}

func (f *HttpConnectFixture) Protocol() string { return "http" }
func (f *HttpConnectFixture) SupportsChaining() bool { return true }
func (f *HttpConnectFixture) StartServer(t *testing.T) (string, func()) {
	return StartMockHttpConnectServer(t, "http-user", "http-pass")
}
func (f *HttpConnectFixture) BuildClientConfig(serverAddr string) UpstreamNode {
	host, portStr, _ := net.SplitHostPort(serverAddr)
	port, _ := strconv.Atoi(portStr)
	return UpstreamNode{
		ID:       "test-http",
		Name:     "Mock HTTP",
		Type:     "http",
		Server:   host,
		Port:     port,
		Username: "http-user",
		Password: "http-pass",
	}
}
func (f *HttpConnectFixture) BuildInvalidClientConfig(serverAddr string) UpstreamNode {
	cfg := f.BuildClientConfig(serverAddr)
	cfg.Password = "wrong-password"
	return cfg
}

type TrojanFixture struct{}

func (f *TrojanFixture) Protocol() string { return "trojan" }
func (f *TrojanFixture) SupportsChaining() bool { return true }
func (f *TrojanFixture) StartServer(t *testing.T) (string, func()) {
	return StartMockTrojanServer(t, "trojan-pass-secret")
}
func (f *TrojanFixture) BuildClientConfig(serverAddr string) UpstreamNode {
	host, portStr, _ := net.SplitHostPort(serverAddr)
	port, _ := strconv.Atoi(portStr)
	return UpstreamNode{
		ID:            "test-trojan",
		Name:          "Mock Trojan",
		Type:          "trojan",
		Server:        host,
		Port:          port,
		Password:      "trojan-pass-secret",
		SkipTLSVerify: true,
	}
}
func (f *TrojanFixture) BuildInvalidClientConfig(serverAddr string) UpstreamNode {
	cfg := f.BuildClientConfig(serverAddr)
	cfg.Password = "wrong-trojan-pass"
	return cfg
}

type VlessFixture struct{}

func (f *VlessFixture) Protocol() string { return "vless" }
func (f *VlessFixture) SupportsChaining() bool { return true }
func (f *VlessFixture) StartServer(t *testing.T) (string, func()) {
	return StartMockVlessServer(t, "a1b2c3d4-e5f6-7890-1234-56789abcdef0")
}
func (f *VlessFixture) BuildClientConfig(serverAddr string) UpstreamNode {
	host, portStr, _ := net.SplitHostPort(serverAddr)
	port, _ := strconv.Atoi(portStr)
	return UpstreamNode{
		ID:            "test-vless",
		Name:          "Mock VLESS",
		Type:          "vless",
		Server:        host,
		Port:          port,
		UUID:          "a1b2c3d4-e5f6-7890-1234-56789abcdef0",
		SkipTLSVerify: true,
	}
}
func (f *VlessFixture) BuildInvalidClientConfig(serverAddr string) UpstreamNode {
	cfg := f.BuildClientConfig(serverAddr)
	cfg.UUID = "ffffffff-ffff-ffff-ffff-ffffffffffff"
	return cfg
}

type ShadowsocksFixture struct{}

func (f *ShadowsocksFixture) Protocol() string { return "shadowsocks" }
func (f *ShadowsocksFixture) SupportsChaining() bool { return true }
func (f *ShadowsocksFixture) StartServer(t *testing.T) (string, func()) {
	return StartMockShadowsocksServer(t, "ss-secret-key-16b")
}
func (f *ShadowsocksFixture) BuildClientConfig(serverAddr string) UpstreamNode {
	host, portStr, _ := net.SplitHostPort(serverAddr)
	port, _ := strconv.Atoi(portStr)
	return UpstreamNode{
		ID:       "test-shadowsocks",
		Name:     "Mock Shadowsocks",
		Type:     "shadowsocks",
		Server:   host,
		Port:     port,
		Password: "ss-secret-key-16b",
	}
}
func (f *ShadowsocksFixture) BuildInvalidClientConfig(serverAddr string) UpstreamNode {
	cfg := f.BuildClientConfig(serverAddr)
	cfg.Password = "wrong-ss-key-16b"
	return cfg
}

// -----------------------------------------------------------------------------
// 通用 6 维标准测试执行器
// -----------------------------------------------------------------------------

func runUniversalProtocolTests(t *testing.T, fixture IProtocolTestFixture) {
	// 启动公用的 Echo Server 作为最终目的端
	echoAddr, echoTeardown := StartMockEchoServer(t)
	defer echoTeardown()

	serverAddr, serverTeardown := fixture.StartServer(t)
	defer serverTeardown()

	dialer, err := GlobalOutboundRegistry.CreateDialer(fixture.BuildClientConfig(serverAddr))
	if err != nil {
		t.Fatalf("[%s] 构造 Dialer 失败: %v", fixture.Protocol(), err)
	}

	// 用例 1: Test_Handshake_PingPong
	t.Run(fixture.Protocol()+"_PingPong", func(t *testing.T) {
		ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer cancel()

		conn, err := dialer.DialContext(ctx, "tcp", echoAddr)
		if err != nil {
			t.Fatalf("DialContext 失败: %v", err)
		}
		defer conn.Close()

		msg := []byte("PING-LWT-2026\n")
		if _, err := conn.Write(msg); err != nil {
			t.Fatalf("发送 Ping 失败: %v", err)
		}

		reply := make([]byte, len(msg))
		if _, err := io.ReadFull(conn, reply); err != nil {
			t.Fatalf("读取 Pong 回复失败: %v", err)
		}

		if string(reply) != string(msg) {
			t.Fatalf("回声数据不一致: 期望 %s, 实际 %s", string(msg), string(reply))
		}
	})

	// 用例 2: Test_Payload_SHA256_Integrity (512KB 随机流哈希保真，多包多块缓冲验证)
	t.Run(fixture.Protocol()+"_SHA256_Integrity", func(t *testing.T) {
		ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
		defer cancel()

		conn, err := dialer.DialContext(ctx, "tcp", echoAddr)
		if err != nil {
			t.Fatalf("DialContext 失败: %v", err)
		}
		defer conn.Close()

		// 构造 512KB 随机流 (为 32KB 缓冲区的 16 倍，充分验证多包流式与对拷完整性)
		payloadSize := 512 * 1024
		payload := make([]byte, payloadSize)
		_, _ = rand.Read(payload)
		origHash := sha256.Sum256(payload)

		var recvHash [32]byte
		var readErr error
		var wg sync.WaitGroup
		wg.Add(1)

		go func() {
			defer wg.Done()
			hasher := sha256.New()
			buf := make([]byte, 32*1024)
			totalRead := 0
			for totalRead < payloadSize {
				toRead := payloadSize - totalRead
				if toRead > len(buf) {
					toRead = len(buf)
				}
				n, err := conn.Read(buf[:toRead])
				if n > 0 {
					hasher.Write(buf[:n])
					totalRead += n
				}
				if err != nil {
					if err != io.EOF {
						readErr = err
					}
					break
				}
			}
			copy(recvHash[:], hasher.Sum(nil))
		}()

		// 发送数据
		if _, err := conn.Write(payload); err != nil {
			t.Fatalf("发送 Payload 失败: %v", err)
		}

		wg.Wait()
		if readErr != nil {
			t.Fatalf("接收回声流失败: %v", readErr)
		}

		if !bytes.Equal(origHash[:], recvHash[:]) {
			t.Fatalf("[%s] SHA256 校验不匹配! 原始: %x, 接收: %x", fixture.Protocol(), origHash, recvHash)
		}
	})

	// 用例 3: Test_Concurrent_Connections (10 并发流)
	t.Run(fixture.Protocol()+"_Concurrency", func(t *testing.T) {
		concurrency := 10
		var wg sync.WaitGroup
		errCh := make(chan error, concurrency)

		for i := 0; i < concurrency; i++ {
			wg.Add(1)
			go func(idx int) {
				defer wg.Done()
				ctx, cancel := context.WithTimeout(context.Background(), 4*time.Second)
				defer cancel()

				conn, err := dialer.DialContext(ctx, "tcp", echoAddr)
				if err != nil {
					errCh <- fmt.Errorf("并发 #%d 拨号失败: %w", idx, err)
					return
				}
				defer conn.Close()

				ping := []byte(fmt.Sprintf("CONCURRENT-%d\n", idx))
				if _, err := conn.Write(ping); err != nil {
					errCh <- fmt.Errorf("并发 #%d 写入失败: %w", idx, err)
					return
				}
				pong := make([]byte, len(ping))
				if _, err := io.ReadFull(conn, pong); err != nil {
					errCh <- fmt.Errorf("并发 #%d 读取失败: %w", idx, err)
					return
				}
				if string(pong) != string(ping) {
					errCh <- fmt.Errorf("并发 #%d 回声不匹配", idx)
					return
				}
			}(i)
		}

		wg.Wait()
		close(errCh)
		for err := range errCh {
			t.Fatal(err)
		}
	})

	// 用例 4: Test_Auth_Failure_Protection
	t.Run(fixture.Protocol()+"_AuthFailure", func(t *testing.T) {
		invalidDialer, err := GlobalOutboundRegistry.CreateDialer(fixture.BuildInvalidClientConfig(serverAddr))
		if err != nil {
			t.Fatalf("构造 Invalid Dialer 失败: %v", err)
		}

		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		defer cancel()

		conn, err := invalidDialer.DialContext(ctx, "tcp", echoAddr)
		if err == nil {
			// 如果拨号本身未报错，则后续写入/读取必定因鉴权错误被服务端掐断
			_, writeErr := conn.Write([]byte("SHOULD-FAIL\n"))
			buf := make([]byte, 32)
			_, readErr := conn.Read(buf)
			_ = conn.Close()
			if writeErr == nil && readErr == nil {
				t.Fatalf("[%s] 错误凭据未被拦截！连接与读写竟然成功！", fixture.Protocol())
			}
		}
	})

	// 用例 5: Test_Timeout_And_Cancellation
	t.Run(fixture.Protocol()+"_Timeout", func(t *testing.T) {
		ctx, cancel := context.WithCancel(context.Background())
		cancel() // 立即取消

		conn, err := dialer.DialContext(ctx, "tcp", echoAddr)
		if err == nil {
			_ = conn.Close()
			t.Fatalf("[%s] 预期 Context 取消失败，但实际拨号成功", fixture.Protocol())
		}
	})

	// 用例 6: Test_DialThrough_Chaining
	if fixture.SupportsChaining() {
		t.Run(fixture.Protocol()+"_DialThrough", func(t *testing.T) {
			ctx, cancel := context.WithTimeout(context.Background(), 3*time.Second)
			defer cancel()

			// 先建立直连到 Mock Server 的底层连接
			baseConn, err := net.DialTimeout("tcp", serverAddr, 3*time.Second)
			if err != nil {
				t.Fatalf("连接底层服务器失败: %v", err)
			}

			// 在已有的底座流上进行 DialThrough
			conn, err := dialer.DialThrough(ctx, baseConn, echoAddr)
			if err != nil {
				t.Fatalf("[%s] DialThrough 失败: %v", fixture.Protocol(), err)
			}
			defer conn.Close()

			ping := []byte("CHAINING-TEST-OK\n")
			if _, err := conn.Write(ping); err != nil {
				t.Fatalf("写入 DialThrough 数据失败: %v", err)
			}

			pong := make([]byte, len(ping))
			if _, err := io.ReadFull(conn, pong); err != nil {
				t.Fatalf("读取 DialThrough 回声失败: %v", err)
			}
			if string(pong) != string(ping) {
				t.Fatalf("DialThrough 回声不匹配: %s vs %s", string(pong), string(ping))
			}
		})
	}
}

// -----------------------------------------------------------------------------
// 协议矩阵自动化测试驱动器
// -----------------------------------------------------------------------------

func Test_Protocol_Matrix(t *testing.T) {
	fixtures := []IProtocolTestFixture{
		&Socks5Fixture{},
		&HttpConnectFixture{},
		&TrojanFixture{},
		&VlessFixture{},
		&ShadowsocksFixture{},
	}

	for _, f := range fixtures {
		t.Run(f.Protocol(), func(t *testing.T) {
			runUniversalProtocolTests(t, f)
		})
	}
}

// -----------------------------------------------------------------------------
// 智能分流路由决策引擎黑盒单测
// -----------------------------------------------------------------------------

func Test_RuleRouting_Matrix(t *testing.T) {
	policy := RoutePolicyConfig{
		DefaultAction:        "proxy",
		DirectPrivateNetwork: true,
		Rules: []RuleConfig{
			{
				Type:   "domain_suffix",
				Value:  ".cn",
				Action: "direct",
			},
			{
				Type:   "domain_suffix",
				Value:  "bilibili.com",
				Action: "direct",
			},
			{
				Type:   "domain_keyword",
				Value:  "adservice",
				Action: "reject",
			},
			{
				Type:   "domain_keyword",
				Value:  "tracking",
				Action: "reject",
			},
			{
				Type:   "ip_cidr",
				Value:  "223.5.5.0/24",
				Action: "direct",
			},
			{
				Type:   "ip_cidr",
				Value:  "114.114.114.114/32",
				Action: "direct",
			},
			{
				Type:   "domain_suffix",
				Value:  "corp.internal",
				Action: "chain",
				Target: "chain-corp",
			},
		},
	}

	router := NewRuleRoutingDispatcher(policy)

	testCases := []struct {
		TargetHost     string
		TargetPort     int
		ExpectedAction string
		ExpectedChain  string
		Desc           string
	}{
		{"192.168.1.100", 80, "direct", "", "RFC 1918 私网直连"},
		{"10.0.8.1", 443, "direct", "", "RFC 1918 10.x.x.x 私网直连"},
		{"127.0.0.1", 5270, "direct", "", "Loopback 回环直连"},
		{"www.bilibili.com", 443, "direct", "", "域名后缀命中 .cn / bilibili.com"},
		{"api.weibo.cn", 443, "direct", "", ".cn 域名后缀命中直连"},
		{"223.5.5.5", 53, "direct", "", "公共 DNS IP-CIDR 命中直连"},
		{"adservice.google.com", 443, "reject", "", "广告关键词命中阻断"},
		{"twitter.com", 443, "proxy", "", "境外域名未命中直连，走默认出站"},
		{"git.corp.internal", 22, "chain", "chain-corp", "企业私有域名走专用代理链"},
	}

	for _, tc := range testCases {
		t.Run(tc.Desc, func(t *testing.T) {
			meta := &SessionMetadata{Host: tc.TargetHost, Port: tc.TargetPort}
			action, chainID := router.Route(meta)
			if action != tc.ExpectedAction || chainID != tc.ExpectedChain {
				t.Fatalf("[%s] 分流决策异常: 目标 %s:%d, 期望 (%s, %s), 实际 (%s, %s)",
					tc.Desc, tc.TargetHost, tc.TargetPort, tc.ExpectedAction, tc.ExpectedChain, action, chainID)
			}
		})
	}
}

// -----------------------------------------------------------------------------
// 链式代理多跳级联端到端单测 (Hop 1: SOCKS5 -> Hop 2: HTTP CONNECT -> Echo Server)
// -----------------------------------------------------------------------------

func Test_ProxyChaining_EndToEnd(t *testing.T) {
	echoAddr, echoTeardown := StartMockEchoServer(t)
	defer echoTeardown()

	// Hop 2: HTTP CONNECT
	httpAddr, httpTeardown := StartMockHttpConnectServer(t, "chain-user", "chain-pass")
	defer httpTeardown()

	// Hop 1: SOCKS5
	socksAddr, socksTeardown := StartMockSocks5Server(t, "", "")
	defer socksTeardown()

	httpHost, httpPortStr, _ := net.SplitHostPort(httpAddr)
	httpPort, _ := strconv.Atoi(httpPortStr)

	socksHost, socksPortStr, _ := net.SplitHostPort(socksAddr)
	socksPort, _ := strconv.Atoi(socksPortStr)

	hop1Node := UpstreamNode{
		ID:     "hop1",
		Name:   "Hop 1 SOCKS5",
		Type:   "socks5",
		Server: socksHost,
		Port:   socksPort,
	}

	hop2Node := UpstreamNode{
		ID:       "hop2",
		Name:     "Hop 2 HTTP",
		Type:     "http",
		Server:   httpHost,
		Port:     httpPort,
		Username: "chain-user",
		Password: "chain-pass",
	}

	dialer1, err := GlobalOutboundRegistry.CreateDialer(hop1Node)
	if err != nil {
		t.Fatalf("创建 dialer1 失败: %v", err)
	}

	dialer2, err := GlobalOutboundRegistry.CreateDialer(hop2Node)
	if err != nil {
		t.Fatalf("创建 dialer2 失败: %v", err)
	}

	// 组合为两跳链式拨号器
	chainedDialer := NewChainedCompositeOutbound(
		"test-chain-1",
		"Test Chain 1",
		[]IOutboundDialer{dialer1, dialer2},
		[]UpstreamNode{hop1Node, hop2Node},
	)

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	conn, err := chainedDialer.DialContext(ctx, "tcp", echoAddr)
	if err != nil {
		t.Fatalf("多跳级联拨号失败: %v", err)
	}
	defer conn.Close()

	ping := []byte("CHAINED-HOP1-HOP2-ECHO\n")
	if _, err := conn.Write(ping); err != nil {
		t.Fatalf("写入链式流失败: %v", err)
	}

	pong := make([]byte, len(ping))
	if _, err := io.ReadFull(conn, pong); err != nil {
		t.Fatalf("读取链式流回声失败: %v", err)
	}

	if string(pong) != string(ping) {
		t.Fatalf("链式回声不匹配: %s vs %s", string(pong), string(ping))
	}
}
