package capabilities

import (
	"testing"
)

func TestDetectorBasic(t *testing.T) {
	d := NewDetector()
	report := d.Detect()

	if report.PlatformOS == "" {
		t.Fatalf("expected non-empty platform OS")
	}

	if len(report.AvailableTargets) == 0 {
		t.Fatalf("expected at least one available target")
	}

	// 验证第一项目标 ID 必须是 container
	if report.AvailableTargets[0].ID != "container" {
		t.Fatalf("expected first target to be container, got %s", report.AvailableTargets[0].ID)
	}

	// 缓存有效性验证
	report2 := d.Detect()
	if report2.PlatformOS != report.PlatformOS {
		t.Fatalf("expected cached report to match")
	}
}
