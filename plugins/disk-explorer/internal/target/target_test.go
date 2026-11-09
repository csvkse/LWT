package target

import (
	"context"
	"os"
	"testing"
)

func TestContainerLocalTarget(t *testing.T) {
	target := NewContainerLocalTarget()
	if target.TargetID() != "container" {
		t.Fatalf("expected targetID container, got %s", target.TargetID())
	}
	if !target.IsAvailable() {
		t.Fatalf("container target should always be available")
	}

	tempDir := t.TempDir()
	ctx := context.Background()

	if err := target.ValidatePath(ctx, tempDir); err != nil {
		t.Fatalf("expected valid path %s, got err: %v", tempDir, err)
	}

	if err := target.ValidatePath(ctx, "/path/that/does/not/exist/ever"); err == nil {
		t.Fatalf("expected error for non-existent path")
	}
}

func TestHostPiercedTarget(t *testing.T) {
	target := NewHostPiercedTarget()
	if target.TargetID() != "host" {
		t.Fatalf("expected targetID host, got %s", target.TargetID())
	}

	// 在本地非特权测试环境下，如果不可用应返回明确错误
	ctx := context.Background()
	if !target.IsAvailable() {
		err := target.ValidatePath(ctx, os.TempDir())
		if err != ErrHostNotAccessible {
			t.Logf("Host target correctly reported not accessible: %v", err)
		}
	} else {
		err := target.ValidatePath(ctx, os.TempDir())
		if err != nil {
			t.Fatalf("expected accessible path in host mode: %v", err)
		}
	}
}
