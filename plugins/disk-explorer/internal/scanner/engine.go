package scanner

import (
	"context"
	"fmt"
	"strings"

	"lwt/disk-explorer/internal/model"
	"lwt/disk-explorer/internal/target"
)

// IScanEngineStrategy 扫描内核策略接口
type IScanEngineStrategy interface {
	// EngineName 返回引擎唯一名称
	EngineName() string

	// IsAvailable 判定该引擎在指定目标策略下是否可用
	IsAvailable(tgt target.IExecutionTargetStrategy) bool

	// Scan 执行文件夹递归占用分析
	Scan(ctx context.Context, tgt target.IExecutionTargetStrategy, req model.ScanRequest, onProgress func(model.ScanProgress)) (*model.FolderNode, error)

	// Probe 执行单目录即时增量下钻探测
	Probe(ctx context.Context, tgt target.IExecutionTargetStrategy, req model.ProbeRequest) (*model.FolderNode, error)
}

// FormatHumanSize 格式化字节大小为人类可读格式
func FormatHumanSize(bytes int64) string {
	if bytes < 0 {
		return "0 B"
	}
	const unit = 1024
	if bytes < unit {
		return fmt.Sprintf("%d B", bytes)
	}
	div, exp := int64(unit), 0
	for n := bytes / unit; n >= unit; n /= unit {
		div *= unit
		exp++
	}
	units := []string{"KB", "MB", "GB", "TB", "PB"}
	if exp >= len(units) {
		exp = len(units) - 1
	}
	return fmt.Sprintf("%.2f %s", float64(bytes)/float64(div), units[exp])
}

// IsVirtualFs 判断指定路径是否为 Linux 伪虚拟文件系统，避免无效扫描与内核死锁
func IsVirtualFs(path string) bool {
	clean := strings.TrimRight(path, "/")
	if clean == "/proc" || strings.HasPrefix(clean, "/proc/") {
		return true
	}
	if clean == "/sys" || strings.HasPrefix(clean, "/sys/") {
		return true
	}
	if clean == "/dev" || strings.HasPrefix(clean, "/dev/") {
		return true
	}
	if clean == "/run" || strings.HasPrefix(clean, "/run/") {
		return true
	}
	return false
}
