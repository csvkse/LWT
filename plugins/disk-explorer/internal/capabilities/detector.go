package capabilities

import (
	"bufio"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"sync"
	"time"

	"lwt/disk-explorer/internal/model"
)

// Detector 环境自省与特权穿透探测器
type Detector struct {
	mu           sync.RWMutex
	cached       *model.CapabilityReport
	lastDetected time.Time
	ttl          time.Duration
}

// NewDetector 构造探测器实例（默认缓存 10 秒）
func NewDetector() *Detector {
	return &Detector{
		ttl: 10 * time.Second,
	}
}

// Detect 获取当前运行环境自省能力报告
func (d *Detector) Detect() model.CapabilityReport {
	d.mu.RLock()
	if d.cached != nil && time.Since(d.lastDetected) < d.ttl {
		report := *d.cached
		d.mu.RUnlock()
		return report
	}
	d.mu.RUnlock()

	d.mu.Lock()
	defer d.mu.Unlock()

	if d.cached != nil && time.Since(d.lastDetected) < d.ttl {
		return *d.cached
	}

	report := d.performDetection()
	d.cached = &report
	d.lastDetected = time.Now()
	return report
}

func (d *Detector) performDetection() model.CapabilityReport {
	isLinux := runtime.GOOS == "linux"
	platformOS := runtime.GOOS

	isContainer := false
	isPrivileged := false
	canAccessHost := false
	hostDistro := ""

	if isLinux {
		// 1. 容器环境判定
		if _, err := os.Stat("/.dockerenv"); err == nil {
			isContainer = true
		} else if checkCgroupForContainer() {
			isContainer = true
		}

		// 2. 特权容器与宿主 mount 命名空间穿透判定
		nsenterPath := findNsenterPath()
		hasNsenter := nsenterPath != ""
		hasHostPid1Ns := false
		if _, err := os.Stat("/proc/1/ns/mnt"); err == nil {
			hasHostPid1Ns = true
		}

		if isContainer {
			// 在容器内：必须同时具备宿主命名空间句柄与 nsenter 二进制
			if hasHostPid1Ns && hasNsenter {
				isPrivileged = true
				canAccessHost = true
			}
		} else {
			// 物理裸机或虚拟机本身就是宿主
			canAccessHost = true
			isPrivileged = true
		}

		// 读取发行版信息
		hostDistro = readDistroName()
	} else if runtime.GOOS == "windows" {
		hostDistro = "Windows System"
		canAccessHost = false
		isContainer = false
		isPrivileged = true
	}

	// 3. 检查扫描引擎可用性
	installedEngines := []string{"fastwalk"}
	if _, err := exec.LookPath("gdu"); err == nil {
		installedEngines = append(installedEngines, "gdu")
	}
	if _, err := exec.LookPath("du"); err == nil {
		installedEngines = append(installedEngines, "posixdu")
	}

	defaultEngine := "fastwalk"
	if contains(installedEngines, "gdu") {
		defaultEngine = "gdu"
	}

	// 4. 构建目标选项列表
	availableTargets := make([]model.TargetOption, 0, 2)

	// 当前系统环境（始终支持）
	if isContainer {
		availableTargets = append(availableTargets, model.TargetOption{
			ID:          "container",
			Name:        "当前系统 (容器环境)",
			Description: "分析当前容器沙箱内部文件系统空间占用",
			DefaultPath: "/app",
			QuickPaths:  []string{"/app", "/var/log", "/tmp", "/"},
		})
	} else {
		availableTargets = append(availableTargets, model.TargetOption{
			ID:          "container",
			Name:        "当前操作系统",
			Description: "分析当前操作系统文件系统空间占用",
			DefaultPath: "/",
			QuickPaths:  []string{"/", "/var/log", "/home", "/tmp"},
		})
	}

	// 宿主主机目标：仅在识别到特权穿透能力时才向用户暴露！
	if canAccessHost && isContainer {
		availableTargets = append(availableTargets, model.TargetOption{
			ID:          "host",
			Name:        "宿主主机 (Host OS)",
			Description: "穿透进入物理宿主机 PID 1 挂载命名空间进行磁盘分析",
			DefaultPath: "/",
			QuickPaths:  []string{"/", "/var/lib/docker", "/var/log", "/home", "/etc"},
		})
	}

	return model.CapabilityReport{
		IsContainer:      isContainer,
		IsPrivileged:     isPrivileged,
		CanAccessHost:    canAccessHost,
		PlatformOS:       platformOS,
		HostDistro:       hostDistro,
		AvailableTargets: availableTargets,
		InstalledEngines: installedEngines,
		DefaultEngine:    defaultEngine,
	}
}

func checkCgroupForContainer() bool {
	f, err := os.Open("/proc/1/cgroup")
	if err != nil {
		return false
	}
	defer f.Close()

	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := strings.ToLower(scanner.Text())
		if strings.Contains(line, "docker") ||
			strings.Contains(line, "containerd") ||
			strings.Contains(line, "kubepods") ||
			strings.Contains(line, "lxc") {
			return true
		}
	}
	return false
}

func findNsenterPath() string {
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

func readDistroName() string {
	f, err := os.Open("/etc/os-release")
	if err != nil {
		return ""
	}
	defer f.Close()

	scanner := bufio.NewScanner(f)
	for scanner.Scan() {
		line := scanner.Text()
		if strings.HasPrefix(line, "PRETTY_NAME=") {
			val := strings.TrimPrefix(line, "PRETTY_NAME=")
			return strings.Trim(val, `"'`)
		}
	}
	return ""
}

func contains(arr []string, item string) bool {
	for _, s := range arr {
		if s == item {
			return true
		}
	}
	return false
}
