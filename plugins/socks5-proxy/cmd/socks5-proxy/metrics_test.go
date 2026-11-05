package main

import (
	"testing"
	"time"
)

func TestPhaseMetricObserveAndSnapshot(t *testing.T) {
	var metric PhaseMetric
	metric.Observe(2 * time.Millisecond)
	metric.Observe(5 * time.Millisecond)

	got := metric.Snapshot()
	if got.Count != 2 {
		t.Fatalf("count = %d, want 2", got.Count)
	}
	if got.TotalNs != int64(7*time.Millisecond) {
		t.Fatalf("totalNs = %d, want %d", got.TotalNs, int64(7*time.Millisecond))
	}
	if got.MaxNs != int64(5*time.Millisecond) {
		t.Fatalf("maxNs = %d, want %d", got.MaxNs, int64(5*time.Millisecond))
	}
}
