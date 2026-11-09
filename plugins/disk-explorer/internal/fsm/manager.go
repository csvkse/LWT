package fsm

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"lwt/disk-explorer/internal/model"
	"lwt/disk-explorer/internal/scanner"
	"lwt/disk-explorer/internal/target"
)

var (
	ErrTaskNotFound = errors.New("task not found")
	ErrTaskBusy     = errors.New("another scan is currently in progress for this path")
)

// TaskManager 扫描长任务调度管理器
type TaskManager struct {
	mu             sync.RWMutex
	tasks          map[string]*Task
	targetRegistry map[string]target.IExecutionTargetStrategy
	engineRegistry map[string]scanner.IScanEngineStrategy
}

// NewTaskManager 实例化任务管理器
func NewTaskManager() *TaskManager {
	mgr := &TaskManager{
		tasks:          make(map[string]*Task),
		targetRegistry: make(map[string]target.IExecutionTargetStrategy),
		engineRegistry: make(map[string]scanner.IScanEngineStrategy),
	}

	// 注册执行目标策略
	mgr.RegisterTarget(target.NewContainerLocalTarget())
	mgr.RegisterTarget(target.NewHostPiercedTarget())

	// 注册扫描引擎策略
	mgr.RegisterEngine(scanner.NewFastWalkEngine(16))
	mgr.RegisterEngine(scanner.NewGduAdapter())
	mgr.RegisterEngine(scanner.NewPosixDuAdapter())

	return mgr
}

func (m *TaskManager) RegisterTarget(tgt target.IExecutionTargetStrategy) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.targetRegistry[tgt.TargetID()] = tgt
}

func (m *TaskManager) RegisterEngine(eng scanner.IScanEngineStrategy) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.engineRegistry[eng.EngineName()] = eng
}

// StartScan 发起异步文件夹占用扫描
func (m *TaskManager) StartScan(parentCtx context.Context, req model.ScanRequest) (*Task, error) {
	if req.Target == "" {
		req.Target = "container"
	}
	cleanPath := strings.TrimRight(strings.TrimSpace(req.Path), "/")
	if cleanPath == "" {
		cleanPath = "/"
	}
	req.Path = cleanPath

	if req.MaxDepth <= 0 {
		req.MaxDepth = 3
	}
	if req.PruneThresholdPercent <= 0 {
		req.PruneThresholdPercent = 0.5
	}

	// 1. 查找目标策略
	m.mu.RLock()
	tgt, ok := m.targetRegistry[req.Target]
	m.mu.RUnlock()
	if !ok {
		return nil, fmt.Errorf("unknown target strategy: %s", req.Target)
	}

	if !tgt.IsAvailable() {
		return nil, target.ErrHostNotAccessible
	}

	// 2. 创建任务与上下文绑定
	taskID := generateTaskID()
	ctx, cancel := context.WithTimeout(parentCtx, 180*time.Second)
	task := NewTask(taskID, req, cancel)

	m.mu.Lock()
	m.tasks[taskID] = task
	m.mu.Unlock()

	// 3. 异步启动状态机
	go m.executeScan(ctx, task, tgt, req)

	return task, nil
}

func (m *TaskManager) executeScan(
	ctx context.Context,
	task *Task,
	tgt target.IExecutionTargetStrategy,
	req model.ScanRequest,
) {
	// 阶段 1: Detecting
	task.Transition(model.StatusDetecting, "")
	if err := tgt.ValidatePath(ctx, req.Path); err != nil {
		task.Transition(model.StatusFailed, fmt.Sprintf("路径校验失败: %v", err))
		return
	}

	// 阶段 2: 选取扫描引擎
	engine := m.resolveEngine(req, tgt)
	task.EngineUsed = engine.EngineName()

	// 阶段 3: Scanning
	task.Transition(model.StatusScanning, "")

	startTime := time.Now()
	resultNode, err := engine.Scan(ctx, tgt, req, func(p model.ScanProgress) {
		p.ElapsedMs = time.Since(startTime).Milliseconds()
		task.UpdateProgress(p)
	})

	if err != nil {
		if errors.Is(err, context.Canceled) {
			task.Transition(model.StatusCancelled, "扫描已被主动终止")
		} else if errors.Is(err, context.DeadlineExceeded) {
			task.Transition(model.StatusTimeout, "扫描超时 (180秒上限)")
		} else {
			task.Transition(model.StatusFailed, fmt.Sprintf("扫描失败: %v", err))
		}
		return
	}

	// 阶段 4: Aggregating & Pruning
	task.Transition(model.StatusAggregating, "")
	m.PruneTree(resultNode, req.PruneThresholdPercent)

	task.Result = resultNode
	task.Transition(model.StatusCompleted, "")
}

func (m *TaskManager) resolveEngine(req model.ScanRequest, tgt target.IExecutionTargetStrategy) scanner.IScanEngineStrategy {
	m.mu.RLock()
	defer m.mu.RUnlock()

	if req.Engine != "" && req.Engine != "auto" {
		if eng, ok := m.engineRegistry[req.Engine]; ok && eng.IsAvailable(tgt) {
			return eng
		}
	}

	// 智能自适应决策：若目标为 host，首选 posixdu 或 gdu；若在 container，首选 fastwalk
	if req.Target == "host" {
		if gdu, ok := m.engineRegistry["gdu"]; ok && gdu.IsAvailable(tgt) {
			return gdu
		}
		return m.engineRegistry["posixdu"]
	}

	// container 目标首选高并发 fastwalk
	return m.engineRegistry["fastwalk"]
}

// PruneTree 智能树节点剪枝与聚合（将占比过小的节点折叠为合成组）
func (m *TaskManager) PruneTree(node *model.FolderNode, thresholdPercent float64) {
	if node == nil || len(node.Children) == 0 {
		return
	}

	if thresholdPercent <= 0 {
		thresholdPercent = 0.5
	}

	kept := make([]*model.FolderNode, 0, len(node.Children))
	unranked := make([]*model.FolderNode, 0)
	var prunedSize int64
	var prunedFiles int64
	var prunedDirs int64
	var prunedCount int

	for _, child := range node.Children {
		// 递归处理子节点
		m.PruneTree(child, thresholdPercent)

		// 检查自身占比
		if child.Percent < thresholdPercent {
			prunedSize += child.SizeBytes
			prunedFiles += child.FileCount
			prunedDirs += child.DirCount
			prunedCount++
			unranked = append(unranked, child)
		} else {
			kept = append(kept, child)
		}
	}

	// 如果有细碎项被折叠，生成合成组条目并保留未入榜明细
	if prunedCount > 0 {
		syntheticPercent := 0.0
		if node.SizeBytes > 0 {
			syntheticPercent = (float64(prunedSize) / float64(node.SizeBytes)) * 100.0
		}
		syntheticGroup := &model.FolderNode{
			Name:             fmt.Sprintf("[其余 %d 项微小内容]", prunedCount),
			Path:             filepath.Join(node.Path, "__others__"),
			SizeBytes:        prunedSize,
			HumanSize:        scanner.FormatHumanSize(prunedSize),
			FileCount:        prunedFiles,
			DirCount:         prunedDirs,
			Percent:          syntheticPercent,
			IsDir:            true,
			IsSyntheticGroup: true,
			UnrankedItems:    unranked,
		}
		kept = append(kept, syntheticGroup)
		node.UnrankedItems = unranked
	}

	node.Children = kept
}

// ProbeSubPath 针对指定已完成任务的子目录执行即时单层探测并动态嫁接
func (m *TaskManager) ProbeSubPath(ctx context.Context, taskID string, req model.ProbeRequest) (*model.FolderNode, error) {
	m.mu.RLock()
	task, ok := m.tasks[taskID]
	m.mu.RUnlock()
	if !ok {
		return nil, ErrTaskNotFound
	}

	cleanSubPath := strings.TrimRight(strings.TrimSpace(req.SubPath), "/")
	if cleanSubPath == "" {
		cleanSubPath = "/"
	}
	req.SubPath = cleanSubPath
	if req.Depth <= 0 {
		req.Depth = 1
	}

	m.mu.RLock()
	tgt, ok := m.targetRegistry[task.Request.Target]
	m.mu.RUnlock()
	if !ok {
		return nil, fmt.Errorf("target strategy not found: %s", task.Request.Target)
	}

	engine := m.resolveEngine(task.Request, tgt)
	probedNode, err := engine.Probe(ctx, tgt, req)
	if err != nil {
		return nil, err
	}

	// 执行轻量级剪枝
	m.PruneTree(probedNode, task.Request.PruneThresholdPercent)

	// 动态树嫁接 (In-Memory Tree Grafting)
	task.mu.Lock()
	if task.Result != nil {
		graftNode(task.Result, cleanSubPath, probedNode)
	}
	task.mu.Unlock()

	return probedNode, nil
}

// graftNode 深度优先搜索目标节点并将新探测的子集嫁接至原树中
func graftNode(root *model.FolderNode, targetPath string, newProbed *model.FolderNode) bool {
	if root == nil || newProbed == nil {
		return false
	}
	if root.Path == targetPath {
		root.Children = newProbed.Children
		root.UnrankedItems = newProbed.UnrankedItems
		root.FileCount = newProbed.FileCount
		root.DirCount = newProbed.DirCount
		if newProbed.SizeBytes > 0 {
			root.SizeBytes = newProbed.SizeBytes
			root.HumanSize = newProbed.HumanSize
		}
		return true
	}
	for _, child := range root.Children {
		if strings.HasPrefix(targetPath, child.Path) || child.Path == targetPath {
			if graftNode(child, targetPath, newProbed) {
				return true
			}
		}
	}
	return false
}

// GetUnrankedItems 分页获取某个父目录或聚合组下的未入榜细微项
func (m *TaskManager) GetUnrankedItems(taskID string, query model.UnrankedQueryRequest) (*model.UnrankedQueryResponse, error) {
	m.mu.RLock()
	task, ok := m.tasks[taskID]
	m.mu.RUnlock()
	if !ok {
		return nil, ErrTaskNotFound
	}

	task.mu.RLock()
	root := task.Result
	task.mu.RUnlock()
	if root == nil {
		return nil, errors.New("scan result not ready")
	}

	cleanPath := strings.TrimRight(strings.TrimSpace(query.Path), "/")
	if cleanPath == "" {
		cleanPath = "/"
	}

	// 查找目标节点
	targetNode := findNodeByPath(root, cleanPath)
	if targetNode == nil {
		// 可能是以 __others__ 结尾的虚拟路径
		if strings.HasSuffix(cleanPath, "/__others__") {
			parentPath := strings.TrimSuffix(cleanPath, "/__others__")
			targetNode = findNodeByPath(root, parentPath)
		}
	}

	if targetNode == nil {
		return nil, fmt.Errorf("path node not found: %s", cleanPath)
	}

	items := targetNode.UnrankedItems
	if len(items) == 0 {
		// 如果合成组本身存在于子级中
		for _, c := range targetNode.Children {
			if c.IsSyntheticGroup && len(c.UnrankedItems) > 0 {
				items = c.UnrankedItems
				break
			}
		}
	}

	// 过滤关键字
	keyword := strings.TrimSpace(strings.ToLower(query.Keyword))
	filtered := make([]*model.FolderNode, 0, len(items))
	var totalBytes int64
	for _, it := range items {
		if keyword != "" && !strings.Contains(strings.ToLower(it.Name), keyword) && !strings.Contains(strings.ToLower(it.Path), keyword) {
			continue
		}
		filtered = append(filtered, it)
		totalBytes += it.SizeBytes
	}

	// 排序
	sort.Slice(filtered, func(i, j int) bool {
		return filtered[i].SizeBytes > filtered[j].SizeBytes
	})

	page := query.Page
	if page <= 0 {
		page = 1
	}
	pageSize := query.PageSize
	if pageSize <= 0 {
		pageSize = 50
	}

	start := (page - 1) * pageSize
	if start > len(filtered) {
		start = len(filtered)
	}
	end := start + pageSize
	if end > len(filtered) {
		end = len(filtered)
	}

	pageItems := filtered[start:end]
	hasMore := end < len(filtered)

	return &model.UnrankedQueryResponse{
		ParentPath:    targetNode.Path,
		TotalUnranked: len(filtered),
		TotalBytes:    totalBytes,
		HumanSize:     scanner.FormatHumanSize(totalBytes),
		Page:          page,
		PageSize:      pageSize,
		Items:         pageItems,
		HasMore:       hasMore,
	}, nil
}

func findNodeByPath(root *model.FolderNode, targetPath string) *model.FolderNode {
	if root == nil {
		return nil
	}
	if root.Path == targetPath {
		return root
	}
	for _, child := range root.Children {
		if child.Path == targetPath {
			return child
		}
		if strings.HasPrefix(targetPath, child.Path+"/") {
			if found := findNodeByPath(child, targetPath); found != nil {
				return found
			}
		}
	}
	return nil
}

// AbortScan 终止指定任务
func (m *TaskManager) AbortScan(taskID string) error {
	m.mu.RLock()
	task, ok := m.tasks[taskID]
	m.mu.RUnlock()
	if !ok {
		return ErrTaskNotFound
	}

	task.Cancel()
	return nil
}

// GetTask 获取任务实体
func (m *TaskManager) GetTask(taskID string) (*Task, bool) {
	m.mu.RLock()
	defer m.mu.RUnlock()
	task, ok := m.tasks[taskID]
	return task, ok
}

// ListRecentTasks 获取最近任务列表
func (m *TaskManager) ListRecentTasks(limit int) []model.TaskRecord {
	m.mu.RLock()
	defer m.mu.RUnlock()

	records := make([]model.TaskRecord, 0, len(m.tasks))
	for _, t := range m.tasks {
		records = append(records, t.Snapshot())
	}
	return records
}

func generateTaskID() string {
	b := make([]byte, 4)
	_, _ = rand.Read(b)
	return fmt.Sprintf("scan_%s", hex.EncodeToString(b))
}
