package driver

import (
	"archive/tar"
	"compress/gzip"
	"context"
	"errors"
	"fmt"
	"hash/fnv"
	"io"
	"sync"
	"time"

	"lwt/lxc-manager/internal/incus"
)

type MockInstance struct {
	Spec   incus.InstanceSpec
	JobID  string
	State  string // "Running", "Stopped"
}

type MockDriver struct {
	mu        sync.RWMutex
	instances map[string]*MockInstance
	images    []incus.ImageInfo
	pools     []incus.PoolInfo
	networks  []incus.NetworkInfo
}

func NewMockDriver() *MockDriver {
	return &MockDriver{
		instances: make(map[string]*MockInstance),
		images: []incus.ImageInfo{
			{Fingerprint: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678", Architecture: "x86_64", Description: "Alpine Linux 3.20 (amd64)"},
			{Fingerprint: "d2e3f4a5b6c7d8e9f0123456789abcdef0123456", Architecture: "x86_64", Description: "Debian 12 Bookworm (amd64)"},
			{Fingerprint: "e3f4a5b6c7d8e9f0123456789abcdef012345678", Architecture: "x86_64", Description: "Ubuntu 24.04 LTS (amd64)"},
			{Fingerprint: "f4a5b6c7d8e9f0123456789abcdef0123456789a", Architecture: "x86_64", Description: "Arch Linux (amd64)"},
		},
		pools: []incus.PoolInfo{
			{Name: "default", Driver: "dir", Status: "Created"},
		},
		networks: []incus.NetworkInfo{
			{Name: "incusbr0"},
		},
	}
}

func (d *MockDriver) Name() string {
	return "mock-sandbox"
}

func (d *MockDriver) Capabilities(ctx context.Context) EngineCapabilities {
	return EngineCapabilities{
		SupportsHotSnapshot:        true,
		SupportsWebSocketTerminal:  true,
		SupportsStorageProjectQuota: true,
		SupportsNativePortForward:  true,
	}
}

func (d *MockDriver) Probe(ctx context.Context) incus.Capability {
	return incus.Capability{
		Status:  incus.Ready,
		Version: "mock-v1.0 (Sandbox Emulation)",
	}
}

func (d *MockDriver) Discover(ctx context.Context) (incus.Discovery, error) {
	d.mu.RLock()
	defer d.mu.RUnlock()

	var instances []incus.InstanceInfo
	for name, inst := range d.instances {
		info := incus.InstanceInfo{
			Name:        name,
			Status:      inst.State,
			CPU:         fmt.Sprintf("%d", inst.Spec.CPU),
			Memory:      fmt.Sprintf("%dMiB", inst.Spec.MemoryMiB),
			Disk:        fmt.Sprintf("%dGiB", inst.Spec.DiskGiB),
			StoragePool: inst.Spec.StoragePool,
			NetworkName: inst.Spec.Network,
		}
		if inst.State == "Running" {
			h := fnv.New32a()
			h.Write([]byte(name))
			suffix := 10 + int(h.Sum32()%230)
			info.IPv4 = fmt.Sprintf("10.0.3.%d", suffix)
			info.IPv6 = fmt.Sprintf("fd42:1d90:d9e1:2d84::%x", suffix)
			info.MAC = fmt.Sprintf("10:66:6a:00:00:%02x", suffix)
		}
		instances = append(instances, info)
	}

	return incus.Discovery{
		Instances: instances,
		Images:    d.images,
		Pools:     d.pools,
		Networks:  d.networks,
	}, nil
}

func (d *MockDriver) SampleResources(ctx context.Context, pool string) (incus.ResourceSample, error) {
	return incus.ResourceSample{
		CPUThreads:       4,
		MemoryTotalBytes: 8192 * 1024 * 1024,
		MemoryUsedBytes:  2048 * 1024 * 1024,
		PoolTotalBytes:   100 * 1024 * 1024 * 1024,
		PoolUsedBytes:    15 * 1024 * 1024 * 1024,
	}, nil
}

func (d *MockDriver) CreateOwned(ctx context.Context, s incus.InstanceSpec, jobID string) (string, error) {
	d.mu.Lock()
	defer d.mu.Unlock()

	d.instances[s.Name] = &MockInstance{
		Spec:  s,
		JobID: jobID,
		State: "Stopped",
	}

	return fmt.Sprintf("/1.0/operations/mock-create-%s", s.Name), nil
}

func (d *MockDriver) ChangeState(ctx context.Context, name, action string) (string, error) {
	d.mu.Lock()
	defer d.mu.Unlock()

	inst, exists := d.instances[name]
	if !exists {
		return "", fmt.Errorf("instance not found: %s", name)
	}

	switch action {
	case "start":
		inst.State = "Running"
	case "stop":
		inst.State = "Stopped"
	case "restart":
		inst.State = "Running"
	}

	return fmt.Sprintf("/1.0/operations/mock-action-%s-%s", name, action), nil
}

func (d *MockDriver) InstanceState(ctx context.Context, name string) (string, error) {
	d.mu.RLock()
	defer d.mu.RUnlock()

	inst, exists := d.instances[name]
	if !exists {
		return "Stopped", nil
	}
	return inst.State, nil
}

func (d *MockDriver) VerifyCreated(ctx context.Context, name, jobID string, s incus.InstanceSpec) (bool, error) {
	d.mu.Lock()
	defer d.mu.Unlock()

	inst, exists := d.instances[s.Name]
	if !exists {
		// Restore from persisted job metadata
		inst = &MockInstance{
			Spec:  s,
			JobID: jobID,
			State: "Stopped",
		}
		d.instances[s.Name] = inst
		return true, nil
	}
	return inst.JobID == jobID, nil
}

func (d *MockDriver) IsRunning(ctx context.Context, name string) (bool, error) {
	d.mu.RLock()
	defer d.mu.RUnlock()

	inst, exists := d.instances[name]
	if !exists {
		return false, nil
	}
	return inst.State == "Running", nil
}

func (d *MockDriver) Operation(ctx context.Context, op string) (string, error) {
	return "Success", nil
}

func (d *MockDriver) GetHostInterfaces(ctx context.Context) ([]HostNicInfo, error) {
	detector := &HostNetworkDetector{}
	return detector.DetectPhysicalInterfaces(), nil
}

func (d *MockDriver) UpdateHardwareLimits(ctx context.Context, name string, spec HardwareLimitsSpec) error {
	if err := spec.Validate(); err != nil {
		return err
	}
	d.mu.Lock()
	defer d.mu.Unlock()

	inst, exists := d.instances[name]
	if !exists {
		return errors.New("instance not found")
	}

	if spec.CpuCores > 0 {
		inst.Spec.CPU = spec.CpuCores
	}
	if spec.MemoryMiB >= 64 {
		inst.Spec.MemoryMiB = spec.MemoryMiB
	}
	if spec.DiskGiB > 0 {
		inst.Spec.DiskGiB = spec.DiskGiB
	}
	if spec.Network != "" {
		inst.Spec.Network = spec.Network
	}
	return nil
}

func (d *MockDriver) Exec(ctx context.Context, name string, cmd []string) (string, string, int, error) {
	return "", "", 0, nil
}

func (d *MockDriver) DeleteInstance(ctx context.Context, name string, force bool) error {
	d.mu.Lock()
	defer d.mu.Unlock()

	inst, exists := d.instances[name]
	if !exists {
		return errors.New("instance not found")
	}
	if inst.State == "Running" && !force {
		return errors.New("instance is running; stop it first or specify force=true")
	}
	delete(d.instances, name)
	return nil
}

func (d *MockDriver) ExportInstance(ctx context.Context, name string, w io.Writer, opts ExportOptions) error {
	d.mu.RLock()
	_, exists := d.instances[name]
	d.mu.RUnlock()
	if !exists {
		return errors.New("instance not found")
	}

	// Write mock gzip tar archive
	gw := gzip.NewWriter(w)
	defer gw.Close()
	tw := tar.NewWriter(gw)
	defer tw.Close()

	payload := []byte(fmt.Sprintf("index_format: 1\ninstance: %s\n", name))
	hdr := &tar.Header{
		Name:    "backup/index.yaml",
		Mode:    0644,
		Size:    int64(len(payload)),
		ModTime: time.Now(),
	}
	if err := tw.WriteHeader(hdr); err != nil {
		return err
	}
	_, err := tw.Write(payload)
	return err
}

func (d *MockDriver) ImportInstance(ctx context.Context, req ImportRequest, r io.Reader) (*incus.InstanceInfo, error) {
	targetName := req.NewName
	if targetName == "" {
		targetName = "imported-mock-01"
	}
	targetPool := req.StoragePool
	if targetPool == "" {
		targetPool = "default"
	}

	d.mu.Lock()
	defer d.mu.Unlock()

	d.instances[targetName] = &MockInstance{
		Spec: incus.InstanceSpec{
			Name:             targetName,
			ImageFingerprint: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
			CPU:              1,
			MemoryMiB:        512,
			DiskGiB:          8,
			StoragePool:      targetPool,
			Network:          "incusbr0",
		},
		JobID: "mock_import_job",
		State: "Stopped",
	}

	return &incus.InstanceInfo{
		Name:        targetName,
		Status:      "Stopped",
		CPU:         "1",
		Memory:      "512MiB",
		Disk:        "8GiB",
		StoragePool: targetPool,
		NetworkName: "incusbr0",
		IPv4:        "",
	}, nil
}

func (d *MockDriver) GetStoragePools(ctx context.Context) ([]StoragePoolInfo, error) {
	return []StoragePoolInfo{
		{
			Name:       "default",
			Driver:     "dir",
			TotalBytes: 100 * 1024 * 1024 * 1024,
			UsedBytes:  12 * 1024 * 1024 * 1024,
		},
	}, nil
}
