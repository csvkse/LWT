package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestProfileManager_SelectPipelineOutbound(t *testing.T) {
	tmpDir, err := os.MkdirTemp("", "forwarder-test-*")
	if err != nil {
		t.Fatalf("创建临时目录失败: %v", err)
	}
	defer os.RemoveAll(tmpDir)

	cfgPath := filepath.Join(tmpDir, "config.json")
	cfg, err := LoadConfig(cfgPath)
	if err != nil {
		t.Fatalf("LoadConfig failed: %v", err)
	}

	mgr := NewProfileManager(cfg)

	// 新建一个用于测试的 Pipeline
	pipe, err := mgr.UpsertPipeline(RoutingPipeline{
		ID:              "pipe-test-selector",
		Name:            "测试极速选择线路",
		DefaultAction:   "proxy",
		DefaultOutbound: "default-lwt-node",
	})
	if err != nil {
		t.Fatalf("UpsertPipeline failed: %v", err)
	}

	if pipe.DefaultOutbound != "default-lwt-node" {
		t.Fatalf("expected defaultOutbound default-lwt-node, got %s", pipe.DefaultOutbound)
	}

	// 极速切换出站到香港节点
	updated, err := mgr.SelectPipelineOutbound("pipe-test-selector", "node-hk-01")
	if err != nil {
		t.Fatalf("SelectPipelineOutbound failed: %v", err)
	}

	if updated.DefaultOutbound != "node-hk-01" {
		t.Errorf("expected defaultOutbound node-hk-01, got %s", updated.DefaultOutbound)
	}

	// 重新加载配置校验持久化
	reloadedCfg, err := LoadConfig(cfgPath)
	if err != nil {
		t.Fatalf("Reload Config failed: %v", err)
	}

	found := false
	for _, p := range reloadedCfg.Pipelines {
		if p.ID == "pipe-test-selector" {
			found = true
			if p.DefaultOutbound != "node-hk-01" {
				t.Errorf("persisted defaultOutbound expected node-hk-01, got %s", p.DefaultOutbound)
			}
		}
	}
	if !found {
		t.Errorf("expected to find pipe-test-selector in reloaded config")
	}
}

func TestProfileManager_GetActiveConnections(t *testing.T) {
	tmpDir, err := os.MkdirTemp("", "forwarder-test-conns-*")
	if err != nil {
		t.Fatalf("创建临时目录失败: %v", err)
	}
	defer os.RemoveAll(tmpDir)

	cfg, err := LoadConfig(filepath.Join(tmpDir, "config.json"))
	if err != nil {
		t.Fatalf("LoadConfig failed: %v", err)
	}

	mgr := NewProfileManager(cfg)
	conns := mgr.GetActiveConnections()
	if conns == nil {
		t.Errorf("expected non-nil connections list")
	}
}
