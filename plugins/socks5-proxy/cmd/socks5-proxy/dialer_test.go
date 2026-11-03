package main

import (
	"context"
	"net"
	"strconv"
	"testing"
	"time"
)

func TestDialUpstreamWithRace_SingleIP(t *testing.T) {
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("failed to listen: %v", err)
	}
	defer l.Close()

	_, portStr, _ := net.SplitHostPort(l.Addr().String())
	port, _ := strconv.Atoi(portStr)

	go func() {
		conn, err := l.Accept()
		if err == nil {
			_ = conn.Close()
		}
	}()

	ips := []net.IP{net.ParseIP("127.0.0.1")}
	conn, winningIP, err := dialUpstreamWithRace(context.Background(), ips, port, 2*time.Second, nil)
	if err != nil {
		t.Fatalf("dial failed: %v", err)
	}
	defer conn.Close()

	if winningIP.String() != "127.0.0.1" {
		t.Fatalf("expected winning IP 127.0.0.1, got %s", winningIP)
	}
}

func TestDialUpstreamWithRace_MultiIP_FastFailover(t *testing.T) {
	// 启动一个正常的 listener
	l, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("failed to listen: %v", err)
	}
	defer l.Close()

	_, portStr, _ := net.SplitHostPort(l.Addr().String())
	port, _ := strconv.Atoi(portStr)

	go func() {
		for {
			conn, err := l.Accept()
			if err != nil {
				return
			}
			_ = conn.Close()
		}
	}()

	// 第一个 IP 是本机未分配/被拒绝的本地回环地址 (127.0.0.2:port 上无服务，会立即 connection refused)
	// 第二个 IP 是正常的 127.0.0.1
	// 验证快速故障转移 (Fast Failover)，第二个 IP 应立即被拉起并胜出
	ips := []net.IP{
		net.ParseIP("127.0.0.2"),
		net.ParseIP("127.0.0.1"),
	}

	t0 := time.Now()
	conn, winningIP, err := dialUpstreamWithRace(context.Background(), ips, port, 2*time.Second, nil)
	if err != nil {
		t.Fatalf("dial with race failed: %v", err)
	}
	defer conn.Close()

	duration := time.Since(t0)
	t.Logf("Happy Eyeballs race won by %s in %v", winningIP, duration)

	if winningIP.String() != "127.0.0.1" {
		t.Fatalf("expected 127.0.0.1 to win the race, got %s", winningIP)
	}
}

func TestDialUpstreamWithRace_ACL_Rejection(t *testing.T) {
	// 配置 ACL 仅允许 10.0.0.0/8
	acl, err := NewAclEngine("all", "10.0.0.0/8", "*", "*")
	if err != nil {
		t.Fatalf("new acl failed: %v", err)
	}

	ips := []net.IP{net.ParseIP("192.168.1.1"), net.ParseIP("172.16.0.1")}
	_, _, err = dialUpstreamWithRace(context.Background(), ips, 80, 1*time.Second, acl)
	if err == nil {
		t.Fatalf("expected ACL rejection error, got nil")
	}
	t.Logf("ACL properly rejected all disallowed IPs: %v", err)
}
