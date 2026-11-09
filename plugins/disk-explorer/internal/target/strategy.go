package target

import (
	"context"
	"errors"
	"os/exec"
)

var (
	ErrHostNotAccessible = errors.New("host namespace is not accessible; privileged container with nsenter required")
	ErrPathNotFound      = errors.New("target path does not exist or cannot be accessed")
	ErrInvalidPath       = errors.New("invalid path format")
)

// IExecutionTargetStrategy 目标命名空间执行策略接口
type IExecutionTargetStrategy interface {
	// TargetID 返回策略标识 ("container" | "host")
	TargetID() string

	// IsAvailable 判定当前运行环境该目标是否可用
	IsAvailable() bool

	// ValidatePath 校验指定路径在目标命名空间中是否有效
	ValidatePath(ctx context.Context, path string) error

	// WrapCommand 在目标命名空间中封装并准备可执行命令
	WrapCommand(ctx context.Context, name string, args ...string) *exec.Cmd
}
