package admin

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"sync"
	"time"
)

const (
	maxRingEntries   = 1000
	maxLogFileBytes  = 8 * 1024 * 1024 // 8 MiB per file (2 files = 16 MiB total)
	auditLogFileName = "audit.log"
)

// AuditLogger manages persistent and in-memory bounded audit event tracking.
type AuditLogger struct {
	mu       sync.Mutex
	stateDir string
	logPath  string
	file     *os.File
	ring     []AuditItemDTO
	fileSize int64
}

// NewAuditLogger initializes the audit logger.
func NewAuditLogger(stateDir string) *AuditLogger {
	logPath := filepath.Join(stateDir, auditLogFileName)

	var initialSize int64
	if fi, err := os.Stat(logPath); err == nil {
		initialSize = fi.Size()
	}

	f, _ := os.OpenFile(logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)

	return &AuditLogger{
		stateDir: stateDir,
		logPath:  logPath,
		file:     f,
		ring:     make([]AuditItemDTO, 0, maxRingEntries),
		fileSize: initialSize,
	}
}

// Record logs an audit item both in-memory and to disk.
func (a *AuditLogger) Record(item AuditItemDTO) {
	a.mu.Lock()
	defer a.mu.Unlock()

	if item.Timestamp.IsZero() {
		item.Timestamp = time.Now()
	}

	// 1. Ring buffer
	if len(a.ring) >= maxRingEntries {
		a.ring = a.ring[1:]
	}
	a.ring = append(a.ring, item)

	// 2. Persistent file append
	if a.file != nil {
		data, err := json.Marshal(item)
		if err == nil {
			line := append(data, '\n')
			if a.fileSize+int64(len(line)) > maxLogFileBytes {
				a.rotateLocked()
			}
			if n, err := a.file.Write(line); err == nil {
				a.fileSize += int64(n)
			}
		}
	}
}

// Log records an audit entry conforming to watchdog.AuditSink.
func (a *AuditLogger) Log(action, target, status, details string) {
	a.Record(AuditItemDTO{
		Timestamp:        time.Now(),
		Action:           action,
		Target:           target,
		ServerID:         target,
		ExportedToolName: action,
		Status:           status,
		Details:          details,
		ErrorCode:        details,
	})
}

func (a *AuditLogger) rotateLocked() {
	if a.file != nil {
		_ = a.file.Close()
		a.file = nil
	}

	oldPath := a.logPath + ".old"
	_ = os.Remove(oldPath)
	_ = os.Rename(a.logPath, oldPath)

	f, err := os.OpenFile(a.logPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0600)
	if err == nil {
		a.file = f
		a.fileSize = 0
	}
}

// List returns items from the ring buffer with opaque pagination.
func (a *AuditLogger) List(cursor string, limit int) ([]AuditItemDTO, string) {
	a.mu.Lock()
	defer a.mu.Unlock()

	if limit <= 0 || limit > 100 {
		limit = 50
	}

	startIndex := 0
	if cursor != "" {
		if parsed, err := strconv.Atoi(cursor); err == nil && parsed >= 0 {
			startIndex = parsed
		}
	}

	total := len(a.ring)
	if startIndex >= total {
		return []AuditItemDTO{}, ""
	}

	endIndex := startIndex + limit
	var nextCursor string
	if endIndex >= total {
		endIndex = total
	} else {
		nextCursor = fmt.Sprintf("%d", endIndex)
	}

	// Return copy in reverse chronological order
	var result []AuditItemDTO
	for i := endIndex - 1; i >= startIndex; i-- {
		result = append(result, a.ring[i])
	}

	return result, nextCursor
}

// Close closes the underlying audit file.
func (a *AuditLogger) Close() error {
	a.mu.Lock()
	defer a.mu.Unlock()
	if a.file != nil {
		err := a.file.Close()
		a.file = nil
		return err
	}
	return nil
}
