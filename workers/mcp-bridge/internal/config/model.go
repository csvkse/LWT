package config

import (
	"encoding/json"
	"time"
)

// SecretAction defines the operation performed on a sensitive field.
type SecretAction string

const (
	SecretActionKeep    SecretAction = "keep"
	SecretActionReplace SecretAction = "replace"
	SecretActionClear   SecretAction = "clear"
)

// SecretMutation represents a write-only mutation for a secret value.
type SecretMutation struct {
	Action SecretAction `json:"action"`
	Value  string       `json:"value,omitempty"`
}

// TunnelConfig holds OpenAI Secure MCP Tunnel connection parameters.
type TunnelConfig struct {
	TunnelID            string `json:"tunnelId"`
	EncryptedKey        string `json:"encryptedKey,omitempty"`
	ControlPlaneBaseURL string `json:"controlPlaneBaseUrl,omitempty"`
	OrganizationID      string `json:"organizationId,omitempty"`
}

// TransportType describes the connection mechanism to the third-party backend.
type TransportType string

const (
	TransportStdio          TransportType = "stdio"
	TransportStreamableHTTP TransportType = "streamable-http"
	TransportSSE            TransportType = "sse"
)

// BackendSpec defines a registered third-party MCP backend server.
type BackendSpec struct {
	ID             string            `json:"id"`
	Name           string            `json:"name"`
	Transport      TransportType     `json:"transport"`
	Enabled        bool              `json:"enabled"`
	Command        string            `json:"command,omitempty"`
	Args           []string          `json:"args,omitempty"`
	Cwd            string            `json:"cwd,omitempty"`
	Env            map[string]string `json:"env,omitempty"`
	URL            string            `json:"url,omitempty"`
	Headers        map[string]string `json:"headers,omitempty"`
	EncryptedAuth  string            `json:"encryptedAuth,omitempty"`
	TimeoutSeconds int               `json:"timeoutSeconds,omitempty"`
	Revision       uint64            `json:"revision"`
	UpdatedAt      time.Time         `json:"updatedAt"`
}

// ServerSelection defines which tools of a backend are approved for exposure.
type ServerSelection struct {
	ServerID           string            `json:"serverId"`
	SelectedTools      []string          `json:"selectedTools"`
	ReviewFingerprint  string            `json:"reviewFingerprint"`
	ApprovedToolDigests map[string]string `json:"approvedToolDigests,omitempty"`
	ReviewedAt         time.Time         `json:"reviewedAt"`
}

// State represents the complete persistent configuration of the Bridge.
type State struct {
	DraftRevision   uint64                      `json:"draftRevision"`
	DesiredRevision uint64                      `json:"desiredRevision"`
	ActiveRevision  uint64                      `json:"activeRevision"`
	Tunnel          TunnelConfig                `json:"tunnel"`
	Servers         map[string]*BackendSpec     `json:"servers"`
	Selections      map[string]*ServerSelection `json:"selections"`
	LastAppliedAt   time.Time                   `json:"lastAppliedAt,omitempty"`
}

// NewInitialState returns a valid blank configuration state.
func NewInitialState() State {
	return State{
		DraftRevision:   1,
		DesiredRevision: 1,
		ActiveRevision:  0,
		Servers:         make(map[string]*BackendSpec),
		Selections:      make(map[string]*ServerSelection),
	}
}

// DeepCopy produces an independent clone of the State.
func (s State) DeepCopy() (State, error) {
	bytes, err := json.Marshal(s)
	if err != nil {
		return State{}, err
	}
	var clone State
	err = json.Unmarshal(bytes, &clone)
	return clone, err
}
