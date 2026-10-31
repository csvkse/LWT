package admin_test

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"linuxwebtool.local/mcp-bridge/internal/admin"
	"linuxwebtool.local/mcp-bridge/internal/runtime"
)

func TestHandlerControlFlow(t *testing.T) {
	tempDir := t.TempDir()
	runID := "run-admin-test-123"
	token := "secret-control-token-xyz"

	rt, err := runtime.NewRuntime(runtime.Config{
		StateDir: tempDir,
		RunID:    runID,
	})
	if err != nil {
		t.Fatalf("failed to create runtime: %v", err)
	}
	defer rt.Stop(context.Background())

	audit := admin.NewAuditLogger(tempDir)
	defer audit.Close()

	shutdownTriggered := false
	handler := admin.NewHandler(rt, token, runID, audit, func() {
		shutdownTriggered = true
	})

	mux := http.NewServeMux()
	handler.RegisterRoutes(mux)
	ts := httptest.NewServer(mux)
	defer ts.Close()

	client := ts.Client()

	// 1. Handshake without auth (handshake does not require token, verifies payload)
	handshakeBody, _ := json.Marshal(admin.HandshakeRequest{
		ProtocolVersion: 1,
		WorkerID:        "mcp-bridge",
		RunID:           runID,
	})
	resp, err := client.Post(ts.URL+"/control/handshake", "application/json", bytes.NewReader(handshakeBody))
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("handshake failed: %v, status: %d", err, resp.StatusCode)
	}
	var hsResp admin.HandshakeResponse
	_ = json.NewDecoder(resp.Body).Decode(&hsResp)
	if !hsResp.Success || hsResp.WorkerRunID != runID {
		t.Errorf("handshake response mismatch: %+v", hsResp)
	}

	// 2. Health without auth -> 401
	resp, err = client.Get(ts.URL + "/control/health")
	if err != nil || resp.StatusCode != http.StatusUnauthorized {
		t.Errorf("expected 401 for unauthenticated health check, got %d", resp.StatusCode)
	}

	// 3. Health with auth -> 200
	req, _ := http.NewRequest(http.MethodGet, ts.URL+"/control/health", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("health check failed: %v, status: %d", err, resp.StatusCode)
	}

	// 4. Invoke status.get
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/status.get", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("status.get failed: status: %d", resp.StatusCode)
	}
	var statusDTO admin.StatusDTO
	_ = json.NewDecoder(resp.Body).Decode(&statusDTO)
	if statusDTO.WorkerRunID != runID || !statusDTO.ProcessAlive {
		t.Errorf("statusDTO mismatch: %+v", statusDTO)
	}

	// 5. Invoke servers.save
	saveBody, _ := json.Marshal(admin.SaveServerRequestDTO{
		Name:      "Fixture HTTP",
		Transport: "http",
		URL:       "http://127.0.0.1:8080/mcp",
		Enabled:   true,
	})
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/servers.save?id=my-http", bytes.NewReader(saveBody))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		var errDTO admin.MutationResultDTO
		_ = json.NewDecoder(resp.Body).Decode(&errDTO)
		t.Fatalf("servers.save failed: status: %d, err: %s", resp.StatusCode, errDTO.Message)
	}

	// 6. Invoke servers.list
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/servers.list", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("servers.list failed: status: %d", resp.StatusCode)
	}
	var srvList admin.ServersResultDTO
	_ = json.NewDecoder(resp.Body).Decode(&srvList)
	if len(srvList.Servers) != 1 || srvList.Servers[0].ID != "my-http" {
		t.Errorf("unexpected server list: %+v", srvList)
	}

	// 7. Test Web UI served at root
	resp, err = client.Get(ts.URL + "/")
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("failed to get root Web UI: %v", err)
	}
	bodyBytes, _ := io.ReadAll(resp.Body)
	if !strings.Contains(string(bodyBytes), "MCP Bridge") {
		t.Errorf("expected root HTML to contain 'MCP Bridge', got: %s", string(bodyBytes))
	}

	// 8. Test Auth via query param ?token=
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/status.get?token="+token, nil)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("invoke with query token failed: %v, status: %d", err, resp.StatusCode)
	}

	// 9. Invoke shutdown
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/shutdown", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("shutdown request failed: status: %d", resp.StatusCode)
	}
	time.Sleep(100 * time.Millisecond)
	if !shutdownTriggered {
		t.Errorf("expected shutdown callback to be triggered")
	}
}
