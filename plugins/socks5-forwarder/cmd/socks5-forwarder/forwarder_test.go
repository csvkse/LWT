package main

import (
	"net"
	"path/filepath"
	"testing"
	"time"
)

// 1. 测试 ACLManager CIDR 白名单
func TestACLManager(t *testing.T) {
	subnets := []string{
		"192.168.1.0/24",
		"10.0.0.0/8",
		"127.0.0.1",
	}
	acl := NewACLManager(subnets)

	allowedAddrs := []string{
		"192.168.1.50:12345",
		"192.168.1.1:80",
		"10.126.1.70:5271",
		"127.0.0.1:9090",
	}
	for _, addrStr := range allowedAddrs {
		addr, _ := net.ResolveTCPAddr("tcp", addrStr)
		if !acl.IsAllowed(addr) {
			t.Errorf("应该放行 %s，但被拒绝", addrStr)
		}
	}

	deniedAddrs := []string{
		"192.168.2.1:12345",
		"172.16.0.1:80",
		"203.0.113.195:443",
		"8.8.8.8:53",
	}
	for _, addrStr := range deniedAddrs {
		addr, _ := net.ResolveTCPAddr("tcp", addrStr)
		if acl.IsAllowed(addr) {
			t.Errorf("应该拦截 %s，但被放行", addrStr)
		}
	}

	// 测试通配符 * 放行
	wildcardAcl := NewACLManager([]string{"*"})
	anyAddr, _ := net.ResolveTCPAddr("tcp", "8.8.8.8:1234")
	if !wildcardAcl.IsAllowed(anyAddr) {
		t.Errorf("通配符 * 应该放行任何地址")
	}
}

// 2. 测试配置加载、保存与并发克隆
func TestConfigPersistence(t *testing.T) {
	tmpDir := t.TempDir()
	cfgFile := filepath.Join(tmpDir, "test_config.json")

	cfg, err := LoadConfig(cfgFile)
	if err != nil {
		t.Fatalf("LoadConfig 失败: %v", err)
	}

	if len(cfg.Inbounds) == 0 || len(cfg.Upstreams) == 0 {
		t.Errorf("默认骨架配置不应为空")
	}

	// 添加新节点并保存
	cfg.Upstreams = append(cfg.Upstreams, UpstreamNode{
		ID:            "test-node-tokyo",
		Name:          "东京测试节点",
		URL:           "wss://example.com/stream",
		Token:         "test-secret",
		SkipTLSVerify: true,
	})
	if err := cfg.Save(); err != nil {
		t.Fatalf("Save 失败: %v", err)
	}

	// 重新加载并校验
	loaded, err := LoadConfig(cfgFile)
	if err != nil {
		t.Fatalf("重新 LoadConfig 失败: %v", err)
	}
	if len(loaded.Upstreams) != len(cfg.Upstreams) {
		t.Errorf("加载的 Upstreams 数量不一致: 期望 %d, 实际 %d", len(cfg.Upstreams), len(loaded.Upstreams))
	}
}

// 3. 测试上游调度策略 (Strategy Pattern)
func TestSelectUpstreamStrategy(t *testing.T) {
	nodeMap := map[string]*UpstreamNode{
		"node-1": {ID: "node-1", Name: "节点1", Status: "healthy", LatencyMs: 150},
		"node-2": {ID: "node-2", Name: "节点2", Status: "healthy", LatencyMs: 45},
		"node-3": {ID: "node-3", Name: "节点3", Status: "dead", LatencyMs: 0},
	}
	candidates := []string{"node-1", "node-2", "node-3"}

	// 策略 A: 最优延迟 (Best Latency) 应该选择 node-2 (45ms)
	bestNode, err := SelectUpstream("best_latency", candidates, nodeMap, nil)
	if err != nil {
		t.Fatalf("SelectUpstream 失败: %v", err)
	}
	if bestNode.ID != "node-2" {
		t.Errorf("最优延迟策略应选择 node-2，实际选择了 %s", bestNode.ID)
	}

	// 策略 B: 轮询 (Round Robin)
	var rr uint64 = 0
	nodeFirst, _ := SelectUpstream("round_robin", candidates, nodeMap, &rr)
	nodeSecond, _ := SelectUpstream("round_robin", candidates, nodeMap, &rr)
	if nodeFirst.ID == nodeSecond.ID {
		t.Errorf("轮询策略连续两次选择不应相同")
	}

	// 策略 C: 单节点 / Failover 遇挂自愈
	singleCandidates := []string{"node-3", "node-1"} // node-3 是 dead
	failoverNode, _ := SelectUpstream("single", singleCandidates, nodeMap, nil)
	if failoverNode.ID != "node-1" {
		t.Errorf("Failover 应自动避开 dead 节点选择 node-1，实际选择 %s", failoverNode.ID)
	}
}

// 4. 测试 ProfileManager 实例生命周期
func TestProfileManagerLifecycle(t *testing.T) {
	tmpDir := t.TempDir()
	cfgFile := filepath.Join(tmpDir, "pm_config.json")
	cfg, _ := LoadConfig(cfgFile)

	// 使用动态高位测试端口避免冲突
	cfg.Inbounds = []InboundProfile{
		{
			ID:               "test-profile-1",
			Name:             "测试入站1",
			Enabled:          false,
			ListenPort:       28091,
			Protocol:         "mixed",
			AllowedCIDR:      []string{"127.0.0.1/32"},
			UpstreamStrategy: "single",
			UpstreamNodes:    []string{"default-lwt-node"},
		},
	}

	mgr := NewProfileManager(cfg)
	mgr.StartAll()
	defer mgr.StopAll()

	// 初始状态应为 stopped
	_, inbounds := mgr.GetSnapshot()
	if inbounds[0].Status != "stopped" {
		t.Errorf("初始未启用 Profile 状态应为 stopped, 实际为 %s", inbounds[0].Status)
	}

	// 启停切换
	toggled, err := mgr.ToggleInbound("test-profile-1")
	if err != nil {
		t.Fatalf("ToggleInbound 失败: %v", err)
	}
	if !toggled.Enabled || toggled.Status != "running" {
		t.Errorf("Toggle 后应为 enabled=true, status=running, 实际: %+v", toggled)
	}

	// 验证端口是否已在监听
	conn, err := net.DialTimeout("tcp", "127.0.0.1:28091", 500*time.Millisecond)
	if err != nil {
		t.Errorf("无法连接已启动的入站端口 28091: %v", err)
	} else {
		conn.Close()
	}

	// 再次 Toggle 关闭
	toggledOff, _ := mgr.ToggleInbound("test-profile-1")
	if toggledOff.Enabled || toggledOff.Status != "stopped" {
		t.Errorf("再次 Toggle 后应为 stopped, 实际: %+v", toggledOff)
	}
}
