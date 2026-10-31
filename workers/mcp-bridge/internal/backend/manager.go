package backend

import (
	"context"
	"errors"
	"fmt"
	"sync"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/config"
	"linuxwebtool.local/mcp-bridge/internal/state"
)

const (
	MaxGlobalSessions   = 8
	MaxSessionsPerServer = 2
	MaxWaitingQueue      = 16
	QueueTimeout         = 2 * time.Second
	IdleSessionTTL       = 120 * time.Second
)

var (
	ErrQueueFull       = errors.New("backend connection queue is full (max 16)")
	ErrQueueTimeout    = errors.New("backend connection queue timeout")
	ErrManagerClosed   = errors.New("backend manager is closed")
)

// CallScope carries isolation identifiers for a tool execution request.
type CallScope struct {
	SessionID string
	RequestID string
}

// Lease represents a checked-out backend session.
type Lease interface {
	Session() Session
	Release()
}

type leaseImpl struct {
	session Session
	onDone  func()
}

func (l *leaseImpl) Session() Session {
	return l.session
}

func (l *leaseImpl) Release() {
	if l.onDone != nil {
		l.onDone()
		l.onDone = nil
	}
}

// ProbeResult reports the outcome of a backend connectivity check.
type ProbeResult struct {
	ServerID  string        `json:"serverId"`
	Connected bool          `json:"connected"`
	ToolCount int           `json:"toolCount"`
	Latency   time.Duration `json:"latency"`
	Error     string        `json:"error,omitempty"`
}

// Manager coordinates pool access, discovery, and lifecycle for all third-party backends.
type Manager struct {
	mu           sync.Mutex
	vault        *state.SecretsVault
	closed       bool
	activeCounts map[string]int
	globalCount  int
	semGlobal    chan struct{}
}

// NewManager creates a connection manager.
func NewManager(vault *state.SecretsVault) *Manager {
	return &Manager{
		vault:        vault,
		activeCounts: make(map[string]int),
		semGlobal:    make(chan struct{}, MaxGlobalSessions),
	}
}

// Acquire gets a session lease subject to concurrency limits.
func (m *Manager) Acquire(ctx context.Context, spec *config.BackendSpec, scope CallScope) (Lease, error) {
	if spec == nil {
		return nil, errors.New("backend spec is nil")
	}

	// 1. Acquire global concurrency slot
	select {
	case m.semGlobal <- struct{}{}:
	case <-time.After(QueueTimeout):
		return nil, ErrQueueTimeout
	case <-ctx.Done():
		return nil, ctx.Err()
	}

	// 2. Check per-server concurrency slot
	m.mu.Lock()
	if m.closed {
		m.mu.Unlock()
		<-m.semGlobal
		return nil, ErrManagerClosed
	}

	if m.activeCounts[spec.ID] >= MaxSessionsPerServer {
		m.mu.Unlock()
		<-m.semGlobal
		return nil, fmt.Errorf("server %q reached maximum concurrent session limit (%d)", spec.ID, MaxSessionsPerServer)
	}

	m.activeCounts[spec.ID]++
	m.globalCount++
	m.mu.Unlock()

	// 3. Decrypt secret if present
	var secret string
	if spec.EncryptedAuth != "" && m.vault != nil {
		decrypted, err := m.vault.Decrypt(spec.EncryptedAuth)
		if err != nil {
			m.releaseSlot(spec.ID)
			return nil, fmt.Errorf("failed to decrypt auth secret for %q: %w", spec.ID, err)
		}
		secret = decrypted
	}

	// 4. Connect session
	sess, err := ConnectBackend(ctx, spec, secret)
	if err != nil {
		m.releaseSlot(spec.ID)
		return nil, err
	}

	lease := &leaseImpl{
		session: sess,
		onDone: func() {
			_ = sess.Close(context.Background())
			m.releaseSlot(spec.ID)
		},
	}

	return lease, nil
}

func (m *Manager) releaseSlot(serverID string) {
	m.mu.Lock()
	if m.activeCounts[serverID] > 0 {
		m.activeCounts[serverID]--
	}
	if m.globalCount > 0 {
		m.globalCount--
	}
	m.mu.Unlock()
	<-m.semGlobal
}

// Probe explicitly tests connectivity and tool availability.
func (m *Manager) Probe(ctx context.Context, spec *config.BackendSpec) (*ProbeResult, error) {
	start := time.Now()
	res := &ProbeResult{
		ServerID: spec.ID,
	}

	var secret string
	if spec.EncryptedAuth != "" && m.vault != nil {
		decrypted, err := m.vault.Decrypt(spec.EncryptedAuth)
		if err != nil {
			res.Error = fmt.Sprintf("auth decrypt failed: %v", err)
			return res, nil
		}
		secret = decrypted
	}

	sess, err := ConnectBackend(ctx, spec, secret)
	if err != nil {
		res.Error = err.Error()
		res.Latency = time.Since(start)
		return res, nil
	}
	defer func() {
		_ = sess.Close(context.Background())
	}()

	toolsResult, err := sess.ListTools(ctx, "")
	res.Latency = time.Since(start)
	if err != nil {
		res.Error = err.Error()
		return res, nil
	}

	res.Connected = true
	if toolsResult != nil {
		res.ToolCount = len(toolsResult.Tools)
	}

	return res, nil
}

// Discover queries all available tools from the backend, following cursors up to 100 pages.
func (m *Manager) Discover(ctx context.Context, spec *config.BackendSpec) ([]*mcp.Tool, error) {
	var secret string
	if spec.EncryptedAuth != "" && m.vault != nil {
		decrypted, err := m.vault.Decrypt(spec.EncryptedAuth)
		if err != nil {
			return nil, fmt.Errorf("failed to decrypt auth for %q: %w", spec.ID, err)
		}
		secret = decrypted
	}

	sess, err := ConnectBackend(ctx, spec, secret)
	if err != nil {
		return nil, err
	}
	defer func() {
		_ = sess.Close(context.Background())
	}()

	var tools []*mcp.Tool
	cursor := ""
	const maxPages = 100

	for page := 0; page < maxPages; page++ {
		toolsResult, err := sess.ListTools(ctx, cursor)
		if err != nil {
			return nil, fmt.Errorf("failed to list tools on page %d: %w", page, err)
		}
		if toolsResult == nil {
			break
		}

		tools = append(tools, toolsResult.Tools...)
		if toolsResult.NextCursor == "" {
			break
		}
		cursor = toolsResult.NextCursor

		if len(tools) > config.MaxToolsPerServer {
			return nil, fmt.Errorf("server %q returned too many tools (%d > %d)", spec.ID, len(tools), config.MaxToolsPerServer)
		}
	}

	return tools, nil
}

// Close gracefully closes the manager.
func (m *Manager) Close() error {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.closed = true
	return nil
}
