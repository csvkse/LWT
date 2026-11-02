package config_test

import (
	"testing"

	"linuxwebtool.local/mcp-bridge/internal/config"
)

func TestValidateState(t *testing.T) {
	state := config.NewInitialState()

	// Initial valid state
	if err := config.Validate(state); err != nil {
		t.Fatalf("expected initial state to be valid, got: %v", err)
	}

	// Valid TunnelID
	state.Tunnel.TunnelID = "tunnel_0123456789abcdef0123456789abcdef"
	if err := config.Validate(state); err != nil {
		t.Errorf("expected valid tunnel ID, got: %v", err)
	}

	// Invalid TunnelID format
	state.Tunnel.TunnelID = "invalid_tunnel_id"
	if err := config.Validate(state); err == nil {
		t.Error("expected error for invalid tunnel ID format")
	}
	state.Tunnel.TunnelID = ""

	// Valid stdio server
	state.Servers["local-tool"] = &config.BackendSpec{
		ID:        "local-tool",
		Name:      "Local Tool",
		Transport: config.TransportStdio,
		Command:   "python",
		Args:      []string{"tool.py"},
	}
	if err := config.Validate(state); err != nil {
		t.Errorf("expected valid stdio server, got: %v", err)
	}

	// Shell metacharacter in command
	state.Servers["local-tool"].Command = "python; rm -rf /"
	if err := config.Validate(state); err == nil {
		t.Error("expected error for shell metacharacter in command")
	}
	state.Servers["local-tool"].Command = "python"

	// Valid http server
	state.Servers["remote-mcp"] = &config.BackendSpec{
		ID:        "remote-mcp",
		Name:      "Remote MCP",
		Transport: config.TransportStreamableHTTP,
		URL:       "http://127.0.0.1:8080/mcp",
	}
	if err := config.Validate(state); err != nil {
		t.Errorf("expected valid HTTP server, got: %v", err)
	}

	// Forbidden cloud metadata IP
	state.Servers["remote-mcp"].URL = "http://169.254.169.254/latest/meta-data"
	if err := config.Validate(state); err == nil {
		t.Error("expected error for cloud metadata IP")
	}

	// Forbidden userinfo
	state.Servers["remote-mcp"].URL = "http://admin:secret@127.0.0.1:8080/mcp"
	if err := config.Validate(state); err == nil {
		t.Error("expected error for userinfo in URL")
	}

	// Forbidden fragment
	state.Servers["remote-mcp"].URL = "http://127.0.0.1:8080/mcp#anchor"
	if err := config.Validate(state); err == nil {
		t.Error("expected error for fragment in URL")
	}
	state.Servers["remote-mcp"].URL = "http://127.0.0.1:8080/mcp"

	// Selection for nonexistent server
	state.Selections["ghost-server"] = &config.ServerSelection{
		ServerID:      "ghost-server",
		SelectedTools: []string{"test"},
	}
	if err := config.Validate(state); err == nil {
		t.Error("expected error for selection on non-existent server")
	}
	delete(state.Selections, "ghost-server")

	// Valid empty selection (means deny all tools)
	state.Selections["local-tool"] = &config.ServerSelection{
		ServerID:      "local-tool",
		SelectedTools: []string{},
	}
	if err := config.Validate(state); err != nil {
		t.Errorf("expected valid empty selection, got: %v", err)
	}
}

func TestValidateSecretMutation(t *testing.T) {
	if err := config.ValidateSecretMutation(config.SecretMutation{Action: config.SecretActionKeep}); err != nil {
		t.Errorf("expected valid keep mutation: %v", err)
	}

	if err := config.ValidateSecretMutation(config.SecretMutation{Action: config.SecretActionKeep, Value: "forbidden"}); err == nil {
		t.Error("expected error when keep has a value")
	}

	if err := config.ValidateSecretMutation(config.SecretMutation{Action: config.SecretActionReplace, Value: "new-secret"}); err != nil {
		t.Errorf("expected valid replace mutation: %v", err)
	}

	if err := config.ValidateSecretMutation(config.SecretMutation{Action: config.SecretActionReplace, Value: "   "}); err == nil {
		t.Error("expected error when replace has empty value")
	}

	if err := config.ValidateSecretMutation(config.SecretMutation{Action: config.SecretActionUpdate, Value: "new-secret"}); err != nil {
		t.Errorf("expected valid update mutation: %v", err)
	}

	if err := config.ValidateSecretMutation(config.SecretMutation{Action: config.SecretActionUpdate, Value: "   "}); err == nil {
		t.Error("expected error when update has empty value")
	}

	if err := config.ValidateSecretMutation(config.SecretMutation{Action: config.SecretActionClear}); err != nil {
		t.Errorf("expected valid clear mutation: %v", err)
	}
}
