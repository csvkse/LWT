package fsm

import (
	"context"
	"os"
	"path/filepath"
	"testing"
	"time"

	"lwt/disk-explorer/internal/model"
)

func TestTaskManagerLifecycle(t *testing.T) {
	tempDir := t.TempDir()

	// 构造测试子文件
	_ = os.WriteFile(filepath.Join(tempDir, "big.bin"), make([]byte, 50000), 0644)
	_ = os.WriteFile(filepath.Join(tempDir, "small1.bin"), make([]byte, 10), 0644)
	_ = os.WriteFile(filepath.Join(tempDir, "small2.bin"), make([]byte, 10), 0644)

	mgr := NewTaskManager()
	ctx := context.Background()

	req := model.ScanRequest{
		Target:                "container",
		Path:                  tempDir,
		MaxDepth:              2,
		PruneThresholdPercent: 1.0, // 1% 以下将被剪枝
	}

	task, err := mgr.StartScan(ctx, req)
	if err != nil {
		t.Fatalf("failed to start scan: %v", err)
	}

	if task.ID == "" {
		t.Fatalf("expected non-empty task ID")
	}

	// 订阅进度流
	ch, unsub := task.Subscribe()
	defer unsub()

	// 等待完成
	timeout := time.After(5 * time.Second)
	completed := false

	for !completed {
		select {
		case p, ok := <-ch:
			if !ok {
				completed = true
				break
			}
			if p.Status == model.StatusCompleted {
				completed = true
			} else if p.Status == model.StatusFailed {
				t.Fatalf("scan failed: %s", p.ErrorMessage)
			}
		case <-timeout:
			t.Fatalf("scan timed out waiting for completion")
		}
	}

	snapshot := task.Snapshot()
	if snapshot.Status != model.StatusCompleted {
		t.Fatalf("expected status Completed, got %s", snapshot.Status)
	}

	if snapshot.Result == nil {
		t.Fatalf("expected non-nil result tree")
	}

	if snapshot.Result.SizeBytes < 50000 {
		t.Errorf("expected size at least 50000, got %d", snapshot.Result.SizeBytes)
	}
}

func TestTaskManagerAbort(t *testing.T) {
	tempDir := t.TempDir()
	mgr := NewTaskManager()
	ctx := context.Background()

	req := model.ScanRequest{
		Target:   "container",
		Path:     tempDir,
		MaxDepth: 2,
	}

	task, err := mgr.StartScan(ctx, req)
	if err != nil {
		t.Fatalf("failed to start scan: %v", err)
	}

	err = mgr.AbortScan(task.ID)
	if err != nil {
		t.Fatalf("failed to abort scan: %v", err)
	}

	time.Sleep(50 * time.Millisecond)
	snapshot := task.Snapshot()
	if snapshot.Status != model.StatusCancelled && snapshot.Status != model.StatusCompleted {
		t.Fatalf("expected cancelled or completed, got %s", snapshot.Status)
	}
}

func TestTaskManagerProbeAndUnranked(t *testing.T) {
	tempDir := t.TempDir()
	subDir := filepath.Join(tempDir, "sub_folder")
	_ = os.Mkdir(subDir, 0755)

	// 在主目录和子目录创建若干文件
	_ = os.WriteFile(filepath.Join(tempDir, "big_file.bin"), make([]byte, 80000), 0644)
	_ = os.WriteFile(filepath.Join(tempDir, "tiny1.log"), make([]byte, 20), 0644)
	_ = os.WriteFile(filepath.Join(tempDir, "tiny2.log"), make([]byte, 30), 0644)
	_ = os.WriteFile(filepath.Join(subDir, "inner_child.bin"), make([]byte, 10000), 0644)

	mgr := NewTaskManager()
	ctx := context.Background()

	task, err := mgr.StartScan(ctx, model.ScanRequest{
		Target:                "container",
		Path:                  tempDir,
		MaxDepth:              1,   // 只扫描到第 1 层，sub_folder 不会展开 inner_child
		PruneThresholdPercent: 1.0, // tiny 文件会被剪枝至 unranked
	})
	if err != nil {
		t.Fatalf("start scan failed: %v", err)
	}

	// 等待扫描完成
	timeout := time.After(3 * time.Second)
	for {
		snap := task.Snapshot()
		if snap.Status == model.StatusCompleted {
			break
		}
		if snap.Status == model.StatusFailed {
			t.Fatalf("scan failed: %s", snap.Progress.ErrorMessage)
		}
		select {
		case <-timeout:
			t.Fatalf("timed out")
		default:
			time.Sleep(20 * time.Millisecond)
		}
	}

	// 1. 验证获取 unranked 微小项
	unrankedResp, err := mgr.GetUnrankedItems(task.ID, model.UnrankedQueryRequest{
		Path:     tempDir,
		Page:     1,
		PageSize: 10,
	})
	if err != nil {
		t.Fatalf("get unranked failed: %v", err)
	}
	if unrankedResp.TotalUnranked < 2 {
		t.Errorf("expected at least 2 unranked items, got %d", unrankedResp.TotalUnranked)
	}

	// 2. 验证关键字过滤
	filteredResp, err := mgr.GetUnrankedItems(task.ID, model.UnrankedQueryRequest{
		Path:    tempDir,
		Keyword: "tiny1",
	})
	if err != nil {
		t.Fatalf("get filtered unranked failed: %v", err)
	}
	if filteredResp.TotalUnranked != 1 {
		t.Errorf("expected 1 item matching tiny1, got %d", filteredResp.TotalUnranked)
	}

	// 3. 验证即时局部探针 ProbeSubPath 与树嫁接
	probed, err := mgr.ProbeSubPath(ctx, task.ID, model.ProbeRequest{
		SubPath: subDir,
		Depth:   1,
	})
	if err != nil {
		t.Fatalf("probe sub path failed: %v", err)
	}
	if len(probed.Children) == 0 {
		t.Fatalf("expected probed children for sub_folder, got 0")
	}
	if probed.Children[0].Name != "inner_child.bin" {
		t.Errorf("expected inner_child.bin, got %s", probed.Children[0].Name)
	}

	// 验证已动态嫁接至原任务内存树
	rootSnap := task.Snapshot().Result
	var subInTree *model.FolderNode
	for _, c := range rootSnap.Children {
		if c.Path == subDir {
			subInTree = c
			break
		}
	}
	if subInTree == nil || len(subInTree.Children) == 0 {
		t.Fatalf("expected sub_folder in task.Result to be grafted with children")
	}
	if subInTree.Children[0].Name != "inner_child.bin" {
		t.Errorf("expected grafted child inner_child.bin, got %s", subInTree.Children[0].Name)
	}
}
