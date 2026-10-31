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
	BackendCount     int    `json:"backendCount"`
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
	DurationMs       int64     `json:"durationMs"`
	Status           string    `json:"status"`
	ErrorCode        string    `json:"errorCode,omitempty"`
	ResultSizeBytes  int64     `json:"resultSizeBytes"`
	EffectUncertain  bool      `json:"effectUncertain"`
}

// SaveTunnelRequestDTO mirrors SaveTunnelRequest in C# contracts.
type SaveTunnelRequestDTO struct {
	TunnelID     string `json:"tunnelId"`
	SecretAction string `json:"secretAction"`
	TunnelKey    string `json:"tunnelKey,omitempty"`
}

// SaveServerRequestDTO mirrors SaveServerRequest in C# contracts.
type SaveServerRequestDTO struct {
	Name             string   `json:"name"`
	Transport        string   `json:"transport"`
	Command          string   `json:"command,omitempty"`
	Arguments        []string `json:"arguments,omitempty"`
	WorkingDirectory string   `json:"workingDirectory,omitempty"`
	URL              string   `json:"url,omitempty"`
	SecretAction     string   `json:"secretAction"`
	SecretValue      string   `json:"secretValue,omitempty"`
	Enabled          bool     `json:"enabled"`
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
	Success         bool   `json:"success"`
	ActiveRevision  uint64 `json:"activeRevision"`
	ActiveToolCount int    `json:"activeToolCount"`
	Message         string `json:"message,omitempty"`
}

// AuditPageDTO mirrors McpBridgeAuditPage in C# contracts.
type AuditPageDTO struct {
	Items      []AuditItemDTO `json:"items"`
	NextCursor string         `json:"nextCursor,omitempty"`
}
