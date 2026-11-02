package admin_test

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
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

	var shutdownTriggered atomic.Bool
	handler := admin.NewHandler(rt, token, runID, audit, func() {
		shutdownTriggered.Store(true)
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
	if statusDTO.ActiveToolCount < 0 {
		t.Errorf("expected non-negative ActiveToolCount, got %d", statusDTO.ActiveToolCount)
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

	// 6.1 Invoke tunnel.save with secretAction: "update" and presharedKey alias (Postel's Law compliance)
	tunnelBody, _ := json.Marshal(map[string]any{
		"tunnelId":         "tunnel_0123456789abcdef0123456789abcdef",
		"expectedRevision": 0,
		"secretAction":     "update",
		"presharedKey":     "sk-proj-test-secret-key-1234567890",
	})
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/tunnel.save", bytes.NewReader(tunnelBody))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		var errDTO admin.MutationResultDTO
		_ = json.NewDecoder(resp.Body).Decode(&errDTO)
		t.Fatalf("tunnel.save with update action failed: status: %d, err: %s", resp.StatusCode, errDTO.Message)
	}
	var tunnelRes admin.MutationResultDTO
	_ = json.NewDecoder(resp.Body).Decode(&tunnelRes)
	if !tunnelRes.Success {
		t.Errorf("expected successful tunnel.save mutation, got: %+v", tunnelRes)
	}

	// 6.2 Test resilience against UTF-8 BOM in JSON payload (RFC 8259 Section 8.1 / Postel's Law)
	bomBody := append([]byte{0xef, 0xbb, 0xbf}, tunnelBody...)
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/tunnel.save", bytes.NewReader(bomBody))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		var errDTO admin.MutationResultDTO
		_ = json.NewDecoder(resp.Body).Decode(&errDTO)
		t.Fatalf("tunnel.save with UTF-8 BOM failed: status: %d, err: %s", resp.StatusCode, errDTO.Message)
	}

	// 6.3 Test servers.import with Claude Desktop format
	importBody := []byte(`{
		"mcpServers": {
			"imported-fs": {
				"command": "npx",
				"args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
				"env": {
					"NODE_ENV": "production"
				}
			},
			"imported-remote": {
				"url": "http://127.0.0.1:9999/sse"
			}
		}
	}`)
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/servers.import", bytes.NewReader(importBody))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		var errDTO admin.ImportServersResultDTO
		_ = json.NewDecoder(resp.Body).Decode(&errDTO)
		t.Fatalf("servers.import failed: status: %d, err: %s", resp.StatusCode, errDTO.Message)
	}
	var importRes admin.ImportServersResultDTO
	_ = json.NewDecoder(resp.Body).Decode(&importRes)
	if !importRes.Success || len(importRes.ImportedIDs) != 2 {
		t.Errorf("expected 2 imported servers, got: %+v", importRes)
	}

	// 7. Test Web UI served at root and token injection
	resp, err = client.Get(ts.URL + "/")
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("failed to get root Web UI: %v", err)
	}
	bodyBytes, _ := io.ReadAll(resp.Body)
	if !strings.Contains(string(bodyBytes), "MCP Bridge") {
		t.Errorf("expected root HTML to contain 'MCP Bridge', got: %s", string(bodyBytes))
	}
	if !strings.Contains(string(bodyBytes), "window.__MCP_BRIDGE_TOKEN__ = \""+token+"\";") {
		t.Errorf("expected injected token in HTML, got: %s", string(bodyBytes))
	}

	// 7.1 Test Ingress Path Normalization (/Assets/index.html/control/invoke/status.get)
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/Assets/index.html/control/invoke/status.get", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("expected 200 for ingress normalized invoke, got %d", resp.StatusCode)
	}
	contentType := resp.Header.Get("Content-Type")
	if !strings.Contains(contentType, "application/json") {
		t.Errorf("expected application/json for status.get, got %s", contentType)
	}

	// 7.2 Test Host Gateway Trusted Header (X-LWT-Plugin-Id: mcp-bridge)
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/status.get", nil)
	req.Header.Set("X-LWT-Plugin-Id", "mcp-bridge")
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("expected 200 for gateway-forwarded invoke without bearer token, got %d", resp.StatusCode)
	}

	// 7.3 Test SPA Fallback Safety Gate: Non-GET to unmatched path returns 404 JSON, NOT 200 HTML
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/non-existent-api", nil)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusNotFound {
		t.Fatalf("expected 404 for unmatched POST, got %d", resp.StatusCode)
	}
	if !strings.Contains(resp.Header.Get("Content-Type"), "application/json") {
		t.Errorf("expected json 404 error, got %s", resp.Header.Get("Content-Type"))
	}

	// 8. Test Auth via query param ?token=
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/status.get?token="+token, nil)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("invoke with query token failed: %v, status: %d", err, resp.StatusCode)
	}
	var stDTO admin.StatusDTO
	_ = json.NewDecoder(resp.Body).Decode(&stDTO)
	if stDTO.TunnelID != "tunnel_0123456789abcdef0123456789abcdef" {
		t.Errorf("expected tunnelId to be persisted and returned in status.get, got: %q", stDTO.TunnelID)
	}
	if !stDTO.HasTunnelKey {
		t.Errorf("expected hasTunnelKey to be true in status.get")
	}

	// 8.1 Test servers.toggle action
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/servers.toggle?id=imported-fs", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("servers.toggle failed: %v, status: %d", err, resp.StatusCode)
	}
	var toggleRes map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&toggleRes)
	if toggleRes["enabled"] != false {
		t.Errorf("expected server to be toggled to false, got: %+v", toggleRes)
	}

	// 8.2 Test config.apply with graceful degradation for unready/failing backends
	applyBody := []byte(`{"expectedRevision":0}`)
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/config.apply", bytes.NewReader(applyBody))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("config.apply with degraded server failed: %v, status: %d", err, resp.StatusCode)
	}
	var applyRes admin.ApplyResultDTO
	_ = json.NewDecoder(resp.Body).Decode(&applyRes)
	if !applyRes.Success {
		t.Errorf("expected config.apply to succeed with graceful degradation, got: %+v", applyRes)
	}

	// 8.3 Test tools.call action
	callBody := []byte(`{"name":"unknown-tool","arguments":{}}`)
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/tools.call", bytes.NewReader(callBody))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("tools.call request failed: %v, status: %d", err, resp.StatusCode)
	}
	var callRes admin.CallToolResultDTO
	_ = json.NewDecoder(resp.Body).Decode(&callRes)
	if !callRes.IsError {
		t.Errorf("expected isError=true for unknown tool, got: %+v", callRes)
	}

	// 8.4 Test tunnel.save with applyNow=true and status.get persistence
	proxyURL := "http://127.0.0.1:7890"
	validTunnelID := "tunnel_12345678901234567890123456789012"
	autoConn := true
	tunnelSaveBody, _ := json.Marshal(admin.SaveTunnelRequestDTO{
		TunnelID:     validTunnelID,
		ProxyURL:     &proxyURL,
		AutoConnect:  &autoConn,
		SecretAction: "keep",
		ApplyNow:     true,
	})
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/tunnel.save", bytes.NewReader(tunnelSaveBody))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("tunnel.save with applyNow failed: %v, status: %d", err, resp.StatusCode)
	}

	// Verify status.get reflects the persisted proxyUrl and tunnelId
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/status.get", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("status.get failed: %v, status: %d", err, resp.StatusCode)
	}
	var afterTunnelStatus admin.StatusDTO
	_ = json.NewDecoder(resp.Body).Decode(&afterTunnelStatus)
	if afterTunnelStatus.TunnelID != validTunnelID {
		t.Errorf("expected TunnelID %q, got %q", validTunnelID, afterTunnelStatus.TunnelID)
	}
	if afterTunnelStatus.ProxyURL != proxyURL {
		t.Errorf("expected ProxyURL %q, got %q", proxyURL, afterTunnelStatus.ProxyURL)
	}
	if afterTunnelStatus.ActiveRevision == 0 {
		t.Errorf("expected ActiveRevision > 0 after applyNow, got %d", afterTunnelStatus.ActiveRevision)
	}

	// 8.5 Test servers.save with applyNow=true (Immediate Persistence)
	newServerID := "persisted-custom-mcp"
	serverSaveBody, _ := json.Marshal(admin.SaveServerRequestDTO{
		Name:         "Persisted Custom MCP",
		Transport:    "stdio",
		Command:      "echo",
		Arguments:    []string{"hello"},
		SecretAction: "clear",
		Enabled:      true,
		ApplyNow:     true,
	})
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/servers.save?id="+newServerID, bytes.NewReader(serverSaveBody))
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("servers.save with applyNow failed: %v, status: %d", err, resp.StatusCode)
	}

	// Read state to verify server is directly persisted in Committed Active State
	persistedState, readErr := rt.Store().Read(context.Background())
	if readErr != nil || persistedState.Servers[newServerID] == nil {
		t.Fatalf("expected server %q to be committed and persisted, err: %v", newServerID, readErr)
	}
	if persistedState.ActiveRevision < persistedState.DraftRevision {
		t.Errorf("expected ActiveRevision to match DraftRevision after applyNow, got active=%d, draft=%d",
			persistedState.ActiveRevision, persistedState.DraftRevision)
	}

	// 8.6 Test tunnel.logs query and clear
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/tunnel.logs?limit=80", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("tunnel.logs query failed: %v, status: %d", err, resp.StatusCode)
	}
	var logsRes map[string]any
	_ = json.NewDecoder(resp.Body).Decode(&logsRes)
	if logsRes["success"] != true {
		t.Errorf("expected tunnel.logs success=true, got %+v", logsRes)
	}

	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/tunnel.logs?clear=1", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("tunnel.logs clear failed: %v, status: %d", err, resp.StatusCode)
	}

	// 8.7 Test servers.delete with applyNow=1
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/invoke/servers.delete?id="+newServerID+"&applyNow=1", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("servers.delete with applyNow failed: %v, status: %d", err, resp.StatusCode)
	}
	deletedState, _ := rt.Store().Read(context.Background())
	if deletedState.Servers[newServerID] != nil {
		t.Errorf("expected server %q to be deleted after applyNow", newServerID)
	}

	// 9. Invoke shutdown
	req, _ = http.NewRequest(http.MethodPost, ts.URL+"/control/shutdown", nil)
	req.Header.Set("Authorization", "Bearer "+token)
	resp, err = client.Do(req)
	if err != nil || resp.StatusCode != http.StatusOK {
		t.Fatalf("shutdown request failed: status: %d", resp.StatusCode)
	}
	time.Sleep(150 * time.Millisecond)
	if !shutdownTriggered.Load() {
		t.Errorf("expected shutdown callback to be triggered")
	}
}
