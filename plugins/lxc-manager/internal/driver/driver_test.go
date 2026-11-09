package driver

import (
	"bytes"
	"context"
	"testing"

	"lwt/lxc-manager/internal/incus"
)

func TestMockDriverLifecycle(t *testing.T) {
	ctx := context.Background()
	driver := NewMockDriver()

	// 1. Probe & Capabilities
	cap := driver.Capabilities(ctx)
	if !cap.SupportsWebSocketTerminal {
		t.Errorf("expected terminal support")
	}
	probe := driver.Probe(ctx)
	if probe.Status != incus.Ready {
		t.Errorf("expected probe ready")
	}

	// 2. Discover
	disc, err := driver.Discover(ctx)
	if err != nil {
		t.Fatalf("discover failed: %v", err)
	}
	if len(disc.Images) < 4 {
		t.Errorf("expected >= 4 images in discovery, got %d", len(disc.Images))
	}

	// 3. Create
	spec := incus.InstanceSpec{
		Name:             "test-alpine",
		ImageFingerprint: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
		CPU:              1,
		MemoryMiB:        512,
		DiskGiB:          5,
		StoragePool:      "default",
		Network:          "incusbr0",
	}
	op, err := driver.CreateOwned(ctx, spec, "0123456789abcdef0123456789abcdef")
	if err != nil {
		t.Fatalf("create failed: %v", err)
	}
	if op == "" {
		t.Errorf("expected non-empty operation")
	}

	// 4. Verify & State
	verified, err := driver.VerifyCreated(ctx, "test-alpine", "0123456789abcdef0123456789abcdef", spec)
	if err != nil || !verified {
		t.Errorf("expected verified=true")
	}

	state, err := driver.InstanceState(ctx, "test-alpine")
	if err != nil || state != "Stopped" {
		t.Errorf("expected Stopped, got %s", state)
	}

	// 5. Start
	_, err = driver.ChangeState(ctx, "test-alpine", "start")
	if err != nil {
		t.Fatalf("start failed: %v", err)
	}
	running, _ := driver.IsRunning(ctx, "test-alpine")
	if !running {
		t.Errorf("expected running=true")
	}

	// 6. Stop
	_, err = driver.ChangeState(ctx, "test-alpine", "stop")
	if err != nil {
		t.Fatalf("stop failed: %v", err)
	}
	running, _ = driver.IsRunning(ctx, "test-alpine")
	if running {
		t.Errorf("expected running=false")
	}
}

func TestMockDriverMultiInstanceIPUniqueness(t *testing.T) {
	ctx := context.Background()
	driver := NewMockDriver()

	spec1 := incus.InstanceSpec{Name: "app-worker-1", CPU: 1, MemoryMiB: 512, DiskGiB: 5, StoragePool: "default", Network: "incusbr0", ImageFingerprint: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"}
	spec2 := incus.InstanceSpec{Name: "app-worker-2", CPU: 2, MemoryMiB: 1024, DiskGiB: 10, StoragePool: "default", Network: "incusbr0", ImageFingerprint: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"}

	_, _ = driver.CreateOwned(ctx, spec1, "0123456789abcdef0123456789abcdef")
	_, _ = driver.CreateOwned(ctx, spec2, "0123456789abcdef0123456789abcdef")

	_, _ = driver.ChangeState(ctx, "app-worker-1", "start")
	_, _ = driver.ChangeState(ctx, "app-worker-2", "start")

	disc, err := driver.Discover(ctx)
	if err != nil {
		t.Fatalf("discover failed: %v", err)
	}
	if len(disc.Instances) != 2 {
		t.Fatalf("expected 2 instances, got %d", len(disc.Instances))
	}

	ipMap := make(map[string]string)
	for _, inst := range disc.Instances {
		if inst.IPv4 == "" {
			t.Errorf("expected non-empty IPv4 for running instance %s", inst.Name)
		}
		if other, exists := ipMap[inst.IPv4]; exists {
			t.Fatalf("GATE VIOLATION: duplicate IP detected! %s and %s share IP %s", other, inst.Name, inst.IPv4)
		}
		ipMap[inst.IPv4] = inst.Name
	}
}

func TestHardwareLimitsSpec_Validation(t *testing.T) {
	// 1. Valid spec
	s1 := HardwareLimitsSpec{
		CpuCores:        2,
		CpuAllowancePct: 50,
		CpuPriority:     5,
		MaxProcesses:    256,
		MemoryMiB:       512,
	}
	if err := s1.Validate(); err != nil {
		t.Fatalf("expected valid, got: %v", err)
	}

	// 2. Invalid process limit (fork starvation risk < 128)
	s2 := HardwareLimitsSpec{MaxProcesses: 32}
	if err := s2.Validate(); err == nil {
		t.Fatalf("expected error for processes < 128")
	}

	// 3. Invalid CPU allowance > 100
	s3 := HardwareLimitsSpec{CpuAllowancePct: 150}
	if err := s3.Validate(); err == nil {
		t.Fatalf("expected error for allowance > 100")
	}
}

func TestMockDriver_ExportAndImportFlow(t *testing.T) {
	ctx := context.Background()
	d := NewMockDriver()

	spec := incus.InstanceSpec{
		Name:             "source-ct",
		ImageFingerprint: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
		CPU:              1,
		MemoryMiB:        512,
		DiskGiB:          10,
		StoragePool:      "default",
		Network:          "incusbr0",
	}
	_, _ = d.CreateOwned(ctx, spec, "0123456789abcdef0123456789abcdef")

	// 1. Export
	buf := new(bytes.Buffer)
	err := d.ExportInstance(ctx, "source-ct", buf, ExportOptions{InstanceOnly: true})
	if err != nil {
		t.Fatalf("export failed: %v", err)
	}
	if buf.Len() == 0 {
		t.Fatalf("export produced empty payload")
	}

	// 2. Import
	inst, err := d.ImportInstance(ctx, ImportRequest{
		NewName:         "restored-ct",
		StoragePool:     "default",
		ResetMacAddress: true,
	}, buf)
	if err != nil {
		t.Fatalf("import failed: %v", err)
	}
	if inst.Name != "restored-ct" {
		t.Errorf("expected restored-ct, got %s", inst.Name)
	}

	// 3. Update Hardware Limits
	err = d.UpdateHardwareLimits(ctx, "restored-ct", HardwareLimitsSpec{
		CpuCores:  4,
		MemoryMiB: 1024,
		DiskGiB:   20,
	})
	if err != nil {
		t.Fatalf("update hardware limits failed: %v", err)
	}

	disc, _ := d.Discover(ctx)
	found := false
	for _, it := range disc.Instances {
		if it.Name == "restored-ct" {
			found = true
			if it.CPU != "4" || it.Memory != "1024MiB" || it.Disk != "20GiB" {
				t.Errorf("hardware limits not applied correctly: %+v", it)
			}
		}
	}
	if !found {
		t.Errorf("restored-ct not found in discovery")
	}
}

func TestHostNetworkDetector_Basic(t *testing.T) {
	det := &HostNetworkDetector{}
	nics := det.DetectPhysicalInterfaces()
	if len(nics) == 0 {
		t.Fatalf("expected at least 1 NIC")
	}
}

func TestDeviceRemapVisitor_Sanitization(t *testing.T) {
	visitor := &DeviceRemapVisitor{
		TargetStoragePool: "nvme-pool",
		ResetMacAddress:   true,
	}

	config := map[string]any{
		"volatile.eth0.hwaddr": "00:11:22:33:44:55",
		"volatile.idmap.base":  "1000000",
		"security.privileged":  "false",
	}
	devices := map[string]map[string]any{
		"root": {
			"type": "disk",
			"pool": "old-pool",
			"path": "/",
		},
		"eth0": {
			"type":   "nic",
			"hwaddr": "00:11:22:33:44:55",
		},
	}

	visitor.Cleanse(config, devices)

	if _, ok := config["volatile.eth0.hwaddr"]; ok {
		t.Errorf("volatile keys should be deleted")
	}
	if devices["root"]["pool"] != "nvme-pool" {
		t.Errorf("root disk pool not remapped")
	}
	if _, ok := devices["eth0"]["hwaddr"]; ok {
		t.Errorf("static hwaddr should be removed")
	}
}
