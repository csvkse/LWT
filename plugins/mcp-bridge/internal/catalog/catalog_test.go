package catalog_test

import (
	"testing"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/catalog"
	"linuxwebtool.local/mcp-bridge/internal/config"
)

func TestExportedToolName(t *testing.T) {
	name1 := catalog.ExportedToolName("server1", "search")
	name2 := catalog.ExportedToolName("server2", "search")
	if name1 == name2 {
		t.Errorf("expected different exported names for different servers, got %q vs %q", name1, name2)
	}

	case1 := catalog.ExportedToolName("srv", "search")
	case2 := catalog.ExportedToolName("srv", "Search")
	if case1 == case2 {
		t.Errorf("expected case sensitivity preserved, got %q vs %q", case1, case2)
	}

	// Long name within 64 chars
	veryLong := "a_very_long_upstream_tool_name_that_exceeds_sixty_four_characters_easily_in_production"
	longExported := catalog.ExportedToolName("myserver", veryLong)
	if len(longExported) > 64 {
		t.Errorf("exported name exceeded 64 chars: %d (%s)", len(longExported), longExported)
	}
}

func TestBuildCandidatePolicy(t *testing.T) {
	state := config.NewInitialState()
	state.DesiredRevision = 5

	state.Servers["srv-a"] = &config.BackendSpec{
		ID:       "srv-a",
		Name:     "Server A",
		Enabled:  true,
		Revision: 1,
	}
	state.Servers["srv-b"] = &config.BackendSpec{
		ID:       "srv-b",
		Name:     "Server B",
		Enabled:  false, // Disabled server!
		Revision: 1,
	}

	discovered := map[string][]*mcp.Tool{
		"srv-a": {
			{Name: "tool1", Description: "Tool 1"},
			{Name: "tool2", Description: "Tool 2"},
			{Name: "tool3", Description: "Tool 3"},
		},
		"srv-b": {
			{Name: "tool_b", Description: "Tool B"},
		},
	}

	// Case 1: Empty selection -> zero tools exposed
	state.Selections["srv-a"] = &config.ServerSelection{
		ServerID:      "srv-a",
		SelectedTools: []string{},
	}

	cand, err := catalog.BuildCandidate(state, discovered)
	if err != nil {
		t.Fatalf("failed to build candidate: %v", err)
	}
	if len(cand.Tools) != 0 {
		t.Errorf("expected 0 tools for empty selection, got %d", len(cand.Tools))
	}

	// Case 2: Only tool1 selected -> only tool1 exposed
	state.Selections["srv-a"].SelectedTools = []string{"tool1"}
	cand, err = catalog.BuildCandidate(state, discovered)
	if err != nil {
		t.Fatalf("failed to build candidate: %v", err)
	}
	if len(cand.Tools) != 1 {
		t.Fatalf("expected 1 tool exposed, got %d", len(cand.Tools))
	}
	if cand.Tools[0].Name != "srv-a__tool1" {
		t.Errorf("expected srv-a__tool1, got %s", cand.Tools[0].Name)
	}

	// Case 3: Disabled server tools should never be exposed
	state.Selections["srv-b"] = &config.ServerSelection{
		ServerID:      "srv-b",
		SelectedTools: []string{"tool_b"},
	}
	cand, err = catalog.BuildCandidate(state, discovered)
	if err != nil {
		t.Fatalf("failed to build candidate: %v", err)
	}
	if len(cand.Tools) != 1 { // Only tool1 from srv-a
		t.Errorf("disabled server tools should not be exposed, got %d tools", len(cand.Tools))
	}

	// Case 4: Schema change drift check
	digestTool1 := catalog.ComputeSchemaDigest(discovered["srv-a"][0])
	state.Selections["srv-a"].ApprovedToolDigests = map[string]string{
		"tool1": digestTool1,
	}

	// When digest matches -> exposed
	cand, err = catalog.BuildCandidate(state, discovered)
	if err != nil || len(cand.Tools) != 1 {
		t.Errorf("expected tool1 with matching digest to be exposed")
	}

	// When upstream schema changes -> digest mismatches -> unexposed (requires review)
	discovered["srv-a"][0].Description = "Modified schema description!"
	cand, err = catalog.BuildCandidate(state, discovered)
	if err != nil {
		t.Fatalf("failed to build candidate: %v", err)
	}
	if len(cand.Tools) != 0 {
		t.Errorf("expected tool with modified schema to be withheld pending review, got %d", len(cand.Tools))
	}

	// Case 5: Nil selection (new server added without manual selection) -> Auto-select all discovered tools
	delete(state.Selections, "srv-a")
	cand, err = catalog.BuildCandidate(state, discovered)
	if err != nil {
		t.Fatalf("failed to build candidate for nil selection: %v", err)
	}
	if len(cand.Tools) != 3 {
		t.Errorf("expected 3 tools for auto-selection when selections is nil, got %d", len(cand.Tools))
	}
}

func TestRegistryPublish(t *testing.T) {
	reg := catalog.NewRegistry()
	curr := reg.Current()
	if curr.Revision != 0 || len(curr.Tools) != 0 {
		t.Errorf("unexpected initial registry state: %+v", curr)
	}

	newSnap := &catalog.Snapshot{
		Revision: 42,
		Tools: []*mcp.Tool{
			{Name: "srv__echo"},
		},
		Bindings: map[string]catalog.Binding{
			"srv__echo": {ExportedName: "srv__echo", UpstreamName: "echo"},
		},
	}
	reg.Publish(newSnap)

	published := reg.Current()
	if published.Revision != 42 || len(published.Tools) != 1 {
		t.Errorf("expected published revision 42, got %+v", published)
	}
}
