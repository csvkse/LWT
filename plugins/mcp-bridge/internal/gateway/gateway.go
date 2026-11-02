package gateway

import (
	"context"
	"encoding/json"
	"fmt"
	"log/slog"
	"sync"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/backend"
	"linuxwebtool.local/mcp-bridge/internal/catalog"
	"linuxwebtool.local/mcp-bridge/internal/config"
	"linuxwebtool.local/mcp-bridge/internal/state"
)

// ApplyResult details the outcome of an atomic configuration publication.
type ApplyResult struct {
	ActiveRevision   uint64    `json:"activeRevision"`
	ExposedToolCount int       `json:"exposedToolCount"`
	DegradedServers  []string  `json:"degradedServers,omitempty"`
	AppliedAt        time.Time `json:"appliedAt"`
}

// Gateway orchestrates inbound tool listings and calls against third-party backends.
type Gateway struct {
	registry            *catalog.Registry
	manager             *backend.Manager
	store               *state.Store
	admission           *AdmissionController
	facadeServer        *mcp.Server
	mu                  sync.Mutex
	registeredToolNames map[string]bool
}

// NewGateway creates a Gateway instance.
func NewGateway(
	registry *catalog.Registry,
	manager *backend.Manager,
	store *state.Store,
	admission *AdmissionController,
	facadeServer *mcp.Server,
) *Gateway {
	gw := &Gateway{
		registry:            registry,
		manager:             manager,
		store:               store,
		admission:           admission,
		facadeServer:        facadeServer,
		registeredToolNames: make(map[string]bool),
	}
	return gw
}

// CurrentSnapshot returns the active immutable snapshot from registry.
func (g *Gateway) CurrentSnapshot() *catalog.Snapshot {
	if g.registry == nil {
		return nil
	}
	return g.registry.Current()
}

// List returns currently approved and active tools.
func (g *Gateway) List(_ context.Context, _ string) (*mcp.ListToolsResult, error) {
	snap := g.registry.Current()
	return &mcp.ListToolsResult{
		Tools: snap.Tools,
	}, nil
}

// Call executes an exported tool through admission guardrails to the target backend.
func (g *Gateway) Call(ctx context.Context, exportedName string, args json.RawMessage, scope backend.CallScope) (*mcp.CallToolResult, error) {
	// 1. Initial snapshot check
	initialSnap := g.registry.Current()
	initialBinding, exists := initialSnap.Bindings[exportedName]
	if !exists {
		return &mcp.CallToolResult{
			IsError: true,
			Content: []mcp.Content{
				&mcp.TextContent{Text: fmt.Sprintf("tool %q is not found or not approved for execution", exportedName)},
			},
		}, nil
	}

	// 2. Concurrency admission
	releaseSlot, err := g.admission.AcquireExecutionSlot(ctx, initialBinding.ServerID)
	if err != nil {
		return &mcp.CallToolResult{
			IsError: true,
			Content: []mcp.Content{
				&mcp.TextContent{Text: fmt.Sprintf("execution admission failed: %v", err)},
			},
		}, nil
	}
	defer releaseSlot()

	// 3. Re-verify at linearization point immediately before dispatch
	currentState, err := g.store.Read(ctx)
	if err != nil {
		return nil, fmt.Errorf("failed to read current state: %w", err)
	}

	activeSnap := g.registry.Current()
	spec, err := VerifyLinearizationPoint(activeSnap, currentState, exportedName, initialBinding)
	if err != nil {
		return &mcp.CallToolResult{
			IsError: true,
			Content: []mcp.Content{
				&mcp.TextContent{Text: fmt.Sprintf("linearization check rejected dispatch: %v", err)},
			},
		}, nil
	}

	// 4. Determine execution timeout deadline
	timeout := DefaultCallTimeout
	if spec.TimeoutSeconds > 0 {
		timeout = time.Duration(spec.TimeoutSeconds) * time.Second
	}
	if timeout > MaxRequestDeadlineGuardrail {
		timeout = MaxRequestDeadlineGuardrail
	}

	callCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()

	// 5. Acquire lease from backend manager
	lease, err := g.manager.Acquire(callCtx, spec, scope)
	if err != nil {
		return &mcp.CallToolResult{
			IsError: true,
			Content: []mcp.Content{
				&mcp.TextContent{Text: fmt.Sprintf("failed to acquire backend connection: %v", err)},
			},
		}, nil
	}
	defer lease.Release()

	// 6. Execute tool call on backend session
	res, err := lease.Session().CallTool(callCtx, initialBinding.UpstreamName, args)
	if err != nil {
		return &mcp.CallToolResult{
			IsError: true,
			Content: []mcp.Content{
				&mcp.TextContent{Text: fmt.Sprintf("backend execution error: %v", err)},
			},
		}, nil
	}

	return res, nil
}

// Apply compiles and publishes the candidate configuration.
func (g *Gateway) Apply(ctx context.Context, expectedRevision uint64) (*ApplyResult, error) {
	stateObj, err := g.store.Read(ctx)
	if err != nil {
		return nil, err
	}

	if expectedRevision != 0 && stateObj.DesiredRevision != expectedRevision {
		return nil, fmt.Errorf("%w: expected revision %d, actual is %d",
			state.ErrRevisionConflict, expectedRevision, stateObj.DesiredRevision)
	}

	// Discover tools for all enabled servers with graceful degradation for unready/failing backends
	discovered := make(map[string][]*mcp.Tool)
	var degradedServers []string
	for id, srv := range stateObj.Servers {
		if srv.Enabled {
			tools, err := g.manager.Discover(ctx, srv)
			if err != nil {
				slog.Warn("discovery failed for server during apply, degraded", "server", id, "error", err)
				degradedServers = append(degradedServers, fmt.Sprintf("%s (%s)", id, err.Error()))
				continue
			}
			discovered[id] = tools
		}
	}

	// Build immutable candidate snapshot
	candidateSnap, err := catalog.BuildCandidate(stateObj, discovered)
	if err != nil {
		return nil, fmt.Errorf("failed to build candidate snapshot: %w", err)
	}

	// Commit state version
	activeRev, err := g.store.Commit(ctx, expectedRevision, func(s *config.State) error {
		return nil
	})
	if err != nil {
		return nil, fmt.Errorf("failed to commit state: %w", err)
	}

	candidateSnap.Revision = activeRev

	// Atomically publish
	g.registry.Publish(candidateSnap)

	// Sync tools to in-memory MCP facade server
	g.syncFacade(candidateSnap)

	return &ApplyResult{
		ActiveRevision:   activeRev,
		ExposedToolCount: len(candidateSnap.Tools),
		DegradedServers:  degradedServers,
		AppliedAt:        time.Now(),
	}, nil
}

// ReplayActiveRevision hydrates in-memory catalog and facade server from the last active revision without committing a new revision.
func (g *Gateway) ReplayActiveRevision(ctx context.Context) (*ApplyResult, error) {
	stateObj, err := g.store.Read(ctx)
	if err != nil {
		return nil, err
	}

	activeRev := stateObj.ActiveRevision
	if activeRev == 0 {
		activeRev = stateObj.DesiredRevision
	}

	discovered := make(map[string][]*mcp.Tool)
	var degradedServers []string
	for id, srv := range stateObj.Servers {
		if srv.Enabled {
			tools, err := g.manager.Discover(ctx, srv)
			if err != nil {
				slog.Warn("discovery failed for server during replay, degraded", "server", id, "error", err)
				degradedServers = append(degradedServers, fmt.Sprintf("%s (%s)", id, err.Error()))
				continue
			}
			discovered[id] = tools
		}
	}

	candidateSnap, err := catalog.BuildCandidate(stateObj, discovered)
	if err != nil {
		return nil, fmt.Errorf("failed to build candidate snapshot during replay: %w", err)
	}

	candidateSnap.Revision = activeRev

	// Atomically publish
	g.registry.Publish(candidateSnap)

	// Sync tools to in-memory MCP facade server
	g.syncFacade(candidateSnap)

	return &ApplyResult{
		ActiveRevision:   activeRev,
		ExposedToolCount: len(candidateSnap.Tools),
		DegradedServers:  degradedServers,
		AppliedAt:        time.Now(),
	}, nil
}

func (g *Gateway) syncFacade(snap *catalog.Snapshot) {
	if g.facadeServer == nil {
		return
	}
	g.mu.Lock()
	defer g.mu.Unlock()

	// 1. Remove tools no longer present
	newNames := make(map[string]bool)
	for _, tool := range snap.Tools {
		newNames[tool.Name] = true
	}

	var toRemove []string
	for old := range g.registeredToolNames {
		if !newNames[old] {
			toRemove = append(toRemove, old)
		}
	}
	if len(toRemove) > 0 {
		g.facadeServer.RemoveTools(toRemove...)
		for _, rem := range toRemove {
			delete(g.registeredToolNames, rem)
		}
	}

	// 2. Add or update current tools
	for _, tool := range snap.Tools {
		toolName := tool.Name
		if !g.registeredToolNames[toolName] {
			g.facadeServer.AddTool(tool, func(ctx context.Context, req *mcp.CallToolRequest) (*mcp.CallToolResult, error) {
				return g.Call(ctx, toolName, req.Params.Arguments, backend.CallScope{
					RequestID: fmt.Sprintf("req-%d", time.Now().UnixNano()),
				})
			})
			g.registeredToolNames[toolName] = true
		}
	}
}
