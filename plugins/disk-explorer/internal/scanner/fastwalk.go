package scanner

import (
	"context"
	"os"
	"path/filepath"
	"sort"
	"sync"
	"sync/atomic"
	"time"

	"lwt/disk-explorer/internal/model"
	"lwt/disk-explorer/internal/target"
)

// FastWalkEngine Go 原生多协程并发扫描内核
type FastWalkEngine struct {
	concurrency int
}

// NewFastWalkEngine 创建原生扫描引擎实例
func NewFastWalkEngine(concurrency int) *FastWalkEngine {
	if concurrency <= 0 {
		concurrency = 16
	}
	return &FastWalkEngine{concurrency: concurrency}
}

func (e *FastWalkEngine) EngineName() string {
	return "fastwalk"
}

func (e *FastWalkEngine) IsAvailable(tgt target.IExecutionTargetStrategy) bool {
	// 针对 container 始终原生可用；针对 host 在非特权下通过降级策略
	return true
}

func (e *FastWalkEngine) Scan(
	ctx context.Context,
	tgt target.IExecutionTargetStrategy,
	req model.ScanRequest,
	onProgress func(model.ScanProgress),
) (*model.FolderNode, error) {
	// 如果目标是 host 且需要穿透执行，如果通过 nsenter，调用 host 降级适配或 du 引擎
	if tgt.TargetID() == "host" {
		duAdapter := NewPosixDuAdapter()
		return duAdapter.Scan(ctx, tgt, req, onProgress)
	}

	cleanRoot := filepath.Clean(req.Path)
	fi, err := os.Lstat(cleanRoot)
	if err != nil {
		return nil, err
	}
	if !fi.IsDir() {
		node := &model.FolderNode{
			Name:      filepath.Base(cleanRoot),
			Path:      cleanRoot,
			SizeBytes: fi.Size(),
			HumanSize: FormatHumanSize(fi.Size()),
			FileCount: 1,
			DirCount:  0,
			Percent:   100.0,
			IsDir:     false,
		}
		return node, nil
	}

	maxDepth := req.MaxDepth
	if maxDepth <= 0 {
		maxDepth = 3
	}

	var scannedFiles int64
	var scannedDirs int64
	var totalBytes int64

	lastProgress := time.Now()
	var progressMu sync.Mutex

	reportProgress := func(currentPath string, force bool) {
		if onProgress == nil {
			return
		}
		progressMu.Lock()
		defer progressMu.Unlock()
		if !force && time.Since(lastProgress) < 150*time.Millisecond {
			return
		}
		lastProgress = time.Now()
		onProgress(model.ScanProgress{
			Status:       model.StatusScanning,
			Target:       req.Target,
			RootPath:     cleanRoot,
			ScannedFiles: atomic.LoadInt64(&scannedFiles),
			ScannedDirs:  atomic.LoadInt64(&scannedDirs),
			TotalBytes:   atomic.LoadInt64(&totalBytes),
			CurrentPath:  currentPath,
		})
	}

	sem := make(chan struct{}, e.concurrency)

	var walkDir func(path string, currentDepth int) (*model.FolderNode, error)
	walkDir = func(path string, currentDepth int) (*model.FolderNode, error) {
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		default:
		}

		if IsVirtualFs(path) {
			return &model.FolderNode{
				Name:      filepath.Base(path),
				Path:      path,
				IsDir:     true,
				HumanSize: "0 B",
			}, nil
		}

		sem <- struct{}{}
		entries, err := os.ReadDir(path)
		<-sem
		if err != nil {
			// 遇到权限不足或无法读取的目录，优雅跳过
			return &model.FolderNode{
				Name:      filepath.Base(path),
				Path:      path,
				IsDir:     true,
				HumanSize: "0 B",
			}, nil
		}

		atomic.AddInt64(&scannedDirs, 1)
		reportProgress(path, false)

		node := &model.FolderNode{
			Name:     filepath.Base(path),
			Path:     path,
			IsDir:    true,
			Children: make([]*model.FolderNode, 0, len(entries)),
		}

		type subResult struct {
			child *model.FolderNode
			err   error
		}

		var subDirs []string
		var directFilesSize int64
		var directFileCount int64

		for _, entry := range entries {
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			default:
			}

			childPath := filepath.Join(path, entry.Name())
			if entry.IsDir() {
				// 排除虚拟文件系统与挂载边界
				if !IsVirtualFs(childPath) {
					subDirs = append(subDirs, childPath)
				}
			} else {
				// 文件或符号链接
				atomic.AddInt64(&scannedFiles, 1)
				directFileCount++
				info, err := entry.Info()
				if err == nil {
					size := info.Size()
					directFilesSize += size
					atomic.AddInt64(&totalBytes, size)
					// 若在允许深度内，记录顶级文件
					if currentDepth < maxDepth {
						node.Children = append(node.Children, &model.FolderNode{
							Name:      entry.Name(),
							Path:      childPath,
							SizeBytes: size,
							HumanSize: FormatHumanSize(size),
							FileCount: 1,
							DirCount:  0,
							IsDir:     false,
						})
					}
				}
			}
		}

		node.SizeBytes += directFilesSize
		node.FileCount += directFileCount

		// 递归遍历子目录
		if len(subDirs) > 0 {
			resChan := make(chan subResult, len(subDirs))
			var wg sync.WaitGroup

			for _, sub := range subDirs {
				subPath := sub
				if currentDepth >= maxDepth {
					// 超过最大显示深度，只快速计算大小，不再构建多层嵌套子节点
					var quickSize int64
					var quickFiles int64
					var quickDirs int64
					_ = filepath.WalkDir(subPath, func(p string, d os.DirEntry, err error) error {
						select {
						case <-ctx.Done():
							return ctx.Err()
						default:
						}
						if err != nil {
							return filepath.SkipDir
						}
						if IsVirtualFs(p) && p != subPath {
							return filepath.SkipDir
						}
						if d.IsDir() {
							quickDirs++
							atomic.AddInt64(&scannedDirs, 1)
						} else {
							quickFiles++
							atomic.AddInt64(&scannedFiles, 1)
							if fi, err := d.Info(); err == nil {
								quickSize += fi.Size()
								atomic.AddInt64(&totalBytes, fi.Size())
							}
						}
						return nil
					})
					childNode := &model.FolderNode{
						Name:      filepath.Base(subPath),
						Path:      subPath,
						SizeBytes: quickSize,
						HumanSize: FormatHumanSize(quickSize),
						FileCount: quickFiles,
						DirCount:  quickDirs,
						IsDir:     true,
					}
					node.Children = append(node.Children, childNode)
					node.SizeBytes += quickSize
					node.FileCount += quickFiles
					node.DirCount += quickDirs + 1
					continue
				}

				// 并发拉起子目录扫描
				wg.Add(1)
				go func(dp string) {
					defer wg.Done()
					child, err := walkDir(dp, currentDepth+1)
					resChan <- subResult{child: child, err: err}
				}(subPath)
			}

			wg.Wait()
			close(resChan)

			for res := range resChan {
				if res.err != nil {
					return nil, res.err
				}
				if res.child != nil {
					node.Children = append(node.Children, res.child)
					node.SizeBytes += res.child.SizeBytes
					node.FileCount += res.child.FileCount
					node.DirCount += res.child.DirCount + 1
				}
			}
		}

		// 排序并计算占比
		node.HumanSize = FormatHumanSize(node.SizeBytes)
		if len(node.Children) > 0 {
			sort.Slice(node.Children, func(i, j int) bool {
				return node.Children[i].SizeBytes > node.Children[j].SizeBytes
			})
			for _, c := range node.Children {
				if node.SizeBytes > 0 {
					c.Percent = (float64(c.SizeBytes) / float64(node.SizeBytes)) * 100.0
				}
			}
		}

		return node, nil
	}

	rootNode, err := walkDir(cleanRoot, 0)
	if err != nil {
		return nil, err
	}

	rootNode.Percent = 100.0
	reportProgress(cleanRoot, true)
	return rootNode, nil
}

// Probe 执行局部单目录轻量级即时增量下钻探测 (JIT Shallow Probe)
func (e *FastWalkEngine) Probe(
	ctx context.Context,
	tgt target.IExecutionTargetStrategy,
	req model.ProbeRequest,
) (*model.FolderNode, error) {
	if tgt.TargetID() == "host" {
		duAdapter := NewPosixDuAdapter()
		return duAdapter.Probe(ctx, tgt, req)
	}

	cleanRoot := filepath.Clean(req.SubPath)
	fi, err := os.Lstat(cleanRoot)
	if err != nil {
		return nil, err
	}
	if !fi.IsDir() {
		return &model.FolderNode{
			Name:      filepath.Base(cleanRoot),
			Path:      cleanRoot,
			SizeBytes: fi.Size(),
			HumanSize: FormatHumanSize(fi.Size()),
			FileCount: 1,
			DirCount:  0,
			Percent:   100.0,
			IsDir:     false,
		}, nil
	}

	entries, err := os.ReadDir(cleanRoot)
	if err != nil {
		return nil, err
	}

	node := &model.FolderNode{
		Name:     filepath.Base(cleanRoot),
		Path:     cleanRoot,
		IsDir:    true,
		Children: make([]*model.FolderNode, 0, len(entries)),
	}

	type subResult struct {
		child *model.FolderNode
		err   error
	}

	var subDirs []string
	var directFilesSize int64
	var directFileCount int64

	for _, entry := range entries {
		select {
		case <-ctx.Done():
			return nil, ctx.Err()
		default:
		}

		childPath := filepath.Join(cleanRoot, entry.Name())
		if entry.IsDir() {
			if !IsVirtualFs(childPath) {
				subDirs = append(subDirs, childPath)
			}
		} else {
			directFileCount++
			if info, err := entry.Info(); err == nil {
				sz := info.Size()
				directFilesSize += sz
				node.Children = append(node.Children, &model.FolderNode{
					Name:      entry.Name(),
					Path:      childPath,
					SizeBytes: sz,
					HumanSize: FormatHumanSize(sz),
					FileCount: 1,
					DirCount:  0,
					IsDir:     false,
				})
			}
		}
	}

	node.SizeBytes += directFilesSize
	node.FileCount += directFileCount

	// 并发针对直接子目录进行大小统计 (JIT 并发计算)
	if len(subDirs) > 0 {
		var wg sync.WaitGroup
		resChan := make(chan subResult, len(subDirs))

		for _, sub := range subDirs {
			subPath := sub
			wg.Add(1)
			go func(dp string) {
				defer wg.Done()
				var dirSize int64
				var dirFiles int64
				var dirDirs int64
				_ = filepath.WalkDir(dp, func(p string, d os.DirEntry, err error) error {
					select {
					case <-ctx.Done():
						return ctx.Err()
					default:
					}
					if err != nil {
						return filepath.SkipDir
					}
					if IsVirtualFs(p) && p != dp {
						return filepath.SkipDir
					}
					if d.IsDir() {
						dirDirs++
					} else {
						dirFiles++
						if sfi, err := d.Info(); err == nil {
							dirSize += sfi.Size()
						}
					}
					return nil
				})

				resChan <- subResult{
					child: &model.FolderNode{
						Name:      filepath.Base(dp),
						Path:      dp,
						SizeBytes: dirSize,
						HumanSize: FormatHumanSize(dirSize),
						FileCount: dirFiles,
						DirCount:  dirDirs,
						IsDir:     true,
					},
					err: nil,
				}
			}(subPath)
		}

		wg.Wait()
		close(resChan)

		for res := range resChan {
			if res.err != nil {
				return nil, res.err
			}
			if res.child != nil {
				node.Children = append(node.Children, res.child)
				node.SizeBytes += res.child.SizeBytes
				node.FileCount += res.child.FileCount
				node.DirCount += res.child.DirCount + 1
			}
		}
	}

	node.HumanSize = FormatHumanSize(node.SizeBytes)
	node.Percent = 100.0
	if len(node.Children) > 0 {
		sort.Slice(node.Children, func(i, j int) bool {
			return node.Children[i].SizeBytes > node.Children[j].SizeBytes
		})
		for _, c := range node.Children {
			if node.SizeBytes > 0 {
				c.Percent = (float64(c.SizeBytes) / float64(node.SizeBytes)) * 100.0
			}
		}
	}

	return node, nil
}
