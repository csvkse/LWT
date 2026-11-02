package runtime

import (
	"time"

	"linuxwebtool.local/mcp-bridge/internal/watchdog"
)

// BackendSummary captures aggregated backend status.
type BackendSummary struct {
	Total    int `json:"total"`
	Enabled  int `json:"enabled"`
	Active   int `json:"active"`
	Degraded int `json:"degraded"`
}

// HealthStatus conveys the comprehensive condition of the Bridge worker.
type HealthStatus struct {
	WorkerAlive      bool                       `json:"workerAlive"`
	WorkerRunID      string                     `json:"workerRunId"`
	AdminReady       bool                       `json:"adminReady"`
	TunnelState      string                     `json:"tunnelState"`
	BackendCounts    BackendSummary             `json:"backendCounts"`
	DraftRevision    uint64                     `json:"draftRevision"`
	DesiredRevision  uint64                     `json:"desiredRevision"`
	ActiveRevision   uint64                     `json:"activeRevision"`
	RecoveryRequired bool                       `json:"recoveryRequired"`
	Hydrating        bool                       `json:"hydrating"`
	SupervisorStatus *watchdog.SupervisorStatus `json:"supervisor,omitempty"`
	Timestamp        time.Time                  `json:"timestamp"`
}
