package tunnel

import (
	"context"
	"fmt"
	"log/slog"
	"sync"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	tunnelclient "github.com/openai/tunnel-client"
	"linuxwebtool.local/mcp-bridge/internal/config"
	"linuxwebtool.local/mcp-bridge/internal/state"
)

// State describes the current connection state of the OpenAI Secure Tunnel.
type State string

const (
	StateUnconfigured State = "Unconfigured"
	StateConnecting   State = "Connecting"
	StateConnected    State = "Connected"
	StateDegraded     State = "Degraded"
	StateAuthFailed   State = "AuthFailed"
	StateStopped      State = "Stopped"
)

// Status details the current operational condition of the tunnel controller.
type Status struct {
	State        State     `json:"state"`
	TunnelID     string    `json:"tunnelId,omitempty"`
	ErrorMessage string    `json:"errorMessage,omitempty"`
	LastActiveAt time.Time `json:"lastActiveAt,omitempty"`
}

// Controller manages the embedded OpenAI Tunnel client runtime.
type Controller struct {
	mu           sync.Mutex
	vault        *state.SecretsVault
	server       *mcp.Server
	client       *tunnelclient.Client
	status       Status
	stopCh       chan struct{}
	drainTimeout time.Duration
	logger       *slog.Logger
}

// NewController creates a Tunnel controller.
func NewController(vault *state.SecretsVault, server *mcp.Server, logger *slog.Logger) *Controller {
	return &Controller{
		vault:        vault,
		server:       server,
		status:       Status{State: StateUnconfigured},
		drainTimeout: 10 * time.Second,
		logger:       logger,
	}
}

// Status returns current tunnel condition.
func (c *Controller) Status() Status {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.status
}

// Start constructs and connects the OpenAI Tunnel client with an in-memory transport pair.
func (c *Controller) Start(ctx context.Context, tunnelCfg config.TunnelConfig) error {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.status.State == StateConnected || c.status.State == StateConnecting {
		return nil // Already running or starting
	}

	if tunnelCfg.TunnelID == "" {
		c.status = Status{State: StateUnconfigured}
		return nil
	}

	var apiKey string
	if tunnelCfg.EncryptedKey != "" && c.vault != nil {
		decrypted, err := c.vault.Decrypt(tunnelCfg.EncryptedKey)
		if err != nil {
			c.status = Status{
				State:        StateAuthFailed,
				TunnelID:     tunnelCfg.TunnelID,
				ErrorMessage: fmt.Sprintf("failed to decrypt tunnel API key: %v", err),
			}
			return err
		}
		apiKey = decrypted
	}

	if apiKey == "" {
		c.status = Status{
			State:        StateUnconfigured,
			TunnelID:     tunnelCfg.TunnelID,
			ErrorMessage: "tunnel API key not configured",
		}
		return nil
	}

	c.status = Status{
		State:    StateConnecting,
		TunnelID: tunnelCfg.TunnelID,
	}

	// Create in-memory transport pair between facade server and tunnel-client
	clientTransport, serverTransport := mcp.NewInMemoryTransports()

	// Run facade server on server transport
	serverCtx, serverCancel := context.WithCancel(context.Background())
	go func() {
		_ = c.server.Run(serverCtx, serverTransport)
	}()

	clientCfg := tunnelclient.Config{
		TunnelID:                 tunnelCfg.TunnelID,
		APIKey:                   apiKey,
		ControlPlaneBaseURL:      tunnelCfg.ControlPlaneBaseURL,
		OrganizationID:           tunnelCfg.OrganizationID,
		PollTimeout:              5 * time.Second,
		PollDeadlineGuardrail:    5 * time.Second,
	}

	client, err := tunnelclient.New(clientCfg, clientTransport)
	if err != nil {
		serverCancel()
		c.status = Status{
			State:        StateDegraded,
			TunnelID:     tunnelCfg.TunnelID,
			ErrorMessage: err.Error(),
		}
		return fmt.Errorf("failed to construct tunnel client: %w", err)
	}

	if err := client.Start(ctx); err != nil {
		serverCancel()
		c.status = Status{
			State:        StateDegraded,
			TunnelID:     tunnelCfg.TunnelID,
			ErrorMessage: err.Error(),
		}
		return fmt.Errorf("failed to start tunnel client: %w", err)
	}

	c.client = client
	c.status.State = StateConnected
	c.status.LastActiveAt = time.Now()

	// Background watcher for unexpected termination
	c.stopCh = make(chan struct{})
	go func() {
		select {
		case <-c.client.Done():
			c.mu.Lock()
			if c.status.State == StateConnected {
				c.status.State = StateDegraded
				c.status.ErrorMessage = "tunnel client runtime terminated unexpectedly"
			}
			c.mu.Unlock()
			serverCancel()
		case <-c.stopCh:
			serverCancel()
		}
	}()

	return nil
}

// Stop halts control-plane polling and shuts down the embedded tunnel client.
func (c *Controller) Stop(ctx context.Context) error {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.client == nil {
		c.status.State = StateStopped
		return nil
	}

	if c.stopCh != nil {
		close(c.stopCh)
		c.stopCh = nil
	}

	drainCtx, cancel := context.WithTimeout(ctx, c.drainTimeout)
	defer cancel()

	err := c.client.Stop(drainCtx)
	c.client = nil
	c.status.State = StateStopped
	return err
}
