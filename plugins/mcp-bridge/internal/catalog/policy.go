package catalog

import (
	"fmt"
	"sort"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/config"
)

// BuildCandidate compiles a candidate snapshot from state and discovered backend tools.
func BuildCandidate(state config.State, discovered map[string][]*mcp.Tool) (*Snapshot, error) {
	snap := &Snapshot{
		Revision: state.DesiredRevision,
		Tools:    make([]*mcp.Tool, 0),
		Bindings: make(map[string]Binding),
	}

	// Sort server IDs for deterministic compilation
	var serverIDs []string
	for id := range state.Servers {
		serverIDs = append(serverIDs, id)
	}
	sort.Strings(serverIDs)

	for _, srvID := range serverIDs {
		srv := state.Servers[srvID]
		if srv == nil || !srv.Enabled {
			continue // Skip disabled or missing servers
		}

		sel, hasSel := state.Selections[srvID]
		var selectedMap map[string]bool
		if hasSel && sel != nil {
			if len(sel.SelectedTools) == 0 {
				// Operator explicitly reviewed and selected 0 tools
				continue
			}
			selectedMap = make(map[string]bool)
			for _, name := range sel.SelectedTools {
				selectedMap[name] = true
			}
		}

		toolsForServer := discovered[srvID]
		for _, tool := range toolsForServer {
			if tool == nil {
				continue
			}
			if selectedMap != nil && !selectedMap[tool.Name] {
				continue
			}

			currentDigest := ComputeSchemaDigest(tool)

			// Check schema drift against approved digest if present
			if sel != nil && sel.ApprovedToolDigests != nil {
				if approvedDigest, ok := sel.ApprovedToolDigests[tool.Name]; ok {
					if approvedDigest != "" && approvedDigest != currentDigest {
						// Schema changed without operator re-approval; exclude from candidate
						continue
					}
				}
			}

			exportedName := ExportedToolName(srv.ID, tool.Name)
			if _, exists := snap.Bindings[exportedName]; exists {
				return nil, fmt.Errorf("duplicate exported tool name collision: %q", exportedName)
			}

			binding := Binding{
				ServerID:        srv.ID,
				BackendRevision: srv.Revision,
				ExportedName:    exportedName,
				UpstreamName:    tool.Name,
				SchemaDigest:    currentDigest,
			}

			exposedTool := &mcp.Tool{
				Name:        exportedName,
				Description: tool.Description,
				InputSchema: tool.InputSchema,
			}

			snap.Bindings[exportedName] = binding
			snap.Tools = append(snap.Tools, exposedTool)
		}
	}

	// Deterministic sort by exported tool name
	sort.Slice(snap.Tools, func(i, j int) bool {
		return snap.Tools[i].Name < snap.Tools[j].Name
	})

	return snap, nil
}
