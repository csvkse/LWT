package tunnel

import (
	"context"
	"fmt"
	"log/slog"
	"os"
	"strconv"
	"strings"
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
	StateBackoffWait  State = "BackoffWait"
	StateAuthFailed   State = "AuthFailed"
	StateStopped      State = "Stopped"
)

// Status details the current operational condition of the tunnel controller.
type Status struct {
	State           State     `json:"state"`
	TunnelID        string    `json:"tunnelId,omitempty"`
	ErrorMessage    string    `json:"errorMessage,omitempty"`
	LastActiveAt    time.Time `json:"lastActiveAt,omitempty"`
	UserPaused      bool      `json:"userPaused"`
	RetryCount      int       `json:"retryCount,omitempty"`
	NextRetryAt     time.Time `json:"nextRetryAt,omitempty"`
	ReadyForChatGPT bool      `json:"readyForChatgpt"`
	InitialPollMs   int64     `json:"initialPollMs,omitempty"`
	LastPollStatus  int       `json:"lastPollStatus,omitempty"`
	LastPollError   string    `json:"lastPollError,omitempty"`
	EffectiveProxy  string    `json:"effectiveProxy,omitempty"`
}

// Controller manages the embedded OpenAI Tunnel client runtime.
type Controller struct {
	mu            sync.Mutex
	vault         *state.SecretsVault
	server        *mcp.Server
	client        *tunnelclient.Client
	status        Status
	lastCfg       config.TunnelConfig
	userPaused    bool
	onStateChange func(Status)
	stopCh        chan struct{}
	drainTimeout  time.Duration
	logger        *slog.Logger
	logBuf        *ChannelLogBuffer
}

// NewController creates a Tunnel controller.
func NewController(vault *state.SecretsVault, server *mcp.Server, logger *slog.Logger) *Controller {
	c := &Controller{
		vault:        vault,
		server:       server,
		status:       Status{State: StateUnconfigured},
		drainTimeout: 10 * time.Second,
		logger:       logger,
	}

	c.logBuf = NewChannelLogBuffer(200, func(entry ChannelLogEntry) {
		go c.processLogEntry(entry)
	})

	return c
}

// processLogEntry handles log events from the tunnel runtime and self-heals transient errors.
func (c *Controller) processLogEntry(e ChannelLogEntry) {
	c.mu.Lock()
	defer c.mu.Unlock()

	if e.Level == "WARN" || e.Level == "ERROR" {
		if errMsg, ok := e.Attrs["error"].(string); ok && errMsg != "" {
			c.status.LastPollError = errMsg
		} else {
			c.status.LastPollError = e.Message
		}
	}

	if statusVal, ok := e.Attrs["status"]; ok {
		if s, ok := statusVal.(int64); ok {
			c.status.LastPollStatus = int(s)
		} else if s, ok := statusVal.(int); ok {
			c.status.LastPollStatus = s
		}
	}

	if e.Level == "INFO" && strings.Contains(strings.ToLower(e.Message), "poll") {
		c.status.LastActiveAt = e.Timestamp
		// 成功轮询活动触发自愈复位：只要渠道处于就绪态且无活跃 HTTP 错误响应，清空历史瞬态错误
		if c.status.ReadyForChatGPT && (c.status.LastPollStatus == 200 || c.status.LastPollStatus == 204 || c.status.LastPollStatus == 0) {
			c.status.LastPollError = ""
		}
	}
}

// ProcessLogEntryForTest allows test suites to inject simulated log entries.
func (c *Controller) ProcessLogEntryForTest(e ChannelLogEntry) {
	c.processLogEntry(e)
}

// SetReadyForChatGPTForTest allows unit tests to set channel readiness for state flow tests.
func (c *Controller) SetReadyForChatGPTForTest(ready bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.status.ReadyForChatGPT = ready
}

// SetStateChangeCallback registers a listener for state transitions (e.g. for Watchdog Supervisor).
func (c *Controller) SetStateChangeCallback(fn func(Status)) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.onStateChange = fn
}

// SetUserPaused marks whether tunnel was explicitly stopped by user intent.
func (c *Controller) SetUserPaused(paused bool) {
	c.mu.Lock()
	c.userPaused = paused
	c.status.UserPaused = paused
	c.mu.Unlock()
}

// IsUserPaused reports whether user explicitly paused the tunnel.
func (c *Controller) IsUserPaused() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.userPaused
}

// LastConfig returns the last known tunnel configuration.
func (c *Controller) LastConfig() config.TunnelConfig {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.lastCfg
}

// SetBackoffStatus records a waiting state between auto-retry attempts.
func (c *Controller) SetBackoffStatus(retryCount int, nextRetry time.Time, errMsg string) {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.status.State = StateBackoffWait
	c.status.RetryCount = retryCount
	c.status.NextRetryAt = nextRetry
	c.status.ErrorMessage = errMsg
	c.status.ReadyForChatGPT = false
}

// Status returns current tunnel condition.
func (c *Controller) Status() Status {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.status
}

// Logs returns recent wire and polling log entries from the tunnel buffer.
func (c *Controller) Logs(limit int) []ChannelLogEntry {
	if c.logBuf == nil {
		return []ChannelLogEntry{}
	}
	return c.logBuf.Entries(limit)
}

// ClearLogs flushes all entries in the wire log buffer.
func (c *Controller) ClearLogs() {
	if c.logBuf != nil {
		c.logBuf.Clear()
	}
}

// Diagnose runs live network diagnostic probes against OpenAI control plane.
func (c *Controller) Diagnose(ctx context.Context, customProxy string) *DiagnosticResult {
	c.mu.Lock()
	baseURL := c.lastCfg.ControlPlaneBaseURL
	proxyURL := c.lastCfg.ProxyURL
	if customProxy != "" {
		proxyURL = customProxy
	}
	c.mu.Unlock()

	return DiagnoseControlPlane(ctx, baseURL, proxyURL)
}

// NormalizeProxyURL cleans and adjusts proxy schemes for DNS security (e.g. socks5:// -> socks5h://)
func NormalizeProxyURL(raw string) string {
	trimmed := strings.TrimSpace(raw)
	if trimmed == "" {
		return ""
	}
	// Upgrade socks5:// to socks5h:// so the proxy performs remote DNS lookup, preventing local DNS poisoning
	if strings.HasPrefix(strings.ToLower(trimmed), "socks5://") {
		return "socks5h://" + trimmed[len("socks5://"):]
	}
	return trimmed
}

// ApplyProxyEnvironment sets or unsets relevant egress proxy environment variables across runtimes.
func ApplyProxyEnvironment(proxyURL string) {
	effective := NormalizeProxyURL(proxyURL)
	if effective != "" {
		os.Setenv("HTTPS_PROXY", effective)
		os.Setenv("HTTP_PROXY", effective)
		os.Setenv("ALL_PROXY", effective)
		os.Setenv("https_proxy", effective)
		os.Setenv("http_proxy", effective)
		os.Setenv("all_proxy", effective)

		// For HTTP/HTTPS proxies, inject official tunnel-client SDK specific proxy variables
		if strings.HasPrefix(strings.ToLower(effective), "http://") || strings.HasPrefix(strings.ToLower(effective), "https://") {
			os.Setenv("CONTROL_PLANE_HTTP_PROXY", effective)
			os.Setenv("TUNNEL_CLIENT_HTTP_PROXY", effective)
		} else {
			os.Unsetenv("CONTROL_PLANE_HTTP_PROXY")
			os.Unsetenv("TUNNEL_CLIENT_HTTP_PROXY")
		}
	} else {
		os.Unsetenv("HTTPS_PROXY")
		os.Unsetenv("HTTP_PROXY")
		os.Unsetenv("ALL_PROXY")
		os.Unsetenv("https_proxy")
		os.Unsetenv("http_proxy")
		os.Unsetenv("all_proxy")
		os.Unsetenv("CONTROL_PLANE_HTTP_PROXY")
		os.Unsetenv("TUNNEL_CLIENT_HTTP_PROXY")
	}
}

// ResolvePollTimeout calculates adaptive poll timeout and deadline guardrail.
// Defaults to 20s poll wait (to safely precede common 30s proxy idle cuts) and 10s guardrail,
// with override support via MCP_BRIDGE_POLL_TIMEOUT_SEC.
func ResolvePollTimeout() (time.Duration, time.Duration) {
	pollTimeout := 20 * time.Second
	pollGuardrail := 10 * time.Second

	if envTimeout := os.Getenv("MCP_BRIDGE_POLL_TIMEOUT_SEC"); envTimeout != "" {
		if sec, err := strconv.Atoi(envTimeout); err == nil && sec >= 5 && sec <= 60 {
			pollTimeout = time.Duration(sec) * time.Second
			pollGuardrail = time.Duration(max(5, sec/2)) * time.Second
		}
	}
	return pollTimeout, pollGuardrail
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

	// Apply egress proxy environment configuration if specified
	proxyURL := strings.TrimSpace(tunnelCfg.ProxyURL)
	effectiveProxy := NormalizeProxyURL(proxyURL)
	ApplyProxyEnvironment(effectiveProxy)

	c.status = Status{
		State:          StateConnecting,
		TunnelID:       tunnelCfg.TunnelID,
		EffectiveProxy: effectiveProxy,
	}

	// Create in-memory transport pair between facade server and tunnel-client
	clientTransport, serverTransport := mcp.NewInMemoryTransports()

	// Run facade server on server transport
	serverCtx, serverCancel := context.WithCancel(context.Background())
	go func() {
		_ = c.server.Run(serverCtx, serverTransport)
	}()

	pollTimeout, pollGuardrail := ResolvePollTimeout()

	clientCfg := tunnelclient.Config{
		TunnelID:              tunnelCfg.TunnelID,
		APIKey:                apiKey,
		ControlPlaneBaseURL:   tunnelCfg.ControlPlaneBaseURL,
		OrganizationID:        tunnelCfg.OrganizationID,
		PollTimeout:           pollTimeout,
		PollDeadlineGuardrail: pollGuardrail,
		LogLevel:              slog.LevelDebug,
		LogWriter:             c.logBuf,
	}

	client, err := tunnelclient.New(clientCfg, clientTransport)
	if err != nil {
		serverCancel()
		c.status = Status{
			State:          StateDegraded,
			TunnelID:       tunnelCfg.TunnelID,
			ErrorMessage:   err.Error(),
			EffectiveProxy: effectiveProxy,
		}
		return fmt.Errorf("failed to construct tunnel client: %w", err)
	}

	startCtx, startCancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer startCancel()
	if err := client.Start(startCtx); err != nil {
		serverCancel()
		c.status = Status{
			State:          StateDegraded,
			TunnelID:       tunnelCfg.TunnelID,
			ErrorMessage:   err.Error(),
			EffectiveProxy: effectiveProxy,
		}
		return fmt.Errorf("failed to start tunnel client: %w", err)
	}

	c.lastCfg = tunnelCfg
	c.userPaused = false
	c.status.UserPaused = false
	c.status.RetryCount = 0
	c.status.NextRetryAt = time.Time{}
	c.client = client
	c.status.LastActiveAt = time.Now()

	stopCh := make(chan struct{})
	c.stopCh = stopCh
	startTime := time.Now()

	// Asynchronously monitor client readiness (first successful control-plane poll handshake)
	readyCh := client.Ready()
	go func() {
		select {
		case <-readyCh:
			c.mu.Lock()
			if c.client == client && c.status.State == StateConnecting {
				c.status.State = StateConnected
				c.status.ReadyForChatGPT = true
				c.status.InitialPollMs = time.Since(startTime).Milliseconds()
				c.status.LastActiveAt = time.Now()
				c.status.ErrorMessage = ""
				c.status.LastPollError = ""
				st := c.status
				cb := c.onStateChange
				c.mu.Unlock()

				c.logger.Info("OpenAI Tunnel ready for ChatGPT traffic",
					slog.String("tunnelId", tunnelCfg.TunnelID),
					slog.Int64("initialPollMs", st.InitialPollMs))

				if cb != nil {
					cb(st)
				}
			} else {
				c.mu.Unlock()
			}
		case <-stopCh:
			return
		case <-client.Done():
			return
		}
	}()

	// Background watcher for unexpected termination
	go func(client *tunnelclient.Client, stopCh <-chan struct{}) {
		select {
		case <-client.Done():
			c.mu.Lock()
			c.status.ReadyForChatGPT = false
			if c.status.State == StateConnected || c.status.State == StateConnecting {
				c.status.State = StateDegraded
				if c.status.LastPollError != "" {
					c.status.ErrorMessage = fmt.Sprintf("tunnel client runtime terminated: %s", c.status.LastPollError)
				} else {
					c.status.ErrorMessage = "tunnel client runtime terminated unexpectedly"
				}
			}
			st := c.status
			cb := c.onStateChange
			c.mu.Unlock()
			serverCancel()
			if cb != nil {
				cb(st)
			}
		case <-stopCh:
			serverCancel()
		}
	}(client, stopCh)

	return nil
}

// Stop halts control-plane polling and shuts down the embedded tunnel client.
func (c *Controller) Stop(ctx context.Context) error {
	c.mu.Lock()
	defer c.mu.Unlock()

	c.userPaused = true
	c.status.UserPaused = true
	c.status.ReadyForChatGPT = false

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

// Reload cleanly terminates any running tunnel client, unpauses, and restarts with updated configuration.
func (c *Controller) Reload(ctx context.Context, tunnelCfg config.TunnelConfig) error {
	_ = c.Stop(ctx)
	c.SetUserPaused(false)
	return c.Start(ctx, tunnelCfg)
}
