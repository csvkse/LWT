package watchdog

import (
	"context"
	"fmt"
	"log/slog"
	"sync"
	"time"

	"linuxwebtool.local/mcp-bridge/internal/backend"
	"linuxwebtool.local/mcp-bridge/internal/gateway"
	"linuxwebtool.local/mcp-bridge/internal/state"
	"linuxwebtool.local/mcp-bridge/internal/tunnel"
)

// AuditSink records administrative and watchdog events.
type AuditSink interface {
	Log(action, target, status, details string)
}

// SupervisorStatus summarizes the overall health and watch state of the supervisor.
type SupervisorStatus struct {
	IsHydrated   bool                    `json:"isHydrated"`
	HydratedAt   time.Time               `json:"hydratedAt,omitempty"`
	Targets      map[string]TargetStatus `json:"targets"`
	LastAuditedAt time.Time              `json:"lastAuditedAt,omitempty"`
}

// Supervisor coordinates startup auto-connection and continuous watchdog self-healing.
type Supervisor struct {
	mu           sync.RWMutex
	store        *state.Store
	gateway      *gateway.Gateway
	manager      *backend.Manager
	tunnel       *tunnel.Controller
	audit        AuditSink
	logger       *slog.Logger
	backoff      *ExponentialBackoff
	breakers     map[string]*CircuitBreaker
	retries      map[string]int
	nextRetryAt  map[string]time.Time
	lastErrors   map[string]string
	isHydrated   bool
	hydratedAt   time.Time
	stopCh       chan struct{}
	doneCh       chan struct{}
	probeInterval time.Duration
}

// NewSupervisor constructs a watchdog supervisor.
func NewSupervisor(
	store *state.Store,
	gw *gateway.Gateway,
	mgr *backend.Manager,
	tun *tunnel.Controller,
	audit AuditSink,
	logger *slog.Logger,
) *Supervisor {
	if logger == nil {
		logger = slog.Default()
	}
	s := &Supervisor{
		store:         store,
		gateway:       gw,
		manager:       mgr,
		tunnel:        tun,
		audit:         audit,
		logger:        logger,
		backoff:       NewDefaultBackoff(),
		breakers:      make(map[string]*CircuitBreaker),
		retries:       make(map[string]int),
		nextRetryAt:   make(map[string]time.Time),
		lastErrors:    make(map[string]string),
		stopCh:        make(chan struct{}),
		doneCh:        make(chan struct{}),
		probeInterval: 10 * time.Second,
	}

	// Register listener on Tunnel controller for immediate reactive triggers
	if tun != nil {
		tun.SetStateChangeCallback(func(st tunnel.Status) {
			if st.State == tunnel.StateDegraded && !tun.IsUserPaused() {
				s.logger.Info("watchdog: reactive tunnel disconnect captured", "tunnelId", st.TunnelID)
				s.triggerTunnelRecovery(context.Background())
			}
		})
	}

	return s
}

// StartAsync initiates fast-boot non-blocking hydration and starts the background watchdog loop.
func (s *Supervisor) StartAsync(ctx context.Context) {
	go func() {
		defer close(s.doneCh)
		s.logger.Info("watchdog supervisor: commencing background hydration...")

		// 1. Perform initial asynchronous state hydration
		s.hydrate(ctx)

		// 2. Run continuous watchdog loop
		s.runLoop(ctx)
	}()
}

// hydrate replays active configuration and auto-starts the tunnel if configured.
func (s *Supervisor) hydrate(ctx context.Context) {
	st, err := s.store.Read(ctx)
	if err != nil {
		s.logger.Error("watchdog: failed to read store during hydration", "error", err)
		return
	}

	// 1. Replay active tools into Gateway
	if s.gateway != nil && (st.ActiveRevision > 0 || len(st.Servers) > 0) {
		res, err := s.gateway.ReplayActiveRevision(ctx)
		if err != nil {
			s.logger.Warn("watchdog: error during active revision replay", "error", err)
			if s.audit != nil {
				s.audit.Log("AUTO_HYDRATE", "gateway", "DEGRADED", fmt.Sprintf("replay warning: %v", err))
			}
		} else {
			s.logger.Info("watchdog: active revision hydrated successfully",
				"revision", res.ActiveRevision, "tools", res.ExposedToolCount, "degraded", res.DegradedServers)
			if s.audit != nil {
				s.audit.Log("AUTO_HYDRATE", "gateway", "SUCCESS",
					fmt.Sprintf("hydrated active revision %d with %d tools", res.ActiveRevision, res.ExposedToolCount))
			}
		}
	}

	// 2. Auto-connect OpenAI Tunnel if enabled and configured
	if s.tunnel != nil && st.Tunnel.ShouldAutoConnect() && st.Tunnel.TunnelID != "" && !s.tunnel.IsUserPaused() {
		s.logger.Info("watchdog: auto-starting OpenAI tunnel on boot", "tunnelId", st.Tunnel.TunnelID)
		if err := s.tunnel.Start(ctx, st.Tunnel); err != nil {
			s.logger.Warn("watchdog: initial auto-connect of tunnel failed, watchdog will retry", "error", err)
			s.recordFailure("tunnel", err)
			if s.audit != nil {
				s.audit.Log("AUTO_CONNECT", "tunnel", "DEGRADED", fmt.Sprintf("initial connection failed: %v", err))
			}
		} else {
			s.logger.Info("watchdog: tunnel auto-connected successfully", "tunnelId", st.Tunnel.TunnelID)
			s.recordSuccess("tunnel")
			if s.audit != nil {
				s.audit.Log("AUTO_CONNECT", "tunnel", "SUCCESS", fmt.Sprintf("tunnel %s auto-connected", st.Tunnel.TunnelID))
			}
		}
	}

	s.mu.Lock()
	s.isHydrated = true
	s.hydratedAt = time.Now()
	s.mu.Unlock()
}

// runLoop executes the periodic watchdog probe and recovery routines.
func (s *Supervisor) runLoop(ctx context.Context) {
	ticker := time.NewTicker(s.probeInterval)
	defer ticker.Stop()

	for {
		select {
		case <-s.stopCh:
			s.logger.Info("watchdog supervisor: stopping background loop")
			return
		case <-ctx.Done():
			s.logger.Info("watchdog supervisor: context cancelled")
			return
		case <-ticker.C:
			s.tick(ctx)
		}
	}
}

// tick checks and self-heals both Tunnel and Third-Party MCP backends.
func (s *Supervisor) tick(ctx context.Context) {
	s.checkTunnel(ctx)
	s.checkBackends(ctx)
}

func (s *Supervisor) getBreaker(id string) *CircuitBreaker {
	s.mu.Lock()
	defer s.mu.Unlock()
	cb, ok := s.breakers[id]
	if !ok {
		cb = NewCircuitBreaker(5, 60*time.Second)
		s.breakers[id] = cb
	}
	return cb
}

func (s *Supervisor) recordSuccess(id string) {
	cb := s.getBreaker(id)
	cb.RecordSuccess()

	s.mu.Lock()
	s.retries[id] = 0
	delete(s.nextRetryAt, id)
	delete(s.lastErrors, id)
	s.mu.Unlock()
}

func (s *Supervisor) recordFailure(id string, err error) {
	cb := s.getBreaker(id)
	tripped := cb.RecordFailure()

	s.mu.Lock()
	r := s.retries[id] + 1
	s.retries[id] = r
	delay := s.backoff.NextDelay(r)
	next := time.Now().Add(delay)
	s.nextRetryAt[id] = next
	s.lastErrors[id] = err.Error()
	s.mu.Unlock()

	if tripped && s.audit != nil {
		s.audit.Log("WATCHDOG_CIRCUIT_TRIPPED", id, "OPEN",
			fmt.Sprintf("consecutive failures reached threshold; cooling off: %v", err))
	}
}

// checkTunnel checks and recovers the OpenAI Tunnel if unexpectedly degraded.
func (s *Supervisor) checkTunnel(ctx context.Context) {
	if s.tunnel == nil {
		return
	}

	st := s.tunnel.Status()
	if st.State == tunnel.StateConnected || st.State == tunnel.StateConnecting || st.State == tunnel.StateUnconfigured || st.State == tunnel.StateStopped {
		return
	}

	if s.tunnel.IsUserPaused() {
		return
	}

	s.triggerTunnelRecovery(ctx)
}

func (s *Supervisor) triggerTunnelRecovery(ctx context.Context) {
	cb := s.getBreaker("tunnel")
	if !cb.AllowExecution() {
		s.logger.Debug("watchdog: tunnel circuit open, waiting for cooling period")
		return
	}

	s.mu.RLock()
	next := s.nextRetryAt["tunnel"]
	retries := s.retries["tunnel"]
	s.mu.RUnlock()

	if time.Now().Before(next) {
		return // Backoff waiting
	}

	cfg := s.tunnel.LastConfig()
	if cfg.TunnelID == "" {
		st, err := s.store.Read(ctx)
		if err == nil {
			cfg = st.Tunnel
		}
	}

	if cfg.TunnelID == "" || !cfg.ShouldAutoConnect() {
		return
	}

	s.logger.Info("watchdog: attempting tunnel reconnect", "tunnelId", cfg.TunnelID, "retry", retries+1)
	s.tunnel.SetBackoffStatus(retries+1, time.Now().Add(2*time.Second), "watchdog reconnecting...")

	err := s.tunnel.Start(ctx, cfg)
	if err != nil {
		s.logger.Warn("watchdog: tunnel reconnect failed", "error", err)
		s.recordFailure("tunnel", err)
		s.tunnel.SetBackoffStatus(s.retries["tunnel"], s.nextRetryAt["tunnel"], err.Error())
	} else {
		s.logger.Info("watchdog: tunnel successfully recovered", "tunnelId", cfg.TunnelID)
		s.recordSuccess("tunnel")
		if s.audit != nil {
			s.audit.Log("WATCHDOG_RECOVER", "tunnel", "SUCCESS", fmt.Sprintf("tunnel %s auto-recovered", cfg.TunnelID))
		}
	}
}

// checkBackends checks health of all enabled third-party servers and attempts self-healing.
func (s *Supervisor) checkBackends(ctx context.Context) {
	if s.manager == nil || s.store == nil {
		return
	}

	st, err := s.store.Read(ctx)
	if err != nil {
		return
	}

	hasRecoveredAny := false

	for id, srv := range st.Servers {
		if !srv.ShouldAutoReconnect() {
			continue
		}

		cb := s.getBreaker(id)
		if !cb.AllowExecution() {
			continue
		}

		s.mu.RLock()
		next := s.nextRetryAt[id]
		s.mu.RUnlock()
		if time.Now().Before(next) {
			continue
		}

		// Probe connectivity
		probeRes, err := s.manager.Probe(ctx, srv)
		if err != nil || (probeRes != nil && !probeRes.Connected) {
			errMsg := "probe disconnected"
			if err != nil {
				errMsg = err.Error()
			} else if probeRes != nil && probeRes.Error != "" {
				errMsg = probeRes.Error
			}
			s.recordFailure(id, fmt.Errorf("%s", errMsg))
		} else {
			// Backend is healthy
			s.mu.RLock()
			hadFailures := s.retries[id] > 0 || cb.State() != CircuitClosed
			s.mu.RUnlock()

			s.recordSuccess(id)
			if hadFailures {
				s.logger.Info("watchdog: backend server self-healed and reconnected", "server", id)
				hasRecoveredAny = true
				if s.audit != nil {
					s.audit.Log("WATCHDOG_RECOVER", id, "SUCCESS", "backend connection restored by watchdog probe")
				}
			}
		}
	}

	// If any previously degraded server recovered, replay active snapshot so its tools re-join the active pool
	if hasRecoveredAny && s.gateway != nil {
		s.logger.Info("watchdog: refreshing active snapshot following backend self-healing")
		_, _ = s.gateway.ReplayActiveRevision(ctx)
	}
}

// Status exports detailed diagnostic condition of the supervisor.
func (s *Supervisor) Status() SupervisorStatus {
	s.mu.RLock()
	defer s.mu.RUnlock()

	targets := make(map[string]TargetStatus)

	// Tunnel target
	if s.tunnel != nil {
		tunStatus := s.tunnel.Status()
		cb := s.breakers["tunnel"]
		cState := CircuitClosed
		if cb != nil {
			cState = cb.State()
		}
		targets["tunnel"] = TargetStatus{
			ID:           "tunnel",
			Type:         TargetTypeTunnel,
			State:        string(tunStatus.State),
			CircuitState: cState,
			RetryCount:   s.retries["tunnel"],
			NextRetryAt:  s.nextRetryAt["tunnel"],
			LastError:    s.lastErrors["tunnel"],
			LastProbeAt:  tunStatus.LastActiveAt,
		}
	}

	// Backend targets
	for id, cb := range s.breakers {
		if id == "tunnel" {
			continue
		}
		targets[id] = TargetStatus{
			ID:           id,
			Type:         TargetTypeBackend,
			State:        "Monitored",
			CircuitState: cb.State(),
			RetryCount:   s.retries[id],
			NextRetryAt:  s.nextRetryAt[id],
			LastError:    s.lastErrors[id],
			LastProbeAt:  time.Now(),
		}
	}

	return SupervisorStatus{
		IsHydrated: s.isHydrated,
		HydratedAt: s.hydratedAt,
		Targets:    targets,
	}
}

// Stop halts the watchdog background processing.
func (s *Supervisor) Stop(ctx context.Context) error {
	select {
	case <-s.stopCh:
		return nil // Already stopped
	default:
		close(s.stopCh)
	}

	select {
	case <-s.doneCh:
		return nil
	case <-ctx.Done():
		return ctx.Err()
	case <-time.After(3 * time.Second):
		return nil
	}
}
