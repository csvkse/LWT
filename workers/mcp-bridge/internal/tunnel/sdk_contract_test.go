package tunnel_test

import (
	"context"
	"encoding/json"
	"testing"
	"time"

	tunnelclient "github.com/openai/tunnel-client"
	"linuxwebtool.local/mcp-bridge/internal/testkit"
)

func TestSDKContract(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	tunnelID := "tunnel_0123456789abcdef0123456789abcdef"
	apiKey := "sk-mock-tunnel-key-12345"

	// 1. Setup mock control plane and in-memory MCP server
	cp := testkit.NewControlPlaneFixture(tunnelID, apiKey)
	defer cp.Close()

	mcpFix := testkit.NewMCPFixture()
	clientTransport, err := mcpFix.CreateInMemoryPair(ctx)
	if err != nil {
		t.Fatalf("failed to create in-memory transport: %v", err)
	}

	cfg := tunnelclient.Config{
		TunnelID:            tunnelID,
		APIKey:              apiKey,
		ControlPlaneBaseURL: cp.URL(),
		PollTimeout:         200 * time.Millisecond,
	}

	client, err := tunnelclient.New(cfg, clientTransport)
	if err != nil {
		t.Fatalf("failed to construct tunnel client: %v", err)
	}

	if err := client.Start(ctx); err != nil {
		t.Fatalf("failed to start tunnel client: %v", err)
	}
	defer func() {
		stopCtx, stopCancel := context.WithTimeout(context.Background(), 3*time.Second)
		defer stopCancel()
		_ = client.Stop(stopCtx)
	}()

	// 2. Wait until client is ready (first successful poll)
	readyCtx, readyCancel := context.WithTimeout(ctx, 3*time.Second)
	defer readyCancel()
	if err := client.WaitUntilReady(readyCtx); err != nil {
		t.Fatalf("client did not become ready: %v", err)
	}

	// 3. Perform MCP handshake over the tunnel
	// 3a. Initialize
	initReq := map[string]any{
		"jsonrpc": "2.0",
		"id":      1,
		"method":  "initialize",
		"params": map[string]any{
			"protocolVersion": "2024-11-05",
			"capabilities":    map[string]any{},
			"clientInfo": map[string]any{
				"name":    "chatgpt-test",
				"version": "1.0.0",
			},
		},
	}
	if err := cp.QueueCommand("req-init", initReq); err != nil {
		t.Fatalf("failed to queue init command: %v", err)
	}

	initResp := cp.WaitForResponse(5 * time.Second)
	if initResp == nil {
		t.Fatalf("timed out waiting for initialize response")
	}
	t.Logf("Initialize Response: %s", string(initResp.JSONResponse))

	// 3b. Queue tools/call command on the control plane
	callReq := map[string]any{
		"jsonrpc": "2.0",
		"id":      2,
		"method":  "tools/call",
		"params": map[string]any{
			"name": "echo",
			"arguments": map[string]any{
				"message": "hello-contract",
			},
		},
	}
	if err := cp.QueueCommand("req-call", callReq); err != nil {
		t.Fatalf("failed to queue call command: %v", err)
	}

	// 4. Verify the response arrives back at the control plane
	resp := cp.WaitForResponse(5 * time.Second)
	if resp == nil {
		t.Fatalf("timed out waiting for response from tunnel-client")
	}

	if resp.RequestID != "req-call" {
		t.Errorf("expected request_id req-call, got %s", resp.RequestID)
	}

	t.Logf("Raw JSON Response: %s", string(resp.JSONResponse))
	var parsed struct {
		Result struct {
			Content []struct {
				Text string `json:"text"`
			} `json:"content"`
			IsError bool `json:"isError"`
		} `json:"result"`
	}
	if err := json.Unmarshal(resp.JSONResponse, &parsed); err != nil {
		t.Fatalf("failed to parse json response: %v\nRaw: %s", err, string(resp.JSONResponse))
	}

	if len(parsed.Result.Content) == 0 || parsed.Result.Content[0].Text != "echo: hello-contract" {
		t.Errorf("unexpected content: %+v", parsed.Result.Content)
	}

	if mcpFix.EchoCount.Load() != 1 {
		t.Errorf("expected echo count 1, got %d", mcpFix.EchoCount.Load())
	}
}
