package tunnel

import (
	"bytes"
	"encoding/json"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

// ChannelLogEntry represents an observed wire/tunnel runtime log record.
type ChannelLogEntry struct {
	Timestamp time.Time      `json:"timestamp"`
	Level     string         `json:"level"`
	Message   string         `json:"message"`
	Attrs     map[string]any `json:"attrs,omitempty"`
}

// ChannelLogBuffer implements a thread-safe circular ring buffer for tunnel client logs.
type ChannelLogBuffer struct {
	mu       sync.RWMutex
	capacity int
	entries  []ChannelLogEntry
	cursor   int
	full     bool
	onEntry  func(ChannelLogEntry)
}

// NewChannelLogBuffer creates a circular buffer of given capacity.
func NewChannelLogBuffer(capacity int, onEntry func(ChannelLogEntry)) *ChannelLogBuffer {
	if capacity <= 0 {
		capacity = 200
	}
	return &ChannelLogBuffer{
		capacity: capacity,
		entries:  make([]ChannelLogEntry, capacity),
		onEntry:  onEntry,
	}
}

var (
	logfmtRegex = regexp.MustCompile(`([a-zA-Z0-9_\-\.]+)=("[^"]*"|[^\s]+)`)
)

// Write implements io.Writer to intercept logs from tunnelclient SDK.
func (b *ChannelLogBuffer) Write(p []byte) (n int, err error) {
	n = len(p)
	lines := bytes.Split(p, []byte("\n"))
	for _, line := range lines {
		line = bytes.TrimSpace(line)
		if len(line) == 0 {
			continue
		}
		entry := b.parseLine(line)
		b.Append(entry)
	}
	return n, nil
}

// Append adds one parsed entry into the ring buffer.
func (b *ChannelLogBuffer) Append(entry ChannelLogEntry) {
	b.mu.Lock()
	b.entries[b.cursor] = entry
	b.cursor = (b.cursor + 1) % b.capacity
	if b.cursor == 0 {
		b.full = true
	}
	b.mu.Unlock()

	if b.onEntry != nil {
		b.onEntry(entry)
	}
}

// Entries returns a copy of the latest entries in chronological order.
func (b *ChannelLogBuffer) Entries(limit int) []ChannelLogEntry {
	b.mu.RLock()
	defer b.mu.RUnlock()

	count := b.cursor
	if b.full {
		count = b.capacity
	}
	if count == 0 {
		return []ChannelLogEntry{}
	}

	result := make([]ChannelLogEntry, 0, count)
	if !b.full {
		for i := 0; i < b.cursor; i++ {
			result = append(result, b.entries[i])
		}
	} else {
		for i := b.cursor; i < b.capacity; i++ {
			result = append(result, b.entries[i])
		}
		for i := 0; i < b.cursor; i++ {
			result = append(result, b.entries[i])
		}
	}

	if limit > 0 && len(result) > limit {
		result = result[len(result)-limit:]
	}
	return result
}

// Clear flushes all entries in the ring buffer.
func (b *ChannelLogBuffer) Clear() {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.cursor = 0
	b.full = false
}

// parseLine attempts JSON first, then falls back to slog text / logfmt.
func (b *ChannelLogBuffer) parseLine(data []byte) ChannelLogEntry {
	entry := ChannelLogEntry{
		Timestamp: time.Now(),
		Level:     "INFO",
		Attrs:     make(map[string]any),
	}

	// Try JSON
	var raw map[string]any
	if err := json.Unmarshal(data, &raw); err == nil && len(raw) > 0 {
		if tVal, ok := raw["time"].(string); ok {
			if parsed, err := time.Parse(time.RFC3339Nano, tVal); err == nil {
				entry.Timestamp = parsed
			}
		}
		if lvl, ok := raw["level"].(string); ok {
			entry.Level = strings.ToUpper(lvl)
		}
		if msg, ok := raw["msg"].(string); ok {
			entry.Message = msg
		} else if msg, ok := raw["message"].(string); ok {
			entry.Message = msg
		}
		for k, v := range raw {
			if k != "time" && k != "level" && k != "msg" && k != "message" {
				entry.Attrs[k] = v
			}
		}
		return entry
	}

	// Fallback logfmt (e.g. time=... level=... msg=...)
	str := string(data)
	matches := logfmtRegex.FindAllStringSubmatch(str, -1)
	if len(matches) > 0 {
		for _, m := range matches {
			k := m[1]
			v := strings.Trim(m[2], `"`)
			switch strings.ToLower(k) {
			case "time":
				if parsed, err := time.Parse(time.RFC3339Nano, v); err == nil {
					entry.Timestamp = parsed
				}
			case "level":
				entry.Level = strings.ToUpper(v)
			case "msg", "message":
				entry.Message = v
			default:
				if intVal, err := strconv.ParseInt(v, 10, 64); err == nil {
					entry.Attrs[k] = intVal
				} else if boolVal, err := strconv.ParseBool(v); err == nil {
					entry.Attrs[k] = boolVal
				} else {
					entry.Attrs[k] = v
				}
			}
		}
		if entry.Message == "" {
			entry.Message = str
		}
		return entry
	}

	// Raw unformatted string
	entry.Message = str
	return entry
}
