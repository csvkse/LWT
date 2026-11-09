package target

import (
	"context"
	"os"
	"os/exec"
	"strings"
)

// ContainerLocalTarget 当前容器或当前本地命名空间执行策略
type ContainerLocalTarget struct{}

func NewContainerLocalTarget() *ContainerLocalTarget {
	return &ContainerLocalTarget{}
}

func (c *ContainerLocalTarget) TargetID() string {
	return "container"
}

func (c *ContainerLocalTarget) IsAvailable() bool {
	return true
}

func (c *ContainerLocalTarget) ValidatePath(ctx context.Context, path string) error {
	cleanPath := strings.TrimSpace(path)
	if cleanPath == "" {
		return ErrInvalidPath
	}
	fi, err := os.Stat(cleanPath)
	if err != nil {
		return ErrPathNotFound
	}
	if !fi.IsDir() {
		return ErrPathNotFound
	}
	return nil
}

func (c *ContainerLocalTarget) WrapCommand(ctx context.Context, name string, args ...string) *exec.Cmd {
	return exec.CommandContext(ctx, name, args...)
}
