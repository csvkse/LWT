package config

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"regexp"
	"strings"
	"time"
)

var (
	nonAlphanumericRegex = regexp.MustCompile(`[^a-z0-9]+`)
	multipleDashRegex    = regexp.MustCompile(`-+`)
)

// RawServerEntry represents standard MCP server definition in external configurations
// (such as Claude Desktop, Cursor, Zed, Cline, or Smithery).
type RawServerEntry struct {
	ID        string            `json:"id,omitempty"`
	Name      string            `json:"name,omitempty"`
	Transport string            `json:"transport,omitempty"`
	Command   string            `json:"command,omitempty"`
	Args      []string          `json:"args,omitempty"`
	Cwd       string            `json:"cwd,omitempty"`
	Env       map[string]string `json:"env,omitempty"`
	URL       string            `json:"url,omitempty"`
	Headers   map[string]string `json:"headers,omitempty"`
	Disabled  bool              `json:"disabled,omitempty"`
}

// SlugifyServerID converts arbitrary external identifiers into a valid server ID
// satisfying ^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$.
func SlugifyServerID(raw string) string {
	lowered := strings.ToLower(strings.TrimSpace(raw))
	cleaned := nonAlphanumericRegex.ReplaceAllString(lowered, "-")
	collapsed := multipleDashRegex.ReplaceAllString(cleaned, "-")
	trimmed := strings.Trim(collapsed, "-")

	if len(trimmed) > 62 {
		trimmed = trimmed[:62]
		trimmed = strings.TrimRight(trimmed, "-")
	}

	if trimmed == "" {
		trimmed = "mcp-server"
	}
	return trimmed
}

// DetectTransport infers the transport type from fields when not explicitly declared.
func DetectTransport(rawCommand, rawURL, declared string) TransportType {
	d := strings.ToLower(strings.TrimSpace(declared))
	switch d {
	case "stdio":
		return TransportStdio
	case "sse":
		return TransportSSE
	case "http", "streamable-http":
		return TransportStreamableHTTP
	}

	if strings.TrimSpace(rawURL) != "" {
		if strings.Contains(strings.ToLower(rawURL), "/sse") {
			return TransportSSE
		}
		return TransportStreamableHTTP
	}
	return TransportStdio
}

// ParseStandardMCPConfig parses standard MCP configuration data in any of the 5 mainstream forms:
// 1. Claude/Cursor format: {"mcpServers": { ... }}
// 2. Zed format: {"context_servers": { ... }}
// 3. Flat dictionary: {"server-1": { ... }}
// 4. Single server JSON snippet: {"command": "...", "args": [...]}
// 5. Array of servers: [{"name": "...", "command": "..."}]
func ParseStandardMCPConfig(data []byte) ([]*BackendSpec, error) {
	data = bytes.TrimPrefix(data, []byte("\xef\xbb\xbf"))
	data = bytes.TrimSpace(data)
	if len(data) == 0 {
		return nil, errors.New("empty JSON configuration")
	}

	// Case A: Array of servers
	if data[0] == '[' {
		var list []RawServerEntry
		if err := json.Unmarshal(data, &list); err != nil {
			return nil, fmt.Errorf("failed to parse JSON array: %w", err)
		}
		return convertRawListToSpecs(list)
	}

	// Case B: Object
	var root map[string]json.RawMessage
	if err := json.Unmarshal(data, &root); err != nil {
		return nil, fmt.Errorf("failed to parse JSON object: %w", err)
	}

	// Check for wrapper keys: "mcpServers", "context_servers", "servers"
	for _, wrapperKey := range []string{"mcpServers", "context_servers", "servers"} {
		if subRaw, ok := root[wrapperKey]; ok {
			var serversMap map[string]RawServerEntry
			if err := json.Unmarshal(subRaw, &serversMap); err == nil && len(serversMap) > 0 {
				return convertRawMapToSpecs(serversMap)
			}
		}
	}

	// Check if the root itself is a single server snippet (has "command" or "url")
	if _, hasCmd := root["command"]; hasCmd {
		var single RawServerEntry
		if err := json.Unmarshal(data, &single); err == nil && single.Command != "" {
			return convertRawListToSpecs([]RawServerEntry{single})
		}
	}
	if _, hasURL := root["url"]; hasURL {
		var single RawServerEntry
		if err := json.Unmarshal(data, &single); err == nil && single.URL != "" {
			return convertRawListToSpecs([]RawServerEntry{single})
		}
	}

	// Case C: Flat map of servers: {"serverA": {...}, "serverB": {...}}
	var flatMap map[string]RawServerEntry
	if err := json.Unmarshal(data, &flatMap); err == nil && len(flatMap) > 0 {
		return convertRawMapToSpecs(flatMap)
	}

	return nil, errors.New("no valid MCP servers found in JSON. Expected 'mcpServers' object, server map, or single server definition")
}

func convertRawMapToSpecs(m map[string]RawServerEntry) ([]*BackendSpec, error) {
	usedIDs := make(map[string]int)
	var specs []*BackendSpec

	for key, entry := range m {
		spec := buildSpecFromRaw(key, entry)
		// Ensure unique ID within this batch
		id := spec.ID
		if count, exists := usedIDs[id]; exists {
			usedIDs[id] = count + 1
			spec.ID = fmt.Sprintf("%s-%d", id, count+1)
		} else {
			usedIDs[id] = 1
		}

		if err := ValidateBackendSpec(spec); err != nil {
			return nil, fmt.Errorf("invalid server configuration for %q: %w", key, err)
		}
		specs = append(specs, spec)
	}

	if len(specs) == 0 {
		return nil, errors.New("no MCP servers found in configuration map")
	}
	return specs, nil
}

func convertRawListToSpecs(list []RawServerEntry) ([]*BackendSpec, error) {
	usedIDs := make(map[string]int)
	var specs []*BackendSpec

	for i, entry := range list {
		fallbackName := fmt.Sprintf("mcp-server-%d", i+1)
		spec := buildSpecFromRaw(fallbackName, entry)
		id := spec.ID
		if count, exists := usedIDs[id]; exists {
			usedIDs[id] = count + 1
			spec.ID = fmt.Sprintf("%s-%d", id, count+1)
		} else {
			usedIDs[id] = 1
		}

		if err := ValidateBackendSpec(spec); err != nil {
			return nil, fmt.Errorf("invalid server entry #%d (%s): %w", i+1, spec.ID, err)
		}
		specs = append(specs, spec)
	}

	if len(specs) == 0 {
		return nil, errors.New("empty server list")
	}
	return specs, nil
}

func buildSpecFromRaw(fallbackKey string, entry RawServerEntry) *BackendSpec {
	id := entry.ID
	if strings.TrimSpace(id) == "" {
		id = entry.Name
	}
	if strings.TrimSpace(id) == "" {
		id = fallbackKey
	}
	slugID := SlugifyServerID(id)

	name := entry.Name
	if strings.TrimSpace(name) == "" {
		name = fallbackKey
	}

	transport := DetectTransport(entry.Command, entry.URL, entry.Transport)

	envCopy := make(map[string]string)
	for k, v := range entry.Env {
		if strings.TrimSpace(k) != "" {
			envCopy[k] = v
		}
	}

	headersCopy := make(map[string]string)
	for k, v := range entry.Headers {
		if strings.TrimSpace(k) != "" {
			headersCopy[k] = v
		}
	}

	return &BackendSpec{
		ID:             slugID,
		Name:           name,
		Transport:      transport,
		Enabled:        !entry.Disabled,
		Command:        entry.Command,
		Args:           entry.Args,
		Cwd:            entry.Cwd,
		Env:            envCopy,
		URL:            entry.URL,
		Headers:        headersCopy,
		TimeoutSeconds: 30,
		Revision:       1,
		UpdatedAt:      time.Now(),
	}
}
