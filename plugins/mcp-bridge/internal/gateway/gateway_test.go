package gateway_test

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/backend"
	"linuxwebtool.local/mcp-bridge/internal/catalog"
	"linuxwebtool.local/mcp-bridge/internal/config"
	"linuxwebtool.local/mcp-bridge/internal/gateway"
	"linuxwebtool.local/mcp-bridge/internal/state"
	"linuxwebtool.local/mcp-bridge/internal/testkit"
)

func TestGatewayLifecycleAndExecution(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	tempDir := t.TempDir()

	// 1. Setup backend fixture
	fix := testkit.NewMCPFixture()
	handler := mcp.NewStreamableHTTPHandler(func(r *http.Request) *mcp.Server {
		return fix.Server()
	}, nil)
	backendServer := httptest.NewServer(handler)
	defer backendServer.Close()

	// 2. Setup state & dependencies
	vault, err := state.OpenOrCreateSecretsVault(tempDir)
	if err != nil {
		t.Fatalf("failed to create vault: %v", err)
	}
	store, err := state.NewStore(tempDir, vault)
	if err != nil {
		t.Fatalf("failed to create store: %v", err)
	}

	reg := catalog.NewRegistry()
	mgr := backend.NewManager(vault)
	defer mgr.Close()
	adm := gateway.NewAdmissionController()
	facade := gateway.NewFacadeServer(nil)

	gw := gateway.NewGateway(reg, mgr, store, adm, facade)

	// 3. Register server and draft selection in store
	_, err = store.SaveDraft(ctx, 1, func(s *config.State) error {
		s.Servers["test-backend"] = &config.BackendSpec{
			ID:        "test-backend",
			Name:      "Test Backend",
			Transport: config.TransportStreamableHTTP,
			URL:       backendServer.URL,
			Enabled:   true,
			Revision:  1,
		}
		s.Selections["test-backend"] = &config.ServerSelection{
			ServerID:      "test-backend",
			SelectedTools: []string{"echo"}, // Only "echo" selected
		}
		return nil
	})
	if err != nil {
		t.Fatalf("failed to save draft: %v", err)
	}

	// 4. Initial List before Apply should be empty
	initialList, err := gw.List(ctx, "")
	if err != nil {
		t.Fatalf("failed to list tools: %v", err)
	}
	if len(initialList.Tools) != 0 {
		t.Errorf("expected 0 tools before apply, got %d", len(initialList.Tools))
	}

	// 5. Call unapproved tool should fail
	unapprovedRes, err := gw.Call(ctx, "test-backend__add", nil, backend.CallScope{RequestID: "req-unapproved"})
	if err != nil {
		t.Fatalf("unexpected call error: %v", err)
	}
	if !unapprovedRes.IsError {
		t.Errorf("expected IsError=true for unapproved tool")
	}

	// 6. Apply configuration (expected desired revision 2)
	applyRes, err := gw.Apply(ctx, 2)
	if err != nil {
		t.Fatalf("failed to apply configuration: %v", err)
	}
	if applyRes.ActiveRevision != 2 || applyRes.ExposedToolCount != 1 {
		t.Errorf("unexpected apply result: %+v", applyRes)
	}

	// 7. Verify List now shows "test-backend__echo"
	activeList, err := gw.List(ctx, "")
	if err != nil || len(activeList.Tools) != 1 {
		t.Fatalf("expected 1 exposed tool, got %d (err: %v)", len(activeList.Tools), err)
	}
	if activeList.Tools[0].Name != "test-backend__echo" {
		t.Errorf("expected test-backend__echo, got %s", activeList.Tools[0].Name)
	}

	// 8. Execute approved tool
	echoArgs, _ := json.Marshal(map[string]any{"message": "gateway-verified"})
	echoRes, err := gw.Call(ctx, "test-backend__echo", echoArgs, backend.CallScope{RequestID: "req-echo"})
	if err != nil {
		t.Fatalf("call failed: %v", err)
	}
	if echoRes.IsError {
		t.Errorf("expected successful call, got IsError=true")
	}
	if len(echoRes.Content) == 0 {
		t.Fatalf("expected content, got none")
	}
	textVal, ok := echoRes.Content[0].(*mcp.TextContent)
	if !ok || textVal.Text != "echo: gateway-verified" {
		t.Errorf("unexpected tool content: %+v", echoRes.Content[0])
	}

	// 9. Now revoke selection and re-apply
	_, err = store.SaveDraft(ctx, 2, func(s *config.State) error {
		s.Selections["test-backend"].SelectedTools = []string{} // Deselect all!
		return nil
	})
	if err != nil {
		t.Fatalf("failed to update draft: %v", err)
	}

	_, err = gw.Apply(ctx, 3)
	if err != nil {
		t.Fatalf("failed to apply revoked state: %v", err)
	}

	// 10. Call should now be rejected immediately
	revokedRes, err := gw.Call(ctx, "test-backend__echo", echoArgs, backend.CallScope{RequestID: "req-revoked"})
	if err != nil {
		t.Fatalf("call error: %v", err)
	}
	if !revokedRes.IsError {
		t.Errorf("expected call to revoked tool to return IsError=true")
	}
}
