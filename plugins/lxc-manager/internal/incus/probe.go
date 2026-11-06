// Package incus provides the Incus runtime adapter.
package incus

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"strings"
)

type Status string

const (
	Ready         Status = "Ready"
	RequiresSetup Status = "RequiresSetup"
	Unknown       Status = "Unknown"
)

type Capability struct {
	Status     Status          `json:"status"`
	Version    string          `json:"version,omitempty"`
	Extensions map[string]bool `json:"extensions,omitempty"`
}

// Probe is read-only. Ready confirms authentication, not storage or creation capability.
func Probe(ctx context.Context, client *http.Client, endpoint string) Capability {
	unknown := Capability{Status: Unknown}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, strings.TrimRight(endpoint, "/")+"/1.0", nil)
	if err != nil {
		return unknown
	}
	resp, err := client.Do(req)
	if err != nil {
		return unknown
	}
	defer resp.Body.Close()
	if resp.StatusCode == http.StatusUnauthorized || resp.StatusCode == http.StatusForbidden {
		return Capability{Status: RequiresSetup}
	}
	if resp.StatusCode != http.StatusOK {
		return unknown
	}
	var body struct {
		Type       string `json:"type"`
		StatusCode int    `json:"status_code"`
		Metadata   struct {
			Auth        string `json:"auth"`
			Environment struct {
				ServerVersion string `json:"server_version"`
			} `json:"environment"`
			APIExtensions []string `json:"api_extensions"`
		} `json:"metadata"`
	}
	if json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&body) != nil || body.Type != "sync" || body.StatusCode != 200 {
		return unknown
	}
	if body.Metadata.Auth != "trusted" {
		return Capability{Status: RequiresSetup}
	}
	extensions := make(map[string]bool, len(body.Metadata.APIExtensions))
	for _, extension := range body.Metadata.APIExtensions {
		extensions[extension] = true
	}
	return Capability{Status: Ready, Version: body.Metadata.Environment.ServerVersion, Extensions: extensions}
}
