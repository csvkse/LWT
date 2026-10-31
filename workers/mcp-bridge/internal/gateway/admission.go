package gateway

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"linuxwebtool.local/mcp-bridge/internal/catalog"
	"linuxwebtool.local/mcp-bridge/internal/config"
)

const (
	MaxGlobalExecutionConcurrency   = 4
	MaxPerServerExecutionConcurrency = 2
	DefaultCallTimeout               = 30 * time.Second
	MaxRequestDeadlineGuardrail     = 115 * time.Second // Reserve 5s for OpenAI tunnel return
)

var (
	ErrExecutionQueueTimeout = errors.New("execution admission queue timeout")
	ErrToolNotApproved       = errors.New("tool is not approved or has been revoked")
	ErrServerDisabled        = errors.New("backend server is disabled")
	ErrBackendRevisionDrift  = errors.New("backend revision changed during call queueing")
)

// AdmissionController manages bounded concurrent tool execution and admission checks.
type AdmissionController struct {
	mu           sync.Mutex
	semGlobal    chan struct{}
	serverActive map[string]int
}

// NewAdmissionController creates an admission controller.
func NewAdmissionController() *AdmissionController {
	return &AdmissionController{
		semGlobal:    make(chan struct{}, MaxGlobalExecutionConcurrency),
		serverActive: make(map[string]int),
	}
}

// AcquireExecutionSlot reserves a concurrency slot.
func (a *AdmissionController) AcquireExecutionSlot(ctx context.Context, serverID string) (func(), error) {
	select {
	case a.semGlobal <- struct{}{}:
	case <-time.After(2 * time.Second):
		return nil, ErrExecutionQueueTimeout
	case <-ctx.Done():
		return nil, ctx.Err()
	}

	a.mu.Lock()
	if a.serverActive[serverID] >= MaxPerServerExecutionConcurrency {
		a.mu.Unlock()
		<-a.semGlobal
		return nil, fmt.Errorf("server %q reached concurrency limit (%d)", serverID, MaxPerServerExecutionConcurrency)
	}

	a.serverActive[serverID]++
	a.mu.Unlock()

	released := false
	release := func() {
		a.mu.Lock()
		if !released {
			released = true
			if a.serverActive[serverID] > 0 {
				a.serverActive[serverID]--
			}
			a.mu.Unlock()
			<-a.semGlobal
		} else {
			a.mu.Unlock()
		}
	}

	return release, nil
}

// VerifyLinearizationPoint confirms that the tool binding is still valid immediately before dispatch.
func VerifyLinearizationPoint(snap *catalog.Snapshot, state config.State, exportedName string, initialBinding catalog.Binding) (*config.BackendSpec, error) {
	if snap == nil {
		return nil, ErrToolNotApproved
	}

	currentBinding, exists := snap.Bindings[exportedName]
	if !exists {
		return nil, fmt.Errorf("%w: tool %q is not in active snapshot", ErrToolNotApproved, exportedName)
	}

	if currentBinding.ServerID != initialBinding.ServerID || currentBinding.BackendRevision != initialBinding.BackendRevision {
		return nil, fmt.Errorf("%w: expected rev %d, active is %d",
			ErrBackendRevisionDrift, initialBinding.BackendRevision, currentBinding.BackendRevision)
	}

	srv, ok := state.Servers[currentBinding.ServerID]
	if !ok || !srv.Enabled {
		return nil, ErrServerDisabled
	}

	return srv, nil
}
