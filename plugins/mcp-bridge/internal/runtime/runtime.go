package runtime

import (
	"context"
	"fmt"
	"log/slog"
	"time"

	"linuxwebtool.local/mcp-bridge/internal/backend"
	"linuxwebtool.local/mcp-bridge/internal/catalog"
	"linuxwebtool.local/mcp-bridge/internal/gateway"
	"linuxwebtool.local/mcp-bridge/internal/state"
	"linuxwebtool.local/mcp-bridge/internal/tunnel"
	"linuxwebtool.local/mcp-bridge/internal/watchdog"
)

// Config configures Bridge runtime startup parameters.
type Config struct {
	StateDir string
	RunID    string
	Listen   string
	Logger   *slog.Logger
	Audit    watchdog.AuditSink
}

// Runtime acts as the primary service orchestrator for mcp-bridge.
type Runtime struct {
	cfg        Config
	vault      *state.SecretsVault
	lock       *state.OwnerLock
	store      *state.Store
	registry   *catalog.Registry
	manager    *backend.Manager
	admission  *gateway.AdmissionController
	gateway    *gateway.Gateway
	tunnel     *tunnel.Controller
	recovery   *tunnel.RecoveryManager
	supervisor *watchdog.Supervisor
	logger     *slog.Logger
}

// NewRuntime constructs and initializes all sub-components.
func NewRuntime(cfg Config) (*Runtime, error) {
	logger := cfg.Logger
	if logger == nil {
		logger = slog.Default()
	}

	// 1. Secrets Vault
	vault, err := state.OpenOrCreateSecretsVault(cfg.StateDir)
	if err != nil {
		return nil, fmt.Errorf("failed to open secrets vault: %w", err)
	}

	// 2. Owner Lock
	lock, err := state.AcquireOwnerLock(cfg.StateDir, cfg.RunID)
	if err != nil {
		return nil, fmt.Errorf("failed to acquire owner lock: %w", err)
	}

	// 3. Persistent Store
	store, err := state.NewStore(cfg.StateDir, vault)
	if err != nil {
		_ = lock.Release()
		return nil, fmt.Errorf("failed to open state store: %w", err)
	}

	// 4. Recovery Manager
	recovery := tunnel.NewRecoveryManager(cfg.StateDir)

	// 5. Backend Manager
	manager := backend.NewManager(vault)
	manager.SetProxyResolver(func() string {
		st, err := store.Read(context.Background())
		if err == nil && st.Tunnel.ProxyURL != "" {
			return st.Tunnel.ProxyURL
		}
		return ""
	})

	// 6. Registry & Admission
	registry := catalog.NewRegistry()
	admission := gateway.NewAdmissionController()

	// 7. Facade Server & Gateway
	facade := gateway.NewFacadeServer(logger)
	gw := gateway.NewGateway(registry, manager, store, admission, facade)

	// 8. Tunnel Controller
	tun := tunnel.NewController(vault, facade, logger)

	// 9. Watchdog Supervisor
	sup := watchdog.NewSupervisor(store, gw, manager, tun, cfg.Audit, logger)

	return &Runtime{
		cfg:        cfg,
		vault:      vault,
		lock:       lock,
		store:      store,
		registry:   registry,
		manager:    manager,
		admission:  admission,
		gateway:    gw,
		tunnel:     tun,
		recovery:   recovery,
		supervisor: sup,
		logger:     logger,
	}, nil
}

// Store returns the persistent store.
func (r *Runtime) Store() *state.Store { return r.store }

// Gateway returns the gateway.
func (r *Runtime) Gateway() *gateway.Gateway { return r.gateway }

// Registry returns the catalog registry.
func (r *Runtime) Registry() *catalog.Registry { return r.registry }

// Manager returns the backend manager.
func (r *Runtime) Manager() *backend.Manager { return r.manager }

// Tunnel returns the tunnel controller.
func (r *Runtime) Tunnel() *tunnel.Controller { return r.tunnel }

// Recovery returns the recovery manager.
func (r *Runtime) Recovery() *tunnel.RecoveryManager { return r.recovery }

// Supervisor returns the watchdog supervisor.
func (r *Runtime) Supervisor() *watchdog.Supervisor { return r.supervisor }

// GetHealth computes the unified health status.
func (r *Runtime) GetHealth(ctx context.Context) HealthStatus {
	st, err := r.store.Read(ctx)
	if err != nil {
		return HealthStatus{
			WorkerAlive: true,
			WorkerRunID: r.cfg.RunID,
			AdminReady:  true,
			Timestamp:   time.Now(),
		}
	}

	var summary BackendSummary
	summary.Total = len(st.Servers)
	for _, srv := range st.Servers {
		if srv.Enabled {
			summary.Enabled++
		}
	}

	tunStatus := r.tunnel.Status()

	var supStatus *watchdog.SupervisorStatus
	hydrating := false
	if r.supervisor != nil {
		supSt := r.supervisor.Status()
		supStatus = &supSt
		hydrating = !supSt.IsHydrated
	}

	return HealthStatus{
		WorkerAlive:      true,
		WorkerRunID:      r.cfg.RunID,
		AdminReady:       true,
		TunnelState:      string(tunStatus.State),
		BackendCounts:    summary,
		DraftRevision:    st.DraftRevision,
		DesiredRevision:  st.DesiredRevision,
		ActiveRevision:   st.ActiveRevision,
		RecoveryRequired: r.recovery.IsRecoveryRequired(),
		Hydrating:        hydrating,
		SupervisorStatus: supStatus,
		Timestamp:        time.Now(),
	}
}

// Stop gracefully cleans up all resources and releases owner lock.
func (r *Runtime) Stop(ctx context.Context) error {
	var errs []error

	if r.supervisor != nil {
		if err := r.supervisor.Stop(ctx); err != nil {
			errs = append(errs, err)
		}
	}

	if err := r.tunnel.Stop(ctx); err != nil {
		errs = append(errs, err)
	}

	if err := r.manager.Close(); err != nil {
		errs = append(errs, err)
	}

	if err := r.lock.Release(); err != nil {
		errs = append(errs, err)
	}

	if len(errs) > 0 {
		return fmt.Errorf("errors during runtime stop: %v", errs)
	}
	return nil
}
