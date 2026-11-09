package model

import "time"

// TaskStatus 扫描任务状态机枚举
type TaskStatus string

const (
	StatusIdle        TaskStatus = "Idle"
	StatusDetecting   TaskStatus = "Detecting"
	StatusScanning    TaskStatus = "Scanning"
	StatusAggregating TaskStatus = "Aggregating"
	StatusCompleted   TaskStatus = "Completed"
	StatusAborting    TaskStatus = "Aborting"
	StatusCancelled   TaskStatus = "Cancelled"
	StatusFailed      TaskStatus = "Failed"
	StatusTimeout     TaskStatus = "Timeout"
)

// TargetOption 目标命名空间描述
type TargetOption struct {
	ID          string   `json:"id"`          // "container" | "host"
	Name        string   `json:"name"`        // 展示名称
	Description string   `json:"description"` // 描述
	DefaultPath string   `json:"defaultPath"` // 默认分析路径
	QuickPaths  []string `json:"quickPaths"`  // 快捷路径推荐
}

// CapabilityReport 系统与运行环境自省能力报告
type CapabilityReport struct {
	IsContainer      bool           `json:"isContainer"`
	IsPrivileged     bool           `json:"isPrivileged"`
	CanAccessHost    bool           `json:"canAccessHost"`
	PlatformOS       string         `json:"platformOs"`
	HostDistro       string         `json:"hostDistro"`
	AvailableTargets []TargetOption `json:"availableTargets"`
	InstalledEngines []string       `json:"installedEngines"`
	DefaultEngine    string         `json:"defaultEngine"`
}

// ScanRequest 扫描请求参数
type ScanRequest struct {
	Target                string  `json:"target"`                // "container" | "host"
	Path                  string  `json:"path"`                  // 扫描起始路径
	OneFileSystem         bool    `json:"oneFileSystem"`         // 是否阻止跨挂载点穿透
	MaxDepth              int     `json:"maxDepth"`              // 分析深度限制（默认 3）
	PruneThresholdPercent float64 `json:"pruneThresholdPercent"` // 剪枝聚合阈值百分比（默认 0.5%）
	Engine                string  `json:"engine"`                // "auto" | "fastwalk" | "gdu" | "posixdu"
}

// ScanProgress 实时扫描进度
type ScanProgress struct {
	TaskID       string     `json:"taskId"`
	Status       TaskStatus `json:"status"`
	Target       string     `json:"target"`
	RootPath     string     `json:"rootPath"`
	ScannedFiles int64      `json:"scannedFiles"`
	ScannedDirs  int64      `json:"scannedDirs"`
	TotalBytes   int64      `json:"totalBytes"`
	CurrentPath  string     `json:"currentPath"`
	ElapsedMs    int64      `json:"elapsedMs"`
	ErrorMessage string     `json:"errorMessage,omitempty"`
}

// FolderNode 统一树状结构节点（适配器归一化模型）
type FolderNode struct {
	Name              string        `json:"name"`
	Path              string        `json:"path"`
	SizeBytes         int64         `json:"sizeBytes"`
	ApparentSizeBytes int64         `json:"apparentSizeBytes"`
	HumanSize         string        `json:"humanSize"`
	FileCount         int64         `json:"fileCount"`
	DirCount          int64         `json:"dirCount"`
	Percent           float64       `json:"percent"`
	IsDir             bool          `json:"isDir"`
	IsSyntheticGroup  bool          `json:"isSyntheticGroup,omitempty"` // 是否为细碎文件合成聚合组
	UnrankedItems     []*FolderNode `json:"unrankedItems,omitempty"`    // 剪枝保留的细小未入榜项明细
	Children          []*FolderNode `json:"children,omitempty"`
}

// ProbeRequest 局部单层增量探测请求
type ProbeRequest struct {
	SubPath       string `json:"subPath"`
	Depth         int    `json:"depth"`
	OneFileSystem bool   `json:"oneFileSystem"`
}

// ProbeResult 局部单层增量探测响应
type ProbeResult struct {
	Node *FolderNode `json:"node"`
}

// UnrankedQueryRequest 查询未入榜细碎项分页请求
type UnrankedQueryRequest struct {
	Path     string `json:"path"`
	Page     int    `json:"page"`
	PageSize int    `json:"pageSize"`
	Keyword  string `json:"keyword"`
}

// UnrankedQueryResponse 查询未入榜细碎项分页响应
type UnrankedQueryResponse struct {
	ParentPath    string        `json:"parentPath"`
	TotalUnranked int           `json:"totalUnranked"`
	TotalBytes    int64         `json:"totalBytes"`
	HumanSize     string        `json:"humanSize"`
	Page          int           `json:"page"`
	PageSize      int           `json:"pageSize"`
	Items         []*FolderNode `json:"items"`
	HasMore       bool          `json:"hasMore"`
}

// TaskRecord 任务全景持久化与缓存结构
type TaskRecord struct {
	TaskID    string        `json:"taskId"`
	Request   ScanRequest   `json:"request"`
	Status    TaskStatus    `json:"status"`
	CreatedAt time.Time     `json:"createdAt"`
	UpdatedAt time.Time     `json:"updatedAt"`
	Progress  ScanProgress  `json:"progress"`
	Result    *FolderNode   `json:"result,omitempty"`
	Engine    string        `json:"engineUsed"`
	Duration  time.Duration `json:"duration"`
}
