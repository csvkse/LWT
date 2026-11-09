package target

import (
	"context"
	"os"
	"os/exec"
	"runtime"
	"strings"
)

// HostPiercedTarget 特权容器 nsenter 宿主穿透执行策略
type HostPiercedTarget struct {
	nsenterPath string
	inContainer bool
}

func NewHostPiercedTarget() *HostPiercedTarget {
	nsenter := findNsenter()
	inCont := detectContainer()
	return &HostPiercedTarget{
		nsenterPath: nsenter,
		inContainer: inCont,
	}
}

func (h *HostPiercedTarget) TargetID() string {
	return "host"
}

func (h *HostPiercedTarget) IsAvailable() bool {
	if runtime.GOOS != "linux" {
		return false
	}
	// 若本身就在裸机/虚机宿主，天然可用
	if !h.inContainer {
		return true
	}
	// 若在容器内，必须有 nsenter 且能看到 /proc/1/ns/mnt
	if h.nsenterPath == "" {
		return false
	}
	if _, err := os.Stat("/proc/1/ns/mnt"); err != nil {
		return false
	}
	return true
}

func (h *HostPiercedTarget) ValidatePath(ctx context.Context, path string) error {
	cleanPath := strings.TrimSpace(path)
	if cleanPath == "" {
		return ErrInvalidPath
	}
	if !h.IsAvailable() {
		return ErrHostNotAccessible
	}

	// 若不在容器内，直接检查本地
	if !h.inContainer {
		fi, err := os.Stat(cleanPath)
		if err != nil || !fi.IsDir() {
			return ErrPathNotFound
		}
		return nil
	}

	// 在特权容器内：使用 nsenter 穿透探测宿主路径
	cmd := h.WrapCommand(ctx, "test", "-d", cleanPath)
	if err := cmd.Run(); err != nil {
		return ErrPathNotFound
	}
	return nil
}

func (h *HostPiercedTarget) WrapCommand(ctx context.Context, name string, args ...string) *exec.Cmd {
	if !h.inContainer || h.nsenterPath == "" {
		return exec.CommandContext(ctx, name, args...)
	}

	// 穿透参数：-t 1 -m (mount) -u (uts) -i (ipc) -n (net)
	nsenterArgs := []string{"-t", "1", "-m", "-u", "-i", "-n", "--", name}
	nsenterArgs = append(nsenterArgs, args...)
	return exec.CommandContext(ctx, h.nsenterPath, nsenterArgs...)
}

func findNsenter() string {
	for _, p := range []string{"/usr/bin/nsenter", "/bin/nsenter"} {
		if fi, err := os.Stat(p); err == nil && !fi.IsDir() {
			return p
		}
	}
	if path, err := exec.LookPath("nsenter"); err == nil {
		return path
	}
	return ""
}

func detectContainer() bool {
	if _, err := os.Stat("/.dockerenv"); err == nil {
		return true
	}
	data, err := os.ReadFile("/proc/1/cgroup")
	if err != nil {
		return false
	}
	content := strings.ToLower(string(data))
	return strings.Contains(content, "docker") ||
		strings.Contains(content, "containerd") ||
		strings.Contains(content, "kubepods") ||
		strings.Contains(content, "lxc")
}
