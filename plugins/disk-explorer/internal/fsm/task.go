package fsm

import (
	"context"
	"sync"
	"time"

	"lwt/disk-explorer/internal/model"
)

// Task 表示单个扫描任务的状态机实体
type Task struct {
	mu           sync.RWMutex
	ID           string
	Request      model.ScanRequest
	Status       model.TaskStatus
	CreatedAt    time.Time
	UpdatedAt    time.Time
	Progress     model.ScanProgress
	Result       *model.FolderNode
	EngineUsed   string
	Duration     time.Duration
	cancelFunc   context.CancelFunc
	listeners    map[chan model.ScanProgress]struct{}
	listenersMu  sync.Mutex
	isTerminated bool
}

// NewTask 初始化任务实体（初始状态：Idle）
func NewTask(id string, req model.ScanRequest, cancel context.CancelFunc) *Task {
	now := time.Now()
	t := &Task{
		ID:         id,
		Request:    req,
		Status:     model.StatusIdle,
		CreatedAt:  now,
		UpdatedAt:  now,
		cancelFunc: cancel,
		listeners:  make(map[chan model.ScanProgress]struct{}),
		Progress: model.ScanProgress{
			TaskID:   id,
			Status:   model.StatusIdle,
			Target:   req.Target,
			RootPath: req.Path,
		},
	}
	return t
}

// Transition 状态机流转守卫
func (t *Task) Transition(newStatus model.TaskStatus, errMsg string) bool {
	t.mu.Lock()

	// 终止状态不可逆转
	if t.isTerminated {
		t.mu.Unlock()
		return false
	}

	t.Status = newStatus
	t.UpdatedAt = time.Now()
	t.Progress.Status = newStatus
	if errMsg != "" {
		t.Progress.ErrorMessage = errMsg
	}

	if newStatus == model.StatusCompleted ||
		newStatus == model.StatusCancelled ||
		newStatus == model.StatusFailed ||
		newStatus == model.StatusTimeout {
		t.isTerminated = true
		t.Duration = t.UpdatedAt.Sub(t.CreatedAt)
	}

	prog := t.Progress
	t.mu.Unlock()

	t.broadcast(prog)
	return true
}

// UpdateProgress 更新扫描进度并广播给订阅者
func (t *Task) UpdateProgress(p model.ScanProgress) {
	t.mu.Lock()
	p.TaskID = t.ID
	p.Target = t.Request.Target
	p.RootPath = t.Request.Path
	p.ElapsedMs = time.Since(t.CreatedAt).Milliseconds()
	t.Progress = p
	t.UpdatedAt = time.Now()
	t.mu.Unlock()

	t.broadcast(p)
}

// Subscribe 注册进度监听器通道 (用于 SSE 流)
func (t *Task) Subscribe() (<-chan model.ScanProgress, func()) {
	t.mu.RLock()
	current := t.Progress
	t.mu.RUnlock()

	t.listenersMu.Lock()
	defer t.listenersMu.Unlock()

	ch := make(chan model.ScanProgress, 32)
	t.listeners[ch] = struct{}{}
	ch <- current

	unsubscribe := func() {
		t.listenersMu.Lock()
		defer t.listenersMu.Unlock()
		if _, ok := t.listeners[ch]; ok {
			delete(t.listeners, ch)
			close(ch)
		}
	}

	return ch, unsubscribe
}

func (t *Task) broadcast(p model.ScanProgress) {
	t.listenersMu.Lock()
	defer t.listenersMu.Unlock()

	for ch := range t.listeners {
		select {
		case ch <- p:
		default:
			// 慢速消费者丢弃旧进度，防止阻塞
		}
	}
}

// Cancel 触发任务取消
func (t *Task) Cancel() {
	if t.cancelFunc != nil {
		t.cancelFunc()
	}
	t.Transition(model.StatusCancelled, "扫描已被用户主动终止")
}

// Snapshot 获取任务当前安全快照
func (t *Task) Snapshot() model.TaskRecord {
	t.mu.RLock()
	defer t.mu.RUnlock()

	return model.TaskRecord{
		TaskID:    t.ID,
		Request:   t.Request,
		Status:    t.Status,
		CreatedAt: t.CreatedAt,
		UpdatedAt: t.UpdatedAt,
		Progress:  t.Progress,
		Result:    t.Result,
		Engine:    t.EngineUsed,
		Duration:  t.Duration,
	}
}
