package backend_test

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"os/exec"
	"path/filepath"
	"runtime"
	"testing"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/backend"
	"linuxwebtool.local/mcp-bridge/internal/config"
	"linuxwebtool.local/mcp-bridge/internal/testkit"
)

func TestStreamableHTTPBackend(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	fix := testkit.NewMCPFixture()
	handler := mcp.NewStreamableHTTPHandler(func(r *http.Request) *mcp.Server {
		if r.Header.Get("Authorization") != "Bearer test-auth-secret" {
			return nil
		}
		return fix.Server()
	}, nil)

	server := httptest.NewServer(handler)
	defer server.Close()

	spec := &config.BackendSpec{
		ID:             "http-test",
		Name:           "HTTP Test",
		Transport:      config.TransportStreamableHTTP,
		URL:            server.URL,
		TimeoutSeconds: 5,
	}

	sess, err := backend.ConnectBackend(ctx, spec, "test-auth-secret")
	if err != nil {
		t.Fatalf("failed to connect http backend: %v", err)
	}
	defer sess.Close(ctx)

	// ListTools
	list, err := sess.ListTools(ctx, "")
	if err != nil {
		t.Fatalf("failed to list tools: %v", err)
	}
	if len(list.Tools) == 0 {
		t.Errorf("expected tools from http server, got 0")
	}

	// CallTool echo
	args, _ := json.Marshal(map[string]any{"message": "http-world"})
	res, err := sess.CallTool(ctx, "echo", args)
	if err != nil {
		t.Fatalf("failed to call tool: %v", err)
	}
	if len(res.Content) == 0 {
		t.Fatalf("expected tool content, got none")
	}
	textContent, ok := res.Content[0].(*mcp.TextContent)
	if !ok || textContent.Text != "echo: http-world" {
		t.Errorf("unexpected content: %+v", res.Content[0])
	}
}

func TestSSEBackend(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	fix := testkit.NewMCPFixture()
	handler := mcp.NewSSEHandler(func(r *http.Request) *mcp.Server {
		return fix.Server()
	}, nil)

	server := httptest.NewServer(handler)
	defer server.Close()

	spec := &config.BackendSpec{
		ID:             "sse-test",
		Name:           "SSE Test",
		Transport:      config.TransportSSE,
		URL:            server.URL,
		TimeoutSeconds: 5,
	}

	sess, err := backend.ConnectBackend(ctx, spec, "")
	if err != nil {
		t.Fatalf("failed to connect sse backend: %v", err)
	}
	defer sess.Close(ctx)

	list, err := sess.ListTools(ctx, "")
	if err != nil {
		t.Fatalf("failed to list tools: %v", err)
	}
	if len(list.Tools) == 0 {
		t.Errorf("expected tools from sse server, got 0")
	}

	args, _ := json.Marshal(map[string]any{"a": 10, "b": 25})
	res, err := sess.CallTool(ctx, "add", args)
	if err != nil {
		t.Fatalf("failed to call tool: %v", err)
	}
	if len(res.Content) == 0 {
		t.Fatalf("expected tool content, got none")
	}
	textContent, ok := res.Content[0].(*mcp.TextContent)
	if !ok || textContent.Text != "35" {
		t.Errorf("unexpected content: %+v", res.Content[0])
	}
}

func TestStdioBackend(t *testing.T) {
	// Compile stdio fixture to temporary directory
	binName := "stdio-fixture"
	if runtime.GOOS == "windows" {
		binName += ".exe"
	}
	tempDir := t.TempDir()
	binPath := filepath.Join(tempDir, binName)
	buildCmd := exec.Command("go", "build", "-o", binPath, "linuxwebtool.local/mcp-bridge/internal/testkit/stdiofixture")
	if out, err := buildCmd.CombinedOutput(); err != nil {
		t.Fatalf("failed to compile stdiofixture: %v\nOutput: %s", err, string(out))
	}

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	spec := &config.BackendSpec{
		ID:        "stdio-test",
		Name:      "Stdio Test",
		Transport: config.TransportStdio,
		Command:   binPath,
	}

	sess, err := backend.ConnectBackend(ctx, spec, "")
	if err != nil {
		t.Fatalf("failed to connect stdio backend: %v", err)
	}
	defer sess.Close(ctx)

	list, err := sess.ListTools(ctx, "")
	if err != nil {
		t.Fatalf("failed to list tools over stdio: %v", err)
	}
	if len(list.Tools) == 0 {
		t.Errorf("expected tools from stdio fixture, got 0")
	}

	args, _ := json.Marshal(map[string]any{"message": "stdio-rocket"})
	res, err := sess.CallTool(ctx, "stdio-echo", args)
	if err != nil {
		t.Fatalf("failed to call tool over stdio: %v", err)
	}
	if len(res.Content) == 0 {
		t.Fatalf("expected tool content, got none")
	}
	textContent, ok := res.Content[0].(*mcp.TextContent)
	if !ok || textContent.Text != "stdio: stdio-rocket" {
		t.Errorf("unexpected content: %+v", res.Content[0])
	}
}

func TestBackendManagerProbeAndDiscover(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	fix := testkit.NewMCPFixture()
	handler := mcp.NewStreamableHTTPHandler(func(r *http.Request) *mcp.Server {
		return fix.Server()
	}, nil)

	server := httptest.NewServer(handler)
	defer server.Close()

	spec := &config.BackendSpec{
		ID:        "mgr-http",
		Name:      "Manager HTTP",
		Transport: config.TransportStreamableHTTP,
		URL:       server.URL,
	}

	mgr := backend.NewManager(nil)
	defer mgr.Close()

	// 1. Probe
	probeRes, err := mgr.Probe(ctx, spec)
	if err != nil {
		t.Fatalf("probe failed: %v", err)
	}
	if !probeRes.Connected || probeRes.ToolCount == 0 {
		t.Errorf("probe did not report connected with tools: %+v", probeRes)
	}

	// 2. Discover
	tools, err := mgr.Discover(ctx, spec)
	if err != nil {
		t.Fatalf("discover failed: %v", err)
	}
	if len(tools) == 0 {
		t.Errorf("discover returned 0 tools")
	}

	// 3. Acquire Lease
	lease, err := mgr.Acquire(ctx, spec, backend.CallScope{SessionID: "sess-1", RequestID: "req-1"})
	if err != nil {
		t.Fatalf("acquire failed: %v", err)
	}
	args, _ := json.Marshal(map[string]any{"message": "leased-call"})
	res, err := lease.Session().CallTool(ctx, "echo", args)
	if err != nil {
		t.Fatalf("call on leased session failed: %v", err)
	}
	if len(res.Content) == 0 {
		t.Errorf("expected content from leased call")
	}
	lease.Release()
}
