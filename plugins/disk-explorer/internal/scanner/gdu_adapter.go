package scanner

import (
	"context"
	"encoding/json"
	"os/exec"
	"strings"

	"lwt/disk-explorer/internal/model"
	"lwt/disk-explorer/internal/target"
)

// GduAdapter GDU 极速分析适配器
type GduAdapter struct {
	fallbackEngine IScanEngineStrategy
}

func NewGduAdapter() *GduAdapter {
	return &GduAdapter{
		fallbackEngine: NewPosixDuAdapter(),
	}
}

func (g *GduAdapter) EngineName() string {
	return "gdu"
}

func (g *GduAdapter) IsAvailable(tgt target.IExecutionTargetStrategy) bool {
	// 检查当前环境或目标环境是否存在 gdu
	if _, err := exec.LookPath("gdu"); err == nil {
		return true
	}
	return false
}

// GduItem GDU 输出 JSON 结构定义
type GduItem struct {
	Name      string     `json:"name"`
	Path      string     `json:"path"`
	Size      int64      `json:"size"`
	ItemCount int64      `json:"item_count"`
	IsDir     bool       `json:"is_dir"`
	Files     []*GduItem `json:"files,omitempty"`
}

func (g *GduAdapter) Scan(
	ctx context.Context,
	tgt target.IExecutionTargetStrategy,
	req model.ScanRequest,
	onProgress func(model.ScanProgress),
) (*model.FolderNode, error) {
	if !g.IsAvailable(tgt) {
		// 自动平滑降级
		return g.fallbackEngine.Scan(ctx, tgt, req, onProgress)
	}

	cleanPath := strings.TrimRight(req.Path, "/")
	if cleanPath == "" {
		cleanPath = "/"
	}

	args := []string{"-n", "-a", "-j"}
	if req.OneFileSystem {
		args = append(args, "-x")
	}

	args = append(args, "--ignore-dirs=/proc,/sys,/dev,/run")
	args = append(args, cleanPath)

	cmd := tgt.WrapCommand(ctx, "gdu", args...)
	out, err := cmd.Output()
	if err != nil {
		// 若 gdu 执行异常，自动回退到 posixdu
		return g.fallbackEngine.Scan(ctx, tgt, req, onProgress)
	}

	var rootItem GduItem
	if err := json.Unmarshal(out, &rootItem); err != nil {
		return g.fallbackEngine.Scan(ctx, tgt, req, onProgress)
	}

	// 适配器转换
	return g.convertGduNode(&rootItem, cleanPath), nil
}

func (g *GduAdapter) convertGduNode(item *GduItem, rootPath string) *model.FolderNode {
	if item == nil {
		return nil
	}

	node := &model.FolderNode{
		Name:      item.Name,
		Path:      item.Path,
		SizeBytes: item.Size,
		HumanSize: FormatHumanSize(item.Size),
		FileCount: item.ItemCount,
		IsDir:     item.IsDir,
		Children:  make([]*model.FolderNode, 0, len(item.Files)),
	}
	if node.Path == "" {
		node.Path = rootPath
	}

	for _, child := range item.Files {
		cNode := g.convertGduNode(child, child.Path)
		if cNode != nil {
			if node.SizeBytes > 0 {
				cNode.Percent = (float64(cNode.SizeBytes) / float64(node.SizeBytes)) * 100.0
			}
			node.Children = append(node.Children, cNode)
		}
	}

	node.Percent = 100.0
	return node
}

func (g *GduAdapter) Probe(
	ctx context.Context,
	tgt target.IExecutionTargetStrategy,
	req model.ProbeRequest,
) (*model.FolderNode, error) {
	return g.fallbackEngine.Probe(ctx, tgt, req)
}
