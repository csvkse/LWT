package catalog

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"regexp"
	"strings"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

var (
	validExportedNamePattern = regexp.MustCompile(`^[a-zA-Z0-9_-]{1,64}$`)
)

// ExportedToolName builds a stable, deterministic, collision-free exported tool name.
// Format: "<serverID>__<upstreamName>". If total length exceeds 64, it truncates upstream name
// and appends a short hash suffix while preserving the server prefix.
func ExportedToolName(serverID string, upstreamName string) string {
	combined := fmt.Sprintf("%s__%s", serverID, upstreamName)
	if len(combined) <= 64 && validExportedNamePattern.MatchString(combined) {
		return combined
	}

	// Sanitize any invalid characters
	sanitized := strings.Map(func(r rune) rune {
		if (r >= 'a' && r <= 'z') || (r >= 'A' && r <= 'Z') || (r >= '0' && r <= '9') || r == '_' || r == '-' {
			return r
		}
		return '_'
	}, combined)

	if len(sanitized) <= 64 {
		return sanitized
	}

	// If over 64 chars, preserve serverID prefix + hash of upstreamName
	hash := sha256.Sum256([]byte(upstreamName))
	hashSuffix := hex.EncodeToString(hash[:4]) // 8 hex chars

	prefix := serverID + "__"
	maxUpstreamLen := 64 - len(prefix) - 1 - len(hashSuffix)
	if maxUpstreamLen < 1 {
		prefix = serverID[:min(len(serverID), 32)] + "__"
		maxUpstreamLen = 64 - len(prefix) - 1 - len(hashSuffix)
	}

	trimmedUpstream := upstreamName
	if len(trimmedUpstream) > maxUpstreamLen {
		trimmedUpstream = trimmedUpstream[:maxUpstreamLen]
	}

	return fmt.Sprintf("%s%s_%s", prefix, trimmedUpstream, hashSuffix)
}

// ComputeSchemaDigest produces a canonical SHA-256 digest of a tool's parameters and description.
func ComputeSchemaDigest(tool *mcp.Tool) string {
	if tool == nil {
		return ""
	}

	payload := struct {
		Name        string          `json:"name"`
		Description string          `json:"description"`
		InputSchema json.RawMessage `json:"inputSchema,omitempty"`
	}{
		Name:        tool.Name,
		Description: tool.Description,
	}

	if tool.InputSchema != nil {
		if bytes, err := json.Marshal(tool.InputSchema); err == nil {
			payload.InputSchema = bytes
		}
	}

	data, _ := json.Marshal(payload)
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:])
}
