package server

import (
	"bytes"
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os"
	"testing"

	"lwt/disk-explorer/internal/capabilities"
	"lwt/disk-explorer/internal/fsm"
	"lwt/disk-explorer/internal/model"
)

func TestServerHealth(t *testing.T) {
	s := NewServer(ServerOptions{
		Secret:   "test-secret",
		Detector: capabilities.NewDetector(),
		Manager:  fsm.NewTaskManager(),
	})

	req := httptest.NewRequest(http.MethodGet, "/health", nil)
	rec := httptest.NewRecorder()

	s.Handler().ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200 OK, got %d", rec.Code)
	}

	var res map[string]any
	if err := json.Unmarshal(rec.Body.Bytes(), &res); err != nil {
		t.Fatalf("failed to parse json: %v", err)
	}

	if res["status"] != "healthy" {
		t.Errorf("expected status healthy, got %v", res["status"])
	}
}

func TestServerCapabilities(t *testing.T) {
	s := NewServer(ServerOptions{
		Secret:   "test-secret",
		Detector: capabilities.NewDetector(),
		Manager:  fsm.NewTaskManager(),
	})

	req := httptest.NewRequest(http.MethodGet, "/api/capabilities", nil)
	req.Header.Set("X-LWT-Internal-Secret", "test-secret")
	rec := httptest.NewRecorder()

	s.Handler().ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200 OK, got %d", rec.Code)
	}

	var res struct {
		Success bool                   `json:"success"`
		Data    model.CapabilityReport `json:"data"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &res); err != nil {
		t.Fatalf("failed to parse json: %v", err)
	}

	if !res.Success {
		t.Fatalf("expected success true")
	}

	if len(res.Data.AvailableTargets) == 0 {
		t.Fatalf("expected at least one target")
	}
}

func TestServerScanLifecycle(t *testing.T) {
	s := NewServer(ServerOptions{
		Secret:   "test-secret",
		Detector: capabilities.NewDetector(),
		Manager:  fsm.NewTaskManager(),
	})

	tempDir := t.TempDir()

	body, _ := json.Marshal(model.ScanRequest{
		Target:   "container",
		Path:     tempDir,
		MaxDepth: 2,
	})

	req := httptest.NewRequest(http.MethodPost, "/api/scans", bytes.NewReader(body))
	req.Header.Set("X-LWT-Internal-Secret", "test-secret")
	req.Header.Set("Content-Type", "application/json")
	rec := httptest.NewRecorder()

	s.Handler().ServeHTTP(rec, req)

	if rec.Code != http.StatusAccepted {
		t.Fatalf("expected 202 Accepted, got %d: %s", rec.Code, rec.Body.String())
	}

	var res struct {
		Success bool             `json:"success"`
		Data    model.TaskRecord `json:"data"`
	}
	if err := json.Unmarshal(rec.Body.Bytes(), &res); err != nil {
		t.Fatalf("failed to parse response: %v", err)
	}

	taskID := res.Data.TaskID
	if taskID == "" {
		t.Fatalf("expected non-empty taskId")
	}

	// 验证查询状态接口
	reqStatus := httptest.NewRequest(http.MethodGet, "/api/scans/"+taskID+"/status", nil)
	reqStatus.Header.Set("X-LWT-Internal-Secret", "test-secret")
	recStatus := httptest.NewRecorder()
	s.Handler().ServeHTTP(recStatus, reqStatus)

	if recStatus.Code != http.StatusOK {
		t.Fatalf("expected 200 OK for status, got %d", recStatus.Code)
	}
}

func TestStaticUIFallback(t *testing.T) {
	tempUIDir := t.TempDir()
	_ = os.WriteFile(tempUIDir+"/index.html", []byte("<!DOCTYPE html><html><body>Test UI</body></html>"), 0644)

	s := NewServer(ServerOptions{
		UIDir:    tempUIDir,
		Detector: capabilities.NewDetector(),
		Manager:  fsm.NewTaskManager(),
	})

	req := httptest.NewRequest(http.MethodGet, "/index.html", nil)
	rec := httptest.NewRecorder()
	s.Handler().ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("expected 200 OK for index.html, got %d", rec.Code)
	}
	if !bytes.Contains(rec.Body.Bytes(), []byte("Test UI")) {
		t.Errorf("expected HTML body to contain Test UI")
	}
}

func TestServerProbeAndUnrankedRoutes(t *testing.T) {
	tempDir := t.TempDir()
	subDir := tempDir + "/sub"
	_ = os.Mkdir(subDir, 0755)
	_ = os.WriteFile(tempDir+"/tiny.log", []byte("hello"), 0644)
	_ = os.WriteFile(subDir+"/sub_file.bin", []byte("1234567890"), 0644)

	mgr := fsm.NewTaskManager()
	s := NewServer(ServerOptions{
		Secret:   "test-secret",
		Detector: capabilities.NewDetector(),
		Manager:  mgr,
	})

	task, _ := mgr.StartScan(context.Background(), model.ScanRequest{
		Target:                "container",
		Path:                  tempDir,
		MaxDepth:              1,
		PruneThresholdPercent: 1.0,
	})

	for {
		if task.Snapshot().Status == model.StatusCompleted {
			break
		}
	}

	// 1. 测试 /api/scans/{id}/unranked
	reqUnranked := httptest.NewRequest(http.MethodGet, "/api/scans/"+task.ID+"/unranked?path="+tempDir, nil)
	reqUnranked.Header.Set("X-LWT-Internal-Secret", "test-secret")
	recUnranked := httptest.NewRecorder()
	s.Handler().ServeHTTP(recUnranked, reqUnranked)

	if recUnranked.Code != http.StatusOK {
		t.Fatalf("expected 200 OK for unranked, got %d: %s", recUnranked.Code, recUnranked.Body.String())
	}

	// 2. 测试 /api/scans/{id}/probe
	probeBody, _ := json.Marshal(model.ProbeRequest{
		SubPath: subDir,
		Depth:   1,
	})
	reqProbe := httptest.NewRequest(http.MethodPost, "/api/scans/"+task.ID+"/probe", bytes.NewReader(probeBody))
	reqProbe.Header.Set("X-LWT-Internal-Secret", "test-secret")
	reqProbe.Header.Set("Content-Type", "application/json")
	recProbe := httptest.NewRecorder()
	s.Handler().ServeHTTP(recProbe, reqProbe)

	if recProbe.Code != http.StatusOK {
		t.Fatalf("expected 200 OK for probe, got %d: %s", recProbe.Code, recProbe.Body.String())
	}
}
