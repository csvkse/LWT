package runtime_test

import (
	"context"
	"testing"
	"time"

	"linuxwebtool.local/mcp-bridge/internal/runtime"
	"linuxwebtool.local/mcp-bridge/internal/tunnel"
)

func TestRuntimeInitializationAndHealth(t *testing.T) {
	tempDir := t.TempDir()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()

	cfg := runtime.Config{
		StateDir: tempDir,
		RunID:    "run-test-1234",
	}

	rt, err := runtime.NewRuntime(cfg)
	if err != nil {
		t.Fatalf("failed to create runtime: %v", err)
	}

	// Health check
	health := rt.GetHealth(ctx)
	if !health.WorkerAlive || health.WorkerRunID != "run-test-1234" || !health.AdminReady {
		t.Errorf("unexpected health status: %+v", health)
	}
	if health.TunnelState != string(tunnel.StateUnconfigured) {
		t.Errorf("expected tunnel unconfigured, got %s", health.TunnelState)
	}

	// Duplicate run with same state directory should fail due to owner lock
	_, err = runtime.NewRuntime(cfg)
	if err == nil {
		t.Error("expected error creating second runtime on same state directory")
	}

	// Stop runtime
	if err := rt.Stop(ctx); err != nil {
		t.Fatalf("failed to stop runtime: %v", err)
	}

	// After stop, another runtime can be created successfully
	rt2, err := runtime.NewRuntime(cfg)
	if err != nil {
		t.Fatalf("failed to create runtime after previous stop: %v", err)
	}
	_ = rt2.Stop(ctx)
}
