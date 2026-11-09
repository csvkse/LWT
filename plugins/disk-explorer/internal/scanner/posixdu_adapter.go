package scanner

import (
	"bufio"
	"context"
	"fmt"
	"path/filepath"
	"sort"
	"strconv"
	"strings"
	"sync/atomic"
	"time"

	"lwt/disk-explorer/internal/model"
	"lwt/disk-explorer/internal/target"
)

// PosixDuAdapter POSIX du 命令行分析适配器（保证跨宿主与特权穿透的 100% 兼容性）
type PosixDuAdapter struct{}

func NewPosixDuAdapter() *PosixDuAdapter {
	return &PosixDuAdapter{}
}

func (a *PosixDuAdapter) EngineName() string {
	return "posixdu"
}

func (a *PosixDuAdapter) IsAvailable(tgt target.IExecutionTargetStrategy) bool {
	return true
}

func (a *PosixDuAdapter) Scan(
	ctx context.Context,
	tgt target.IExecutionTargetStrategy,
	req model.ScanRequest,
	onProgress func(model.ScanProgress),
) (*model.FolderNode, error) {
	cleanPath := strings.TrimRight(req.Path, "/")
	if cleanPath == "" {
		cleanPath = "/"
	}

	maxDepth := req.MaxDepth
	if maxDepth <= 0 {
		maxDepth = 2
	}

	args := []string{"-k"}
	if req.OneFileSystem {
		args = append(args, "-x")
	}

	// 排除伪文件系统
	args = append(args,
		"--exclude=/proc/*",
		"--exclude=/sys/*",
		"--exclude=/dev/*",
		"--exclude=/run/*",
	)

	// 指定最大分析深度
	args = append(args, fmt.Sprintf("--max-depth=%d", maxDepth), cleanPath)

	cmd := tgt.WrapCommand(ctx, "du", args...)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, fmt.Errorf("failed to open du stdout pipe: %w", err)
	}

	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("failed to start du process: %w", err)
	}

	type rawEntry struct {
		path      string
		sizeBytes int64
	}

	var entries []rawEntry
	scanner := bufio.NewScanner(stdout)

	var scannedCount int64
	lastProgress := time.Now()

	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		if line == "" {
			continue
		}

		parts := strings.SplitN(line, "\t", 2)
		if len(parts) != 2 {
			continue
		}

		kb, err := strconv.ParseInt(strings.TrimSpace(parts[0]), 10, 64)
		if err != nil {
			continue
		}

		p := strings.TrimSpace(parts[1])
		sizeBytes := kb * 1024
		entries = append(entries, rawEntry{path: p, sizeBytes: sizeBytes})

		atomic.AddInt64(&scannedCount, 1)
		if onProgress != nil && time.Since(lastProgress) > 150*time.Millisecond {
			lastProgress = time.Now()
			onProgress(model.ScanProgress{
				Status:       model.StatusScanning,
				Target:       req.Target,
				RootPath:     cleanPath,
				ScannedFiles: atomic.LoadInt64(&scannedCount),
				ScannedDirs:  atomic.LoadInt64(&scannedCount),
				TotalBytes:   sizeBytes,
				CurrentPath:  p,
			})
		}
	}

	_ = cmd.Wait()

	if len(entries) == 0 {
		return &model.FolderNode{
			Name:      filepath.Base(cleanPath),
			Path:      cleanPath,
			SizeBytes: 0,
			HumanSize: "0 B",
			IsDir:     true,
			Percent:   100.0,
		}, nil
	}

	// 查找根节点条目（通常为最后一条）
	var rootEntry *rawEntry
	for i := len(entries) - 1; i >= 0; i-- {
		norm := strings.TrimRight(entries[i].path, "/")
		if norm == "" {
			norm = "/"
		}
		if norm == cleanPath {
			rootEntry = &entries[i]
			break
		}
	}

	totalRootSize := int64(0)
	if rootEntry != nil {
		totalRootSize = rootEntry.sizeBytes
	} else if len(entries) > 0 {
		totalRootSize = entries[len(entries)-1].sizeBytes
	}

	rootNode := &model.FolderNode{
		Name:      filepath.Base(cleanPath),
		Path:      cleanPath,
		SizeBytes: totalRootSize,
		HumanSize: FormatHumanSize(totalRootSize),
		IsDir:     true,
		Percent:   100.0,
		Children:  make([]*model.FolderNode, 0),
	}
	if cleanPath == "/" {
		rootNode.Name = "/"
	}

	// 将条目构造成直接子节点列表
	pathMap := make(map[string]*model.FolderNode)
	pathMap[cleanPath] = rootNode

	for _, entry := range entries {
		p := strings.TrimRight(entry.path, "/")
		if p == "" {
			p = "/"
		}
		if p == cleanPath {
			continue
		}

		parentDir := filepath.Dir(p)
		if p == "/" {
			continue
		}

		childNode := &model.FolderNode{
			Name:      filepath.Base(p),
			Path:      p,
			SizeBytes: entry.sizeBytes,
			HumanSize: FormatHumanSize(entry.sizeBytes),
			IsDir:     true,
			Children:  make([]*model.FolderNode, 0),
		}
		pathMap[p] = childNode

		// 如果父级是根节点，或者在 pathMap 中找到父级
		if parent, ok := pathMap[parentDir]; ok {
			parent.Children = append(parent.Children, childNode)
		} else if cleanPath == "/" || strings.HasPrefix(p, cleanPath+"/") {
			// 作为根的候选直接或间接子级
			rel, err := filepath.Rel(cleanPath, p)
			if err == nil && !strings.Contains(rel, string(filepath.Separator)) {
				rootNode.Children = append(rootNode.Children, childNode)
			}
		}
	}

	// 对各级子节点降序排序并计算百分比
	sortAndCalculatePercent(rootNode)

	if onProgress != nil {
		onProgress(model.ScanProgress{
			Status:       model.StatusAggregating,
			Target:       req.Target,
			RootPath:     cleanPath,
			ScannedFiles: atomic.LoadInt64(&scannedCount),
			ScannedDirs:  atomic.LoadInt64(&scannedCount),
			TotalBytes:   totalRootSize,
			CurrentPath:  cleanPath,
		})
	}

	return rootNode, nil
}

func sortAndCalculatePercent(node *model.FolderNode) {
	if node == nil || len(node.Children) == 0 {
		return
	}

	sort.Slice(node.Children, func(i, j int) bool {
		return node.Children[i].SizeBytes > node.Children[j].SizeBytes
	})

	for _, child := range node.Children {
		if node.SizeBytes > 0 {
			child.Percent = (float64(child.SizeBytes) / float64(node.SizeBytes)) * 100.0
		}
		sortAndCalculatePercent(child)
	}
}

// Probe 执行宿主或 POSIX du 单层增量下钻探测
func (a *PosixDuAdapter) Probe(
	ctx context.Context,
	tgt target.IExecutionTargetStrategy,
	req model.ProbeRequest,
) (*model.FolderNode, error) {
	cleanPath := strings.TrimRight(req.SubPath, "/")
	if cleanPath == "" {
		cleanPath = "/"
	}

	maxDepth := req.Depth
	if maxDepth <= 0 {
		maxDepth = 1
	}

	args := []string{"-k"}
	if req.OneFileSystem {
		args = append(args, "-x")
	}
	args = append(args,
		"--exclude=/proc/*",
		"--exclude=/sys/*",
		"--exclude=/dev/*",
		"--exclude=/run/*",
		fmt.Sprintf("--max-depth=%d", maxDepth),
		cleanPath,
	)

	cmd := tgt.WrapCommand(ctx, "du", args...)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, fmt.Errorf("failed to open du stdout pipe: %w", err)
	}

	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("failed to start du process: %w", err)
	}

	type rawEntry struct {
		path      string
		sizeBytes int64
	}

	var entries []rawEntry
	sc := bufio.NewScanner(stdout)
	for sc.Scan() {
		line := strings.TrimSpace(sc.Text())
		if line == "" {
			continue
		}
		parts := strings.SplitN(line, "\t", 2)
		if len(parts) != 2 {
			continue
		}
		kb, err := strconv.ParseInt(strings.TrimSpace(parts[0]), 10, 64)
		if err != nil {
			continue
		}
		p := strings.TrimSpace(parts[1])
		entries = append(entries, rawEntry{path: p, sizeBytes: kb * 1024})
	}
	_ = cmd.Wait()

	if len(entries) == 0 {
		return &model.FolderNode{
			Name:      filepath.Base(cleanPath),
			Path:      cleanPath,
			SizeBytes: 0,
			HumanSize: "0 B",
			IsDir:     true,
			Percent:   100.0,
		}, nil
	}

	var totalRootSize int64
	for i := len(entries) - 1; i >= 0; i-- {
		norm := strings.TrimRight(entries[i].path, "/")
		if norm == "" {
			norm = "/"
		}
		if norm == cleanPath {
			totalRootSize = entries[i].sizeBytes
			break
		}
	}
	if totalRootSize == 0 && len(entries) > 0 {
		totalRootSize = entries[len(entries)-1].sizeBytes
	}

	rootNode := &model.FolderNode{
		Name:      filepath.Base(cleanPath),
		Path:      cleanPath,
		SizeBytes: totalRootSize,
		HumanSize: FormatHumanSize(totalRootSize),
		IsDir:     true,
		Percent:   100.0,
		Children:  make([]*model.FolderNode, 0),
	}
	if cleanPath == "/" {
		rootNode.Name = "/"
	}

	for _, entry := range entries {
		p := strings.TrimRight(entry.path, "/")
		if p == "" || p == cleanPath {
			continue
		}
		parentDir := filepath.Dir(p)
		if parentDir != cleanPath && parentDir != "/" && cleanPath != "/" {
			continue
		}

		childNode := &model.FolderNode{
			Name:      filepath.Base(p),
			Path:      p,
			SizeBytes: entry.sizeBytes,
			HumanSize: FormatHumanSize(entry.sizeBytes),
			IsDir:     true,
			Children:  make([]*model.FolderNode, 0),
		}
		if totalRootSize > 0 {
			childNode.Percent = (float64(childNode.SizeBytes) / float64(totalRootSize)) * 100.0
		}
		rootNode.Children = append(rootNode.Children, childNode)
	}

	sortAndCalculatePercent(rootNode)
	return rootNode, nil
}
