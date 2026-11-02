package config

import (
	"testing"
)

func TestSlugifyServerID(t *testing.T) {
	tests := []struct {
		input    string
		expected string
	}{
		{"filesystem", "filesystem"},
		{"My_Server_1", "my-server-1"},
		{"@modelcontextprotocol/server-github", "modelcontextprotocol-server-github"},
		{"  Hello   World!  ", "hello-world"},
		{"---abc---def---", "abc-def"},
		{"", "mcp-server"},
		{"!@#$%^", "mcp-server"},
	}

	for _, tc := range tests {
		got := SlugifyServerID(tc.input)
		if got != tc.expected {
			t.Errorf("SlugifyServerID(%q) = %q, expected %q", tc.input, got, tc.expected)
		}
	}
}

func TestDetectTransport(t *testing.T) {
	if got := DetectTransport("npx", "", ""); got != TransportStdio {
		t.Errorf("expected stdio, got %s", got)
	}
	if got := DetectTransport("", "http://127.0.0.1:8080/sse", ""); got != TransportSSE {
		t.Errorf("expected sse, got %s", got)
	}
	if got := DetectTransport("", "http://127.0.0.1:8080/mcp", ""); got != TransportStreamableHTTP {
		t.Errorf("expected streamable-http, got %s", got)
	}
	if got := DetectTransport("npx", "http://example.com", "sse"); got != TransportSSE {
		t.Errorf("expected explicit sse, got %s", got)
	}
}

func TestParseStandardMCPConfig_ClaudeDesktop(t *testing.T) {
	rawJSON := `{
		"mcpServers": {
			"filesystem": {
				"command": "npx",
				"args": ["-y", "@modelcontextprotocol/server-filesystem", "/allowed/path"],
				"env": {
					"DEBUG": "true"
				}
			},
			"github": {
				"command": "docker",
				"args": ["run", "-i", "--rm", "mcp/github"],
				"env": {
					"GITHUB_PERSONAL_ACCESS_TOKEN": "ghp_123456"
				}
			},
			"remote-sse": {
				"url": "http://127.0.0.1:8000/sse",
				"headers": {
					"Authorization": "Bearer test-token"
				}
			}
		}
	}`

	specs, err := ParseStandardMCPConfig([]byte(rawJSON))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(specs) != 3 {
		t.Fatalf("expected 3 specs, got %d", len(specs))
	}

	specMap := make(map[string]*BackendSpec)
	for _, s := range specs {
		specMap[s.ID] = s
	}

	fs := specMap["filesystem"]
	if fs == nil || fs.Transport != TransportStdio || fs.Command != "npx" || fs.Env["DEBUG"] != "true" {
		t.Errorf("invalid filesystem spec: %+v", fs)
	}

	gh := specMap["github"]
	if gh == nil || gh.Transport != TransportStdio || gh.Env["GITHUB_PERSONAL_ACCESS_TOKEN"] != "ghp_123456" {
		t.Errorf("invalid github spec: %+v", gh)
	}

	sse := specMap["remote-sse"]
	if sse == nil || sse.Transport != TransportSSE || sse.URL != "http://127.0.0.1:8000/sse" || sse.Headers["Authorization"] != "Bearer test-token" {
		t.Errorf("invalid remote-sse spec: %+v", sse)
	}
}

func TestParseStandardMCPConfig_Zed(t *testing.T) {
	rawJSON := `{
		"context_servers": {
			"@modelcontextprotocol/server-everything": {
				"command": "npx",
				"args": ["-y", "@modelcontextprotocol/server-everything"]
			}
		}
	}`

	specs, err := ParseStandardMCPConfig([]byte(rawJSON))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(specs) != 1 {
		t.Fatalf("expected 1 spec, got %d", len(specs))
	}

	spec := specs[0]
	if spec.ID != "modelcontextprotocol-server-everything" {
		t.Errorf("expected slugified ID, got %s", spec.ID)
	}
	if spec.Name != "@modelcontextprotocol/server-everything" {
		t.Errorf("expected original name preserved, got %s", spec.Name)
	}
	if spec.Transport != TransportStdio || spec.Command != "npx" {
		t.Errorf("invalid spec: %+v", spec)
	}
}

func TestParseStandardMCPConfig_SingleServerSnippet(t *testing.T) {
	rawJSON := `{
		"name": "sqlite-db",
		"command": "uvx",
		"args": ["mcp-server-sqlite", "--db-path", "/app/data/test.db"]
	}`

	specs, err := ParseStandardMCPConfig([]byte(rawJSON))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(specs) != 1 {
		t.Fatalf("expected 1 spec, got %d", len(specs))
	}
	if specs[0].ID != "sqlite-db" || specs[0].Command != "uvx" {
		t.Errorf("invalid spec: %+v", specs[0])
	}
}

func TestParseStandardMCPConfig_ArrayFormat(t *testing.T) {
	rawJSON := `[
		{
			"id": "srv1",
			"command": "node",
			"args": ["dist/index.js"]
		},
		{
			"id": "srv2",
			"url": "http://10.0.0.1:9090/sse"
		}
	]`

	specs, err := ParseStandardMCPConfig([]byte(rawJSON))
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if len(specs) != 2 {
		t.Fatalf("expected 2 specs, got %d", len(specs))
	}
	if specs[0].ID != "srv1" || specs[1].Transport != TransportSSE {
		t.Errorf("unexpected specs: %+v", specs)
	}
}

func TestParseStandardMCPConfig_UTF8BOM(t *testing.T) {
	rawJSON := []byte("\xef\xbb\xbf{\n\t\"mcpServers\": {\n\t\t\"my-test\": {\n\t\t\t\"command\": \"python\",\n\t\t\t\"args\": [\"main.py\"]\n\t\t}\n\t}\n}")
	specs, err := ParseStandardMCPConfig(rawJSON)
	if err != nil {
		t.Fatalf("unexpected error with BOM: %v", err)
	}
	if len(specs) != 1 || specs[0].ID != "my-test" {
		t.Errorf("unexpected spec: %+v", specs)
	}
}
