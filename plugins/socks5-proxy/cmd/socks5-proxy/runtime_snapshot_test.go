package main

import (
	"encoding/json"
	"testing"
)

func TestRuntimeSnapshotContainsCapacitySignals(t *testing.T) {
	snapshot := currentRuntimeSnapshot()
	if snapshot.PID <= 0 {
		t.Errorf("pid = %d, want a positive process id", snapshot.PID)
	}
	if snapshot.GoVersion == "" {
		t.Error("goVersion is empty")
	}
	if snapshot.Goroutines == 0 {
		t.Error("goroutines = 0, want at least the test goroutine")
	}
	if snapshot.GOMAXPROCS == 0 {
		t.Error("gomaxprocs = 0, want a positive scheduler limit")
	}
	if snapshot.HeapObjectsBytes == 0 {
		t.Error("heapObjectsBytes = 0, want the test process heap")
	}
}

func TestRuntimeSnapshotUsesStableJSONFieldNames(t *testing.T) {
	encoded, err := json.Marshal(currentRuntimeSnapshot())
	if err != nil {
		t.Fatalf("marshal runtime snapshot: %v", err)
	}

	var fields map[string]json.RawMessage
	if err := json.Unmarshal(encoded, &fields); err != nil {
		t.Fatalf("unmarshal runtime snapshot: %v", err)
	}
	for _, name := range []string{"pid", "goVersion", "goroutines", "gomaxprocs", "heapObjectsBytes", "gcHeapLiveBytes", "gcHeapAllocBytes", "gcHeapAllocObjects", "gcCycles"} {
		if _, ok := fields[name]; !ok {
			t.Errorf("runtime snapshot missing JSON field %q", name)
		}
	}
}
