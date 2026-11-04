package main

import (
	"os"
	"runtime"
)

// TunStatus 透明网关与 TUN 虚拟设备环境状态
type TunStatus struct {
	DeviceAvailable   bool   `json:"deviceAvailable"`
	DevicePath        string `json:"devicePath"`
	HasPrivilege      bool   `json:"hasPrivilege"`
	PlatformSupported bool   `json:"platformSupported"`
	InterfaceName     string `json:"interfaceName"`
	RecommendedMode   string `json:"recommendedMode"` // gvisor_netstack
	IsRunning         bool   `json:"isRunning"`
	StatusMessage     string `json:"statusMessage"`
}

// TunConfig 透明网关持久化配置
type TunConfig struct {
	Enabled       bool     `json:"enabled"`
	InterfaceName string   `json:"interfaceName"`
	IPv4Subnet    string   `json:"ipv4Subnet"`
	DNSHijack     bool     `json:"dnsHijack"`
	BypassSubnets []string `json:"bypassSubnets"`
}

// DefaultTunConfig 默认 TUN 配置
func DefaultTunConfig() TunConfig {
	return TunConfig{
		Enabled:       false,
		InterfaceName: "lwt-tun0",
		IPv4Subnet:    "198.18.0.1/16",
		DNSHijack:     true,
		BypassSubnets: []string{
			"10.0.0.0/8",
			"172.16.0.0/12",
			"192.168.0.0/16",
			"127.0.0.0/8",
		},
	}
}

// CheckTunEnvironment 探查宿主容器的 TUN 特权与设备就绪状态
func CheckTunEnvironment() TunStatus {
	devicePath := "/dev/net/tun"
	isLinux := runtime.GOOS == "linux"

	status := TunStatus{
		DevicePath:        devicePath,
		PlatformSupported: isLinux,
		InterfaceName:     "lwt-tun0",
		RecommendedMode:   "gvisor_netstack",
		IsRunning:         false,
	}

	if !isLinux {
		status.DeviceAvailable = false
		status.HasPrivilege = false
		status.StatusMessage = "非 Linux 宿主环境（当前为 " + runtime.GOOS + "），TUN 透明网关仅在 Linux 生产容器下可用"
		return status
	}

	// 1. 检查 /dev/net/tun 是否存在
	if fi, err := os.Stat(devicePath); err == nil {
		status.DeviceAvailable = true
		// 2. 检查是否具备读写权限（CAP_NET_ADMIN / 特权容器）
		if f, err := os.OpenFile(devicePath, os.O_RDWR, 0); err == nil {
			_ = f.Close()
			status.HasPrivilege = true
			status.StatusMessage = "🟢 /dev/net/tun 设备就绪，容器具备 CAP_NET_ADMIN 特权，已满足 gVisor 用户态透明网关运行条件"
		} else {
			status.HasPrivilege = false
			status.StatusMessage = "⚠️ /dev/net/tun 存在但读写被拒绝: " + err.Error() + " (容器可能缺少 --cap-add=NET_ADMIN 或 --privileged)"
		}
		_ = fi
	} else {
		status.DeviceAvailable = false
		status.HasPrivilege = false
		status.StatusMessage = "⚠️ 未检测到 /dev/net/tun 字符设备 (宿主启动 Docker 时需添加 --device=/dev/net/tun)"
	}

	return status
}
