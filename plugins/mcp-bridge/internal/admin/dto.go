package admin

import (
	"encoding/json"
	"time"
)

// HandshakeRequest represents payload to POST /control/handshake.
type HandshakeRequest struct {
	ProtocolVersion int    `json:"protocolVersion"`
	WorkerID        string `json:"workerId"`
	RunID           string `json:"runId"`
}

// HandshakeResponse represents response from POST /control/handshake.
type HandshakeResponse struct {
	Success         bool   `json:"success"`
	ProtocolVersion int    `json:"protocolVersion"`
	WorkerID        string `json:"workerId"`
	WorkerRunID     string `json:"workerRunId"`
	Message         string `json:"message,omitempty"`
}

// StatusDTO mirrors McpBridgeStatusDto in C# contracts.
type StatusDTO struct {
	WorkerRunID      string `json:"workerRunId"`
	ProcessAlive     bool   `json:"processAlive"`
	AdminReady       bool   `json:"adminReady"`
	TunnelState      string `json:"tunnelState"`
	TunnelID         string `json:"tunnelId,omitempty"`
	ProxyURL         string `json:"proxyUrl,omitempty"`
	HasTunnelKey     bool   `json:"hasTunnelKey"`
	AutoConnect      bool   `json:"autoConnect"`
	UserPaused       bool   `json:"userPaused"`
	Hydrating        bool   `json:"hydrating"`
	ReadyForChatGPT  bool   `json:"readyForChatgpt"`
	InitialPollMs    int64  `json:"initialPollMs,omitempty"`
	LastPollStatus   int    `json:"lastPollStatus,omitempty"`
	LastPollError    string `json:"lastPollError,omitempty"`
	EffectiveProxy   string `json:"effectiveProxy,omitempty"`
	BackendCount     int    `json:"backendCount"`
	ActiveToolCount  int    `json:"activeToolCount"`
	DraftRevision    uint64 `json:"draftRevision"`
	DesiredRevision  uint64 `json:"desiredRevision"`
	ActiveRevision   uint64 `json:"activeRevision"`
	RecoveryRequired bool   `json:"recoveryRequired"`
	LastError        string `json:"lastError,omitempty"`
}

// ServerDTO mirrors McpServerDto in C# contracts.
type ServerDTO struct {
	ID               string   `json:"id"`
	Name             string   `json:"name"`
	Transport        string   `json:"transport"`
	Command          string   `json:"command,omitempty"`
	Arguments        []string `json:"arguments,omitempty"`
	WorkingDirectory string   `json:"workingDirectory,omitempty"`
	URL              string   `json:"url,omitempty"`
	HasSecret        bool     `json:"hasSecret"`
	Enabled          bool     `json:"enabled"`
	ToolCount        int      `json:"toolCount"`
	Status           string   `json:"status"`
	Error            string   `json:"error,omitempty"`
}

// ToolDTO mirrors McpToolDto in C# contracts.
type ToolDTO struct {
	ExportedName string          `json:"exportedName"`
	ServerID     string          `json:"serverId"`
	OriginalName string          `json:"originalName"`
	Description  string          `json:"description,omitempty"`
	InputSchema  json.RawMessage `json:"inputSchema,omitempty"`
	Selected     bool            `json:"selected"`
	SchemaDigest string          `json:"schemaDigest"`
}

// AuditItemDTO mirrors McpBridgeAuditItem in C# contracts.
type AuditItemDTO struct {
	Timestamp        time.Time `json:"timestamp"`
	RunID            string    `json:"runId"`
	ConfigRevision   uint64    `json:"configRevision"`
	ServerID         string    `json:"serverId"`
	ExportedToolName string    `json:"exportedToolName"`
	Action           string    `json:"action,omitempty"`
	Target           string    `json:"target,omitempty"`
	DurationMs       int64     `json:"durationMs"`
	Status           string    `json:"status"`
	ErrorCode        string    `json:"errorCode,omitempty"`
	Details          string    `json:"details,omitempty"`
	ResultSizeBytes  int64     `json:"resultSizeBytes"`
	EffectUncertain  bool      `json:"effectUncertain"`
}

// SaveTunnelRequestDTO mirrors SaveTunnelRequest in C# contracts.
type SaveTunnelRequestDTO struct {
	TunnelID         string  `json:"tunnelId"`
	SecretAction     string  `json:"secretAction"`
	TunnelKey        string  `json:"tunnelKey,omitempty"`
	PresharedKey     string  `json:"presharedKey,omitempty"`     // Webview UI & API alias
	ProxyURL         *string `json:"proxyUrl,omitempty"`         // Egress HTTP/SOCKS5 proxy URL
	AutoConnect      *bool   `json:"autoConnect,omitempty"`      // Auto-connect on startup
	ApplyNow         bool    `json:"applyNow,omitempty"`         // Save & Apply atomically
	ExpectedRevision uint64  `json:"expectedRevision,omitempty"` // Optimistic concurrency check
}

// ChannelLogEntryDTO represents one wire/polling event from the tunnel client runtime.
type ChannelLogEntryDTO struct {
	Timestamp time.Time      `json:"timestamp"`
	Level     string         `json:"level"`
	Message   string         `json:"message"`
	Attrs     map[string]any `json:"attrs,omitempty"`
}

// DiagnosticProbeResultDTO holds network diagnostics results for OpenAI control-plane egress.
type DiagnosticProbeResultDTO struct {
	TargetHost     string   `json:"targetHost"`
	TargetPort     int      `json:"targetPort"`
	DnsResolvedIPs []string `json:"dnsResolvedIPs"`
	DnsDurationMs  int64    `json:"dnsDurationMs"`
	TcpConnected   bool     `json:"tcpConnected"`
	TcpDurationMs  int64    `json:"tcpDurationMs"`
	TlsHandshakeOk bool     `json:"tlsHandshakeOk"`
	TlsDurationMs  int64    `json:"tlsDurationMs"`
	TlsVersion     string   `json:"tlsVersion,omitempty"`
	EffectiveProxy string   `json:"effectiveProxy,omitempty"`
	Error          string   `json:"error,omitempty"`
	Suggestion     string   `json:"suggestion,omitempty"`
}

// SaveServerRequestDTO mirrors SaveServerRequest in C# contracts.
type SaveServerRequestDTO struct {
	Name             string            `json:"name"`
	Transport        string            `json:"transport"`
	Command          string            `json:"command,omitempty"`
	Arguments        []string          `json:"arguments,omitempty"`
	WorkingDirectory string            `json:"workingDirectory,omitempty"`
	URL              string            `json:"url,omitempty"`
	SecretAction     string            `json:"secretAction"`
	SecretValue      string            `json:"secretValue,omitempty"`
	Enabled          bool              `json:"enabled"`
	Env              map[string]string `json:"env,omitempty"`
	Headers          map[string]string `json:"headers,omitempty"`
	ApplyNow         bool              `json:"applyNow,omitempty"`
}

// ImportServersRequestDTO defines payload for POST /control/invoke/servers.import.
type ImportServersRequestDTO struct {
	RawJSON            string `json:"rawJson,omitempty"`
	ConflictResolution string `json:"conflictResolution,omitempty"` // "overwrite" (default), "skip", "rename"
	ExpectedRevision   uint64 `json:"expectedRevision,omitempty"`
	ApplyNow           bool   `json:"applyNow,omitempty"`
}

// ImportServersResultDTO defines response for POST /control/invoke/servers.import.
type ImportServersResultDTO struct {
	Success     bool     `json:"success"`
	Revision    uint64   `json:"revision"`
	ImportedIDs []string `json:"importedIds"`
	SkippedIDs  []string `json:"skippedIds"`
	Errors      []string `json:"errors,omitempty"`
	Message     string   `json:"message,omitempty"`
}

// SaveSelectionRequestDTO mirrors SaveSelectionRequest in C# contracts.
type SaveSelectionRequestDTO struct {
	ExpectedRevision      uint64   `json:"expectedRevision"`
	SelectedExportedNames []string `json:"selectedExportedNames"`
}

// ApplyConfigRequestDTO mirrors ApplyConfigRequest in C# contracts.
type ApplyConfigRequestDTO struct {
	ExpectedRevision uint64 `json:"expectedRevision"`
}

// MutationResultDTO mirrors McpBridgeMutationResult in C# contracts.
type MutationResultDTO struct {
	Success  bool   `json:"success"`
	Revision uint64 `json:"revision"`
	Message  string `json:"message,omitempty"`
}

// OperationResultDTO mirrors McpBridgeOperationResult in C# contracts.
type OperationResultDTO struct {
	Success bool   `json:"success"`
	Message string `json:"message,omitempty"`
	JobID   string `json:"jobId,omitempty"`
}

// ServersResultDTO mirrors McpBridgeServersResult in C# contracts.
type ServersResultDTO struct {
	Servers []ServerDTO `json:"servers"`
}

// ProbeResultDTO mirrors McpBridgeProbeResult in C# contracts.
type ProbeResultDTO struct {
	Success   bool   `json:"success"`
	Status    string `json:"status"`
	ToolCount int    `json:"toolCount"`
	Message   string `json:"message,omitempty"`
}

// DiscoveryResultDTO mirrors McpBridgeDiscoveryResult in C# contracts.
type DiscoveryResultDTO struct {
	Success         bool   `json:"success"`
	DiscoveredCount int    `json:"discoveredCount"`
	JobID           string `json:"jobId,omitempty"`
	Message         string `json:"message,omitempty"`
}

// ToolsPageDTO mirrors McpBridgeToolsPage in C# contracts.
type ToolsPageDTO struct {
	Tools            []ToolDTO `json:"tools"`
	NextCursor       string    `json:"nextCursor,omitempty"`
	SnapshotRevision uint64    `json:"snapshotRevision"`
}

// ApplyResultDTO mirrors McpBridgeApplyResult in C# contracts.
type ApplyResultDTO struct {
	Success         bool     `json:"success"`
	ActiveRevision  uint64   `json:"activeRevision"`
	ActiveToolCount int      `json:"activeToolCount"`
	DegradedServers []string `json:"degradedServers,omitempty"`
	Message         string   `json:"message,omitempty"`
}

// AuditPageDTO mirrors McpBridgeAuditPage in C# contracts.
type AuditPageDTO struct {
	Items      []AuditItemDTO `json:"items"`
	NextCursor string         `json:"nextCursor,omitempty"`
}

// CallToolRequestDTO defines payload for POST /control/invoke/tools.call.
type CallToolRequestDTO struct {
	Name      string          `json:"name"`                // Exported tool name (e.g. "server_tool") or tool name
	ServerID  string          `json:"serverId,omitempty"`  // Target backend server ID (optional)
	Arguments json.RawMessage `json:"arguments,omitempty"` // Arguments object
}

// CallToolResultDTO defines response for POST /control/invoke/tools.call.
type CallToolResultDTO struct {
	Success   bool   `json:"success"`
	IsError   bool   `json:"isError"`
	Content   any    `json:"content,omitempty"`
	LatencyMs int64  `json:"latencyMs"`
	Message   string `json:"message,omitempty"`
}

