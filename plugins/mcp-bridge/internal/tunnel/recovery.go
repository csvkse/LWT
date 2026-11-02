package tunnel

import (
	"encoding/json"
	"os"
	"path/filepath"
	"sync"
	"time"
)

const recoveryFlagFileName = "recovery.flag"

// InFlightRecord records operations currently in-flight.
type InFlightRecord struct {
	RequestID    string    `json:"requestId"`
	ExportedName string    `json:"exportedName"`
	ServerID     string    `json:"serverId"`
	StartedAt    time.Time `json:"startedAt"`
}

// RecoveryManager manages in-flight bookkeeping and crash recovery flags.
type RecoveryManager struct {
	mu       sync.Mutex
	stateDir string
	inFlight map[string]InFlightRecord
}

// NewRecoveryManager initializes recovery tracking in the state directory.
func NewRecoveryManager(stateDir string) *RecoveryManager {
	return &RecoveryManager{
		stateDir: stateDir,
		inFlight: make(map[string]InFlightRecord),
	}
}

// IsRecoveryRequired checks if a prior run left uncertain in-flight executions.
func (m *RecoveryManager) IsRecoveryRequired() bool {
	flagPath := filepath.Join(m.stateDir, recoveryFlagFileName)
	_, err := os.Stat(flagPath)
	return err == nil
}

// ClearRecovery removes the recovery flag.
func (m *RecoveryManager) ClearRecovery() error {
	flagPath := filepath.Join(m.stateDir, recoveryFlagFileName)
	err := os.Remove(flagPath)
	if os.IsNotExist(err) {
		return nil
	}
	return err
}

// TrackInFlight adds an active operation.
func (m *RecoveryManager) TrackInFlight(rec InFlightRecord) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.inFlight[rec.RequestID] = rec
}

// Untrack removes a completed operation.
func (m *RecoveryManager) Untrack(requestID string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	delete(m.inFlight, requestID)
}

// MarkRecoveryRequired writes current in-flight state to recovery.flag.
func (m *RecoveryManager) MarkRecoveryRequired() error {
	m.mu.Lock()
	defer m.mu.Unlock()

	if len(m.inFlight) == 0 {
		return nil
	}

	data, err := json.MarshalIndent(m.inFlight, "", "  ")
	if err != nil {
		return err
	}

	flagPath := filepath.Join(m.stateDir, recoveryFlagFileName)
	return os.WriteFile(flagPath, data, 0600)
}
