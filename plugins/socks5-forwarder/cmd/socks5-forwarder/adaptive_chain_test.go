package main

import (
	"context"
	"net"
	"testing"
)

func TestAdaptiveChainedOutbound_PrioritySorting(t *testing.T) {
	nodes := []UpstreamNode{
		{ID: "node1", Name: "High Latency", Status: "healthy", LatencyMs: 200},
		{ID: "node2", Name: "Dead Node", Status: "dead", LatencyMs: 10},
		{ID: "node3", Name: "Low Latency", Status: "healthy", LatencyMs: 15},
		{ID: "node4", Name: "Unknown Node", Status: "unknown", LatencyMs: 0},
	}

	sorted := sortNodesByPriority(nodes)
	if sorted[0].ID != "node3" {
		t.Errorf("expected node3 (15ms) first, got %s (%dms, %s)", sorted[0].ID, sorted[0].LatencyMs, sorted[0].Status)
	}
	if sorted[1].ID != "node1" {
		t.Errorf("expected node1 (200ms) second, got %s", sorted[1].ID)
	}
	// Dead node should be last
	if sorted[len(sorted)-1].ID != "node2" {
		t.Errorf("expected dead node2 last, got %s", sorted[len(sorted)-1].ID)
	}
}

func TestAdaptiveChainedOutbound_PinnedExitFallback(t *testing.T) {
	// 启动一个本地 Echo 服务作为目标
	targetLn, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatalf("Listen failed: %v", err)
	}
	defer targetLn.Close()
	targetAddr := targetLn.Addr().String()

	go func() {
		for {
			conn, err := targetLn.Accept()
			if err != nil {
				return
			}
			go func(c net.Conn) {
				defer c.Close()
				buf := make([]byte, 1024)
				n, _ := c.Read(buf)
				if n > 0 {
					_, _ = c.Write(buf[:n])
				}
			}(conn)
		}
	}()

	// 固定落地节点：direct 协议
	fixedExit := &UpstreamNode{
		ID:     "fixed-exit-01",
		Name:   "Fixed Direct Egress",
		Type:   "direct",
		Status: "healthy",
	}

	// 候选跳板池为空时，直接单节点直连固定出口
	adaptive := NewAdaptiveChainedOutbound("chain-test", "Test Chain", fixedExit, nil, nil)
	conn, err := adaptive.DialContext(context.Background(), "tcp", targetAddr)
	if err != nil {
		t.Fatalf("DialContext failed: %v", err)
	}
	defer conn.Close()

	msg := []byte("ping-adaptive-pinned")
	if _, err := conn.Write(msg); err != nil {
		t.Fatalf("Write failed: %v", err)
	}
	reply := make([]byte, len(msg))
	if _, err := conn.Read(reply); err != nil {
		t.Fatalf("Read failed: %v", err)
	}
	if string(reply) != string(msg) {
		t.Errorf("expected reply %s, got %s", string(msg), string(reply))
	}
}
