package tunnel

import (
	"encoding/json"
	"strings"
	"testing"
	"time"
)

func TestChannelLogBuffer(t *testing.T) {
	var lastErr string
	buf := NewChannelLogBuffer(5, func(entry ChannelLogEntry) {
		if entry.Level == "WARN" || entry.Level == "ERROR" {
			if errMsg, ok := entry.Attrs["error"].(string); ok {
				lastErr = errMsg
			} else {
				lastErr = entry.Message
			}
		}
	})

	// 1. Write logfmt line
	logfmtLine := `time=2026-11-02T12:00:00Z level=WARN msg="poll failed; backing off" error="network timeout" retry_in_ms=5000` + "\n"
	_, err := buf.Write([]byte(logfmtLine))
	if err != nil {
		t.Fatalf("unexpected write error: %v", err)
	}

	if lastErr != "network timeout" {
		t.Errorf("expected lastErr 'network timeout', got %q", lastErr)
	}

	entries := buf.Entries(10)
	if len(entries) != 1 {
		t.Fatalf("expected 1 entry, got %d", len(entries))
	}
	if entries[0].Message != "poll failed; backing off" {
		t.Errorf("expected parsed message, got %q", entries[0].Message)
	}
	if entries[0].Attrs["retry_in_ms"] != int64(5000) {
		t.Errorf("expected retry_in_ms=5000, got %v", entries[0].Attrs["retry_in_ms"])
	}

	// 2. Write JSON line
	jsonMap := map[string]any{
		"time":    time.Now().Format(time.RFC3339Nano),
		"level":   "info",
		"msg":     "tunnel metadata fetched",
		"status":  200,
		"channel": "main",
	}
	jsonData, _ := json.Marshal(jsonMap)
	_, _ = buf.Write(append(jsonData, '\n'))

	entries = buf.Entries(10)
	if len(entries) != 2 {
		t.Fatalf("expected 2 entries, got %d", len(entries))
	}

	// 3. Ring buffer overflow test
	for i := 0; i < 10; i++ {
		_, _ = buf.Write([]byte("plain log message\n"))
	}
	entries = buf.Entries(0)
	if len(entries) != 5 {
		t.Errorf("expected buffer capped at 5, got %d", len(entries))
	}

	// 4. Clear test
	buf.Clear()
	entries = buf.Entries(10)
	if len(entries) != 0 {
		t.Errorf("expected 0 entries after clear, got %d", len(entries))
	}
}

func TestDiagnoseControlPlane_InvalidHost(t *testing.T) {
	res := DiagnoseControlPlane(t.Context(), "http://invalid-non-existing-host-12345.local:80", "")
	if res.TcpConnected {
		t.Errorf("expected tcp connection to fail for non-existing host")
	}
	if !strings.Contains(res.Error, "failed") && !strings.Contains(res.Error, "lookup") && !strings.Contains(res.Error, "dial") {
		t.Errorf("expected error message in diagnostic probe, got: %q", res.Error)
	}
	if res.Suggestion == "" {
		t.Errorf("expected diagnostic suggestion to be populated")
	}
}
