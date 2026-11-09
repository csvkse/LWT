package driver

import (
	"errors"
	"net"
	"os"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
)

// HostNicInfo represents a physical or bridge network interface on the host.
type HostNicInfo struct {
	Name         string `json:"name"`
	HardwareAddr string `json:"hardwareAddr"`
	IsDefault    bool   `json:"isDefault"`
	IsWireless   bool   `json:"isWireless"`
	IsBridge     bool   `json:"isBridge"`
	SpeedMbps    int    `json:"speedMbps"`
	OperState    string `json:"operState"`
	IPv4         string `json:"ipv4"`
}

// HardwareLimitsSpec aggregates fine-grained CPU, disk IO, PID, memory, and network limits.
type HardwareLimitsSpec struct {
	CpuCores         int   `json:"cpuCores"`         // e.g. 1, 2, 4
	CpuAllowancePct  int   `json:"cpuAllowancePct"`  // 10~100 (CFS Bandwidth quota %)
	CpuPriority      int   `json:"cpuPriority"`      // 1~10 (CFS fair share weight)
	DiskGiB          int   `json:"diskGiB"`          // Expand disk capacity (GiB)
	DiskReadMbps     int   `json:"diskReadMbps"`     // 0 = unthrottled
	DiskWriteMbps    int   `json:"diskWriteMbps"`    // 0 = unthrottled
	DiskReadIops     int   `json:"diskReadIops"`     // 0 = unthrottled
	DiskWriteIops    int   `json:"diskWriteIops"`    // 0 = unthrottled
	MaxProcesses     int   `json:"maxProcesses"`     // >= 128 (PIDs limit to prevent fork bombs)
	AllowSwap        *bool  `json:"allowSwap"`        // nil = untouched, false = no swap to protect SSD
	MemoryMiB        int    `json:"memoryMiB"`        // RAM hard limit
	BandwidthMaxMbps int    `json:"bandwidthMaxMbps"` // Network QoS (Mbit)
	Network          string `json:"network,omitempty"` // Switch network (e.g. "incusbr0" or "macvlan:ens2")
}

// ExportOptions specifies container export parameters.
type ExportOptions struct {
	InstanceOnly     bool   `json:"instanceOnly"`
	OptimizedStorage bool   `json:"optimizedStorage"`
	Compression      string `json:"compression"` // "gzip" or ""
}

// ImportRequest defines options for restoring or cloning a container instance.
type ImportRequest struct {
	NewName         string `json:"newName"`
	StoragePool     string `json:"storagePool"`
	ResetMacAddress bool   `json:"resetMacAddress"`
}

// StoragePoolInfo provides details about a host storage pool.
type StoragePoolInfo struct {
	Name       string `json:"name"`
	Driver     string `json:"driver"`
	UsedBytes  int64  `json:"usedBytes"`
	TotalBytes int64  `json:"totalBytes"`
}

// HostNetworkDetector scans physical interfaces on the host operating system.
type HostNetworkDetector struct{}

// DetectPhysicalInterfaces probes the host network stack for available physical NICs.
func (d *HostNetworkDetector) DetectPhysicalInterfaces() []HostNicInfo {
	var result []HostNicInfo

	defaultIface := d.detectDefaultOutboundInterface()

	ifaces, err := net.Interfaces()
	if err != nil {
		return d.fallbackInterfaces()
	}

	for _, ifc := range ifaces {
		name := ifc.Name
		// Skip loopback and common virtual/container pseudodevices
		if ifc.Flags&net.FlagLoopback != 0 || isVirtualInterface(name) {
			continue
		}

		mac := ifc.HardwareAddr.String()
		isDefault := (name == defaultIface)
		isWireless := checkWireless(name)
		isBridge := checkBridge(name)
		operState := "down"
		if ifc.Flags&net.FlagUp != 0 {
			operState = "up"
		}

		speed := readSpeed(name)
		ipv4 := extractIPv4(ifc)

		result = append(result, HostNicInfo{
			Name:         name,
			HardwareAddr: mac,
			IsDefault:    isDefault,
			IsWireless:   isWireless,
			IsBridge:     isBridge,
			SpeedMbps:    speed,
			OperState:    operState,
			IPv4:         ipv4,
		})
	}

	if len(result) == 0 {
		return d.fallbackInterfaces()
	}
	return result
}

func isVirtualInterface(name string) bool {
	lower := strings.ToLower(name)
	prefixes := []string{"lo", "docker", "veth", "incus", "tun", "tap", "dummy", "easytier", "wg", "br-"}
	for _, p := range prefixes {
		if strings.HasPrefix(lower, p) {
			return true
		}
	}
	return false
}

func checkWireless(name string) bool {
	path := filepath.Join("/sys/class/net", name, "wireless")
	_, err := os.Stat(path)
	return err == nil
}

func checkBridge(name string) bool {
	path := filepath.Join("/sys/class/net", name, "bridge")
	_, err := os.Stat(path)
	return err == nil
}

func readSpeed(name string) int {
	path := filepath.Join("/sys/class/net", name, "speed")
	data, err := os.ReadFile(path)
	if err != nil {
		return 1000 // default assume 1Gbps
	}
	v, err := strconv.Atoi(strings.TrimSpace(string(data)))
	if err != nil || v <= 0 {
		return 1000
	}
	return v
}

func extractIPv4(ifc net.Interface) string {
	addrs, err := ifc.Addrs()
	if err != nil {
		return ""
	}
	for _, a := range addrs {
		if ipNet, ok := a.(*net.IPNet); ok && !ipNet.IP.IsLoopback() {
			if ipNet.IP.To4() != nil {
				return ipNet.IP.String()
			}
		}
	}
	return ""
}

func (d *HostNetworkDetector) detectDefaultOutboundInterface() string {
	data, err := os.ReadFile("/proc/net/route")
	if err != nil {
		return ""
	}
	lines := strings.Split(string(data), "\n")
	for _, line := range lines[1:] {
		fields := strings.Fields(line)
		if len(fields) >= 2 && fields[1] == "00000000" { // destination == 0.0.0.0
			return fields[0]
		}
	}
	return ""
}

func (d *HostNetworkDetector) fallbackInterfaces() []HostNicInfo {
	return []HostNicInfo{
		{
			Name:         "eth0",
			HardwareAddr: "52:54:00:12:34:56",
			IsDefault:    true,
			IsWireless:   false,
			IsBridge:     false,
			SpeedMbps:    1000,
			OperState:    "up",
			IPv4:         "10.0.2.15",
		},
	}
}

// DeviceRemapVisitor strips specific host parameters when importing instances.
type DeviceRemapVisitor struct {
	TargetStoragePool string
	ResetMacAddress   bool
}

// Cleanse modifies config and devices to avoid hardware collisions.
func (v *DeviceRemapVisitor) Cleanse(config map[string]any, devices map[string]map[string]any) {
	// 1. Remove host volatile state
	for k := range config {
		if strings.HasPrefix(k, "volatile.") {
			delete(config, k)
		}
	}

	// 2. Remap root storage pool
	if v.TargetStoragePool != "" {
		if root, ok := devices["root"]; ok {
			root["pool"] = v.TargetStoragePool
		}
	}

	// 3. Reset network MAC addresses if requested
	if v.ResetMacAddress {
		for _, dev := range devices {
			if dev["type"] == "nic" {
				delete(dev, "hwaddr")
			}
		}
	}
}

var instanceNamePattern = regexp.MustCompile(`^[a-zA-Z0-9][a-zA-Z0-9-]{0,62}$`)

// Validate checks whether hardware limits are within safe and sane bounds.
func (s HardwareLimitsSpec) Validate() error {
	if s.CpuCores < 0 || s.CpuCores > 128 {
		return errors.New("invalid cpu cores")
	}
	if s.CpuAllowancePct < 0 || s.CpuAllowancePct > 100 {
		return errors.New("invalid cpu allowance percentage")
	}
	if s.CpuPriority < 0 || s.CpuPriority > 10 {
		return errors.New("invalid cpu priority")
	}
	if s.MaxProcesses != 0 && s.MaxProcesses < 128 {
		return errors.New("process limit must be at least 128 to prevent system starvation")
	}
	if s.MemoryMiB != 0 && s.MemoryMiB < 64 {
		return errors.New("memory limit must be at least 64MiB")
	}
	return nil
}
