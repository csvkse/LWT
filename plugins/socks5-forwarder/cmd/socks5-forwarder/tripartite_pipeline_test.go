package main

import (
	"os"
	"path/filepath"
	"testing"
)

func TestTripartite_MigrationAndDefaultPipelines(t *testing.T) {
	tmpDir, err := os.MkdirTemp("", "forwarder-tripartite-test-*")
	if err != nil {
		t.Fatalf("failed to create temp dir: %v", err)
	}
	defer os.RemoveAll(tmpDir)

	cfgPath := filepath.Join(tmpDir, "config.json")
	cfg, err := LoadConfig(cfgPath)
	if err != nil {
		t.Fatalf("LoadConfig failed: %v", err)
	}

	if len(cfg.Pipelines) < 2 {
		t.Fatalf("expected at least 2 default pipelines, got %d", len(cfg.Pipelines))
	}

	defaultPipe := cfg.Pipelines[0]
	if defaultPipe.ID != "pipe-default" {
		t.Errorf("expected pipe-default, got %s", defaultPipe.ID)
	}

	if len(cfg.Inbounds) == 0 {
		t.Fatalf("expected at least 1 inbound")
	}
	if cfg.Inbounds[0].PipelineID != "pipe-default" {
		t.Errorf("expected inbound linked to pipe-default, got %s", cfg.Inbounds[0].PipelineID)
	}
	if cfg.Inbounds[0].Type != "port" {
		t.Errorf("expected inbound type port, got %s", cfg.Inbounds[0].Type)
	}
}

func TestTripartite_ManagerPipelineCRUD(t *testing.T) {
	cfg := &Config{
		Pipelines: []RoutingPipeline{
			{
				ID:            "pipe-default",
				Name:          "Default Pipeline",
				DefaultAction: "proxy",
			},
		},
	}
	mgr := NewProfileManager(cfg)

	// 1. GetPipelines
	pipes := mgr.GetPipelines()
	if len(pipes) != 1 {
		t.Fatalf("expected 1 pipeline, got %d", len(pipes))
	}

	// 2. Upsert custom pipeline
	custom := RoutingPipeline{
		ID:            "pipe-ai",
		Name:          "AI Dedicated",
		DefaultAction: "direct",
		Rules: []RuleConfig{
			{Type: "domain_suffix", Value: "openai.com", Action: "proxy"},
		},
	}
	saved, err := mgr.UpsertPipeline(custom)
	if err != nil {
		t.Fatalf("UpsertPipeline failed: %v", err)
	}
	if saved.ID != "pipe-ai" {
		t.Errorf("expected ID pipe-ai, got %s", saved.ID)
	}

	// 3. Verify lookup
	p, found := mgr.GetPipeline("pipe-ai")
	if !found || len(p.Rules) != 1 {
		t.Fatalf("GetPipeline failed or rules missing")
	}

	// 4. Protection on deleting pipe-default
	if err := mgr.DeletePipeline("pipe-default"); err == nil {
		t.Errorf("expected error when deleting default pipeline")
	}

	// 5. Delete custom pipeline
	if err := mgr.DeletePipeline("pipe-ai"); err != nil {
		t.Fatalf("DeletePipeline failed: %v", err)
	}
	if _, found := mgr.GetPipeline("pipe-ai"); found {
		t.Errorf("expected pipe-ai deleted")
	}
}

func TestTripartite_StartAllNoDeadlock(t *testing.T) {
	cfg := &Config{
		Inbounds: []InboundProfile{
			{
				ID:         "inbound-test-1",
				Name:       "Test Inbound",
				ListenPort: 28091,
				Enabled:    true,
				PipelineID: "pipe-default",
			},
		},
		Pipelines: []RoutingPipeline{
			{
				ID:            "pipe-default",
				Name:          "Default Pipeline",
				DefaultAction: "proxy",
			},
		},
	}
	mgr := NewProfileManager(cfg)

	// Verify StartAll does not deadlock when resolving pipelines
	mgr.StartAll()
	defer mgr.StopAll()

	_, inbounds := mgr.GetSnapshot()
	if len(inbounds) != 1 {
		t.Fatalf("expected 1 inbound, got %d", len(inbounds))
	}
	if inbounds[0].Status != "running" {
		t.Errorf("expected inbound running, got %s (error: %s)", inbounds[0].Status, inbounds[0].LastError)
	}
}
