package config

import (
	"errors"
	"fmt"
	"net"
	"net/url"
	"regexp"
	"strings"
)

const (
	MaxServersCount       = 20
	MaxToolsPerServer     = 1000
	MaxServerIDLength     = 64
	MaxConfigEnvelopeBytes = 4 * 1024 * 1024 // 4 MiB
)

var (
	serverIDPattern = regexp.MustCompile(`^[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$`)
	tunnelIDPattern = regexp.MustCompile(`^tunnel_([a-z0-9]{4}_)?[a-z0-9]{32}$`)
	shellMetachars  = []string{";", "&", "|", ">", "<", "$", "`", "\n", "\r"}
)

// Validate checks complete Bridge configuration integrity.
func Validate(state State) error {
	if state.Tunnel.TunnelID != "" {
		if !tunnelIDPattern.MatchString(state.Tunnel.TunnelID) {
			return fmt.Errorf("invalid tunnelId %q: must match tunnel_<32 chars> or tunnel_<namespace>_<32 chars>", state.Tunnel.TunnelID)
		}
	}

	if len(state.Servers) > MaxServersCount {
		return fmt.Errorf("exceeded maximum server count: %d > %d", len(state.Servers), MaxServersCount)
	}

	for id, srv := range state.Servers {
		if srv == nil {
			return fmt.Errorf("server entry %q is nil", id)
		}
		if srv.ID != id {
			return fmt.Errorf("server key %q does not match server id %q", id, srv.ID)
		}
		if err := ValidateBackendSpec(srv); err != nil {
			return fmt.Errorf("server %q invalid: %w", id, err)
		}
	}

	for id, sel := range state.Selections {
		if sel == nil {
			return fmt.Errorf("selection entry %q is nil", id)
		}
		if sel.ServerID != id {
			return fmt.Errorf("selection key %q does not match server id %q", id, sel.ServerID)
		}
		if _, ok := state.Servers[id]; !ok {
			return fmt.Errorf("selection refers to non-existent server %q", id)
		}
		if len(sel.SelectedTools) > MaxToolsPerServer {
			return fmt.Errorf("selection for %q exceeds max tools limit: %d > %d", id, len(sel.SelectedTools), MaxToolsPerServer)
		}
	}

	return nil
}

// ValidateBackendSpec verifies a single backend definition.
func ValidateBackendSpec(spec *BackendSpec) error {
	if spec == nil {
		return errors.New("backend spec is nil")
	}
	if !serverIDPattern.MatchString(spec.ID) {
		return fmt.Errorf("invalid server ID %q: must be 1-64 lowercase alphanumeric/dash characters", spec.ID)
	}
	if strings.TrimSpace(spec.Name) == "" {
		return errors.New("server name cannot be empty")
	}

	switch spec.Transport {
	case TransportStdio:
		if strings.TrimSpace(spec.Command) == "" {
			return errors.New("stdio transport requires a command")
		}
		for _, meta := range shellMetachars {
			if strings.Contains(spec.Command, meta) {
				return fmt.Errorf("stdio command cannot contain shell metacharacter %q", meta)
			}
		}
	case TransportStreamableHTTP, TransportSSE:
		if strings.TrimSpace(spec.URL) == "" {
			return errors.New("http/sse transport requires a URL")
		}
		if err := ValidateURL(spec.URL); err != nil {
			return err
		}
	default:
		return fmt.Errorf("unsupported transport type: %q", spec.Transport)
	}

	if spec.TimeoutSeconds < 0 || spec.TimeoutSeconds > 120 {
		return fmt.Errorf("timeoutSeconds must be between 0 and 120, got %d", spec.TimeoutSeconds)
	}

	return nil
}

// ValidateURL enforces secure target URL constraints.
func ValidateURL(rawURL string) error {
	parsed, err := url.Parse(rawURL)
	if err != nil {
		return fmt.Errorf("malformed URL: %w", err)
	}

	if parsed.Scheme != "http" && parsed.Scheme != "https" {
		return fmt.Errorf("URL scheme must be http or https, got %q", parsed.Scheme)
	}

	if parsed.User != nil {
		return errors.New("URL userinfo credentials are not allowed")
	}

	if parsed.Fragment != "" {
		return errors.New("URL fragments are not allowed")
	}

	host := parsed.Hostname()
	if host == "" {
		return errors.New("URL host cannot be empty")
	}

	// Prohibit cloud metadata endpoints (169.254.169.254 or link-local IPv4/IPv6)
	ip := net.ParseIP(host)
	if ip != nil {
		if ip.IsLinkLocalUnicast() || ip.IsLinkLocalMulticast() || host == "169.254.169.254" {
			return fmt.Errorf("forbidden destination IP: %s (link-local/cloud metadata prohibited)", host)
		}
	}

	return nil
}

// ValidateSecretMutation checks mutation parameters.
func ValidateSecretMutation(mut SecretMutation) error {
	switch mut.Action {
	case SecretActionKeep:
		if mut.Value != "" {
			return errors.New("secret value must be empty when action is 'keep'")
		}
	case SecretActionClear:
		if mut.Value != "" {
			return errors.New("secret value must be empty when action is 'clear'")
		}
	case SecretActionReplace:
		if strings.TrimSpace(mut.Value) == "" {
			return errors.New("secret value cannot be empty when action is 'replace'")
		}
	default:
		return fmt.Errorf("unsupported secret action: %q", mut.Action)
	}
	return nil
}
