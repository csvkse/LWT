package network

import (
	"encoding/json"
	"path/filepath"
	"testing"
)

func TestPortMappingStore(t *testing.T) {
	tmpDir := t.TempDir()
	store, err := OpenStore(filepath.Join(tmpDir, "network"))
	if err != nil {
		t.Fatalf("failed to open store: %v", err)
	}

	m1 := PortMapping{
		ID:          "map-1",
		ContainerID: "ct-debian",
		Protocol:    "tcp",
		HostPort:    2222,
		TargetPort:  22,
		TargetIP:    "10.0.3.15",
		Enabled:     true,
		Description: "SSH forward",
	}

	if err := store.Upsert(m1); err != nil {
		t.Fatalf("upsert failed: %v", err)
	}

	list := store.List("ct-debian")
	if len(list) != 1 {
		t.Fatalf("expected 1 mapping, got %d", len(list))
	}
	if list[0].HostPort != 2222 {
		t.Errorf("expected host port 2222, got %d", list[0].HostPort)
	}

	// Conflict test
	m2 := PortMapping{
		ID:          "map-2",
		ContainerID: "ct-ubuntu",
		Protocol:    "tcp",
		HostPort:    2222,
		TargetPort:  22,
		Enabled:     true,
	}
	if err := store.Upsert(m2); err == nil {
		t.Errorf("expected port conflict error, got nil")
	}

	// Empty list must serialize to [] and never null
	emptyList := store.List("non-existent")
	if emptyList == nil || len(emptyList) != 0 {
		t.Errorf("expected non-nil empty slice, got %v", emptyList)
	}
	emptyJSON, _ := json.Marshal(emptyList)
	if string(emptyJSON) != "[]" {
		t.Errorf("expected empty JSON array '[]', got %s", string(emptyJSON))
	}

	// Delete test
	if err := store.Delete("map-1"); err != nil {
		t.Fatalf("delete failed: %v", err)
	}
	if len(store.List("ct-debian")) != 0 {
		t.Errorf("expected 0 mappings after deletion")
	}
}
