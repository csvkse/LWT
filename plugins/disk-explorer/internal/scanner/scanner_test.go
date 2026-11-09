package scanner

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	"lwt/disk-explorer/internal/model"
	"lwt/disk-explorer/internal/target"
)

func TestFormatHumanSize(t *testing.T) {
	tests := []struct {
		bytes    int64
		expected string
	}{
		{0, "0 B"},
		{500, "500 B"},
		{1024, "1.00 KB"},
		{1048576, "1.00 MB"},
		{1073741824, "1.00 GB"},
	}

	for _, tt := range tests {
		got := FormatHumanSize(tt.bytes)
		if got != tt.expected {
			t.Errorf("FormatHumanSize(%d) = %s, expected %s", tt.bytes, got, tt.expected)
		}
	}
}

func TestIsVirtualFs(t *testing.T) {
	if !IsVirtualFs("/proc") || !IsVirtualFs("/proc/1/ns") {
		t.Errorf("expected /proc to be virtual")
	}
	if !IsVirtualFs("/sys/class") {
		t.Errorf("expected /sys to be virtual")
	}
	if IsVirtualFs("/var/log") {
		t.Errorf("expected /var/log to be non-virtual")
	}
}

func TestFastWalkEngineLocal(t *testing.T) {
	tempDir := t.TempDir()

	// 创建测试目录结构
	sub1 := filepath.Join(tempDir, "sub1")
	sub2 := filepath.Join(tempDir, "sub2")
	_ = os.MkdirAll(sub1, 0755)
	_ = os.MkdirAll(sub2, 0755)

	_ = os.WriteFile(filepath.Join(sub1, "file1.txt"), make([]byte, 10240), 0644) // 10KB
	_ = os.WriteFile(filepath.Join(sub2, "file2.txt"), make([]byte, 20480), 0644) // 20KB

	engine := NewFastWalkEngine(4)
	tgt := target.NewContainerLocalTarget()
	ctx := context.Background()

	req := model.ScanRequest{
		Target:        "container",
		Path:          tempDir,
		MaxDepth:      2,
		OneFileSystem: true,
	}

	var lastProgress model.ScanProgress
	node, err := engine.Scan(ctx, tgt, req, func(p model.ScanProgress) {
		lastProgress = p
	})

	if err != nil {
		t.Fatalf("scan failed: %v", err)
	}

	if node == nil {
		t.Fatalf("expected non-nil node")
	}

	if node.SizeBytes < 30720 {
		t.Errorf("expected at least 30KB, got %d", node.SizeBytes)
	}

	if len(node.Children) != 2 {
		t.Errorf("expected 2 children, got %d", len(node.Children))
	}

	if lastProgress.RootPath != tempDir {
		t.Errorf("expected progress reported for rootPath %s", tempDir)
	}
}
