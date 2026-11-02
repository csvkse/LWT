package watchdog

import (
	"context"
	"time"
)

// TargetType differentiates target communication nature.
type TargetType string

const (
	TargetTypeTunnel  TargetType = "Tunnel"
	TargetTypeBackend TargetType = "Backend"
)

// TargetStatus summarizes runtime health and recovery condition of a watched target.
type TargetStatus struct {
	ID           string       `json:"id"`
	Type         TargetType   `json:"type"`
	State        string       `json:"state"`
	CircuitState CircuitState `json:"circuitState"`
	RetryCount   int          `json:"retryCount"`
	NextRetryAt  time.Time    `json:"nextRetryAt,omitempty"`
	LastError    string       `json:"lastError,omitempty"`
	LastProbeAt  time.Time    `json:"lastProbeAt,omitempty"`
}

// WatchdogTarget provides unified life-cycle management for auto-recovery.
type WatchdogTarget interface {
	ID() string
	Type() TargetType
	CheckHealth(ctx context.Context) (healthy bool, err error)
	Reconnect(ctx context.Context) error
	CanSelfHeal() bool
}
