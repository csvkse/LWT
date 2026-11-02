package watchdog

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"testing"
	"time"

	"linuxwebtool.local/mcp-bridge/internal/backend"
	"linuxwebtool.local/mcp-bridge/internal/catalog"
	"linuxwebtool.local/mcp-bridge/internal/config"
	"linuxwebtool.local/mcp-bridge/internal/gateway"
	"linuxwebtool.local/mcp-bridge/internal/state"
	"linuxwebtool.local/mcp-bridge/internal/tunnel"
)

type dummyAudit struct {
	logs []string
}

func (d *dummyAudit) Log(action, target, status, details string) {
	d.logs = append(d.logs, fmt.Sprintf("%s:%s:%s", action, target, status))
}

func TestExponentialBackoff_JitterAndBounds(t *testing.T) {
	bo := NewDefaultBackoff()

	d0 := bo.NextDelay(0)
	if d0 < 1500*time.Millisecond || d0 > 2500*time.Millisecond {
		t.Fatalf("unexpected d0: %v", d0)
	}

	d3 := bo.NextDelay(3)
	if d3 < 10*time.Second || d3 > 25*time.Second {
		t.Fatalf("unexpected d3: %v", d3)
	}

	d10 := bo.NextDelay(10)
	if d10 > 75*time.Second {
		t.Fatalf("d10 exceeded max bound: %v", d10)
	}
}

func TestCircuitBreaker_TrippingAndRecovery(t *testing.T) {
	cb := NewCircuitBreaker(3, 100*time.Millisecond)

	if !cb.AllowExecution() || cb.State() != CircuitClosed {
		t.Fatalf("initial state should be closed")
	}

	// 2 failures: still closed
	cb.RecordFailure()
	cb.RecordFailure()
	if !cb.AllowExecution() || cb.State() != CircuitClosed {
		t.Fatalf("should still be closed after 2 failures")
	}

	// 3rd failure: trips to Open
	tripped := cb.RecordFailure()
	if !tripped || cb.State() != CircuitOpen {
		t.Fatalf("should be tripped to Open")
	}
	if cb.AllowExecution() {
		t.Fatalf("execution should be blocked while Open")
	}

	// Wait for cooling period
	time.Sleep(120 * time.Millisecond)
	if cb.State() != CircuitHalfOpen {
		t.Fatalf("should transition to HalfOpen after cooling, got %s", cb.State())
	}
	if !cb.AllowExecution() {
		t.Fatalf("should allow execution in HalfOpen")
	}

	// Success resets to Closed
	cb.RecordSuccess()
	if cb.State() != CircuitClosed || cb.FailureCount() != 0 {
		t.Fatalf("expected reset to Closed, got %s", cb.State())
	}
}

func TestSupervisor_HydrationAndUserPaused(t *testing.T) {
	tmpDir := t.TempDir()

	vault, err := state.OpenOrCreateSecretsVault(tmpDir)
	if err != nil {
		t.Fatalf("failed to create vault: %v", err)
	}

	store, err := state.NewStore(tmpDir, vault)
	if err != nil {
		t.Fatalf("failed to create store: %v", err)
	}

	// Write mock state with autoConnect = true
	autoConn := true
	_, err = store.SaveDraft(context.Background(), 0, func(s *config.State) error {
		s.Tunnel = config.TunnelConfig{
			TunnelID:    "tunnel_0123456789abcdef0123456789abcdef",
			AutoConnect: &autoConn,
		}
		s.ActiveRevision = 1
		return nil
	})
	if err != nil {
		t.Fatalf("failed to save draft: %v", err)
	}

	registry := catalog.NewRegistry()
	mgr := backend.NewManager(vault)
	admission := gateway.NewAdmissionController()
	facade := gateway.NewFacadeServer(nil)
	gw := gateway.NewGateway(registry, mgr, store, admission, facade)
	tun := tunnel.NewController(vault, facade, nil)
	audit := &dummyAudit{}

	sup := NewSupervisor(store, gw, mgr, tun, audit, nil)

	// User pauses the tunnel explicitly
	tun.SetUserPaused(true)

	// Trigger hydration
	sup.hydrate(context.Background())

	st := sup.Status()
	if !st.IsHydrated {
		t.Fatalf("expected supervisor to be hydrated")
	}

	// Because user paused, tunnel should NOT be connected
	if tun.Status().State == tunnel.StateConnected {
		t.Fatalf("tunnel should respect userPaused intent")
	}

	// Now unpause and verify auto-connect logic
	tun.SetUserPaused(false)
	// Without key it sets StateUnconfigured or fails gracefully
	sup.hydrate(context.Background())
	if tun.Status().State == tunnel.StateConnected {
		t.Fatalf("should not connect without key")
	}

	_ = sup.Stop(context.Background())
	_ = os.RemoveAll(filepath.Join(tmpDir, "data"))
}
