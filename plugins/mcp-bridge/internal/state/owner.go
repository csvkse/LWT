package state

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"time"
)

const ownerLockFileName = "owner.lock"

// OwnerRecord records process identity and run metadata for exclusive execution.
type OwnerRecord struct {
	PID       int       `json:"pid"`
	RunID     string    `json:"runId"`
	Hostname  string    `json:"hostname"`
	StartedAt time.Time `json:"startedAt"`
}

// OwnerLock represents an acquired single-owner execution lock.
type OwnerLock struct {
	lockFile *os.File
	path     string
	record   OwnerRecord
}

// AcquireOwnerLock attempts to acquire exclusive ownership of the state directory.
func AcquireOwnerLock(stateDir string, runID string) (*OwnerLock, error) {
	lockPath := filepath.Join(stateDir, ownerLockFileName)

	// Check if existing lock file exists and points to an active process
	if existingData, err := os.ReadFile(lockPath); err == nil {
		var existing OwnerRecord
		if err := json.Unmarshal(existingData, &existing); err == nil && existing.PID > 0 {
			if isProcessAlive(existing.PID) {
				return nil, fmt.Errorf("state directory is locked by active process PID %d (runId: %s, started: %s)",
					existing.PID, existing.RunID, existing.StartedAt.Format(time.RFC3339))
			}
		}
	}

	// Try to open with exclusive write access
	f, err := os.OpenFile(lockPath, os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, fmt.Errorf("failed to open owner lock file: %w", err)
	}

	hostname, _ := os.Hostname()
	record := OwnerRecord{
		PID:       os.Getpid(),
		RunID:     runID,
		Hostname:  hostname,
		StartedAt: time.Now(),
	}

	data, err := json.MarshalIndent(record, "", "  ")
	if err != nil {
		_ = f.Close()
		return nil, err
	}

	if err := f.Truncate(0); err != nil {
		_ = f.Close()
		return nil, err
	}
	if _, err := f.Write(data); err != nil {
		_ = f.Close()
		return nil, err
	}

	return &OwnerLock{
		lockFile: f,
		path:     lockPath,
		record:   record,
	}, nil
}

// Release releases the lock and removes the lock file.
func (l *OwnerLock) Release() error {
	if l == nil || l.lockFile == nil {
		return nil
	}
	_ = l.lockFile.Close()
	return os.Remove(l.path)
}

// Record returns the active owner record.
func (l *OwnerLock) Record() OwnerRecord {
	return l.record
}

// isProcessAlive reports whether a process with given PID is running.
func isProcessAlive(pid int) bool {
	process, err := os.FindProcess(pid)
	if err != nil {
		return false
	}
	// On Windows, FindProcess always succeeds, but sending signal 0 checks on Unix.
	// On Windows, checking whether process can be queried is standard.
	return checkProcessRunning(process, pid)
}
