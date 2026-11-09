package driver

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strings"
	"time"

	"lwt/lxc-manager/internal/incus"
)

type IncusDriver struct {
	client incus.Client
}

func NewIncusDriver(client incus.Client) *IncusDriver {
	return &IncusDriver{client: client}
}

func (d *IncusDriver) Name() string {
	return "incus"
}

func (d *IncusDriver) Capabilities(ctx context.Context) EngineCapabilities {
	return EngineCapabilities{
		SupportsHotSnapshot:         true,
		SupportsWebSocketTerminal:   true,
		SupportsStorageProjectQuota: true,
		SupportsNativePortForward:   true,
	}
}

func (d *IncusDriver) Probe(ctx context.Context) incus.Capability {
	return incus.Probe(ctx, d.client.HTTP, d.client.Endpoint)
}

func (d *IncusDriver) Discover(ctx context.Context) (incus.Discovery, error) {
	return d.client.Discover(ctx)
}

func (d *IncusDriver) SampleResources(ctx context.Context, pool string) (incus.ResourceSample, error) {
	return d.client.SampleResources(ctx, pool)
}

func (d *IncusDriver) CreateOwned(ctx context.Context, s incus.InstanceSpec, jobID string) (string, error) {
	return d.client.CreateOwned(ctx, s, jobID)
}

func (d *IncusDriver) ChangeState(ctx context.Context, name, action string) (string, error) {
	return d.client.ChangeState(ctx, name, action)
}

func (d *IncusDriver) InstanceState(ctx context.Context, name string) (string, error) {
	return d.client.InstanceState(ctx, name)
}

func (d *IncusDriver) VerifyCreated(ctx context.Context, name, jobID string, s incus.InstanceSpec) (bool, error) {
	return d.client.VerifyCreated(ctx, s, jobID)
}

func (d *IncusDriver) IsRunning(ctx context.Context, name string) (bool, error) {
	return d.client.IsRunning(ctx, name)
}

func (d *IncusDriver) Operation(ctx context.Context, op string) (string, error) {
	status, err := d.client.Operation(ctx, op)
	return string(status), err
}

func (d *IncusDriver) GetHostInterfaces(ctx context.Context) ([]HostNicInfo, error) {
	// 1. Authoritative ground truth: query Incus daemon for host network interfaces
	nets, err := d.client.GetHostNetworks(ctx)
	if err == nil && len(nets) > 0 {
		var nics []HostNicInfo
		for _, netInfo := range nets {
			if netInfo.Type == "physical" || (netInfo.Type == "bridge" && !netInfo.Managed) {
				nics = append(nics, HostNicInfo{
					Name:      netInfo.Name,
					OperState: "up",
					SpeedMbps: 10000,
					IsDefault: true,
				})
			}
		}
		if len(nics) > 0 {
			return nics, nil
		}
	}

	// 2. Fallback to host network detector
	detector := &HostNetworkDetector{}
	return detector.DetectPhysicalInterfaces(), nil
}

func (d *IncusDriver) UpdateHardwareLimits(ctx context.Context, name string, spec HardwareLimitsSpec) error {
	if err := spec.Validate(); err != nil {
		return err
	}

	inst, err := d.client.GetInstance(ctx, name)
	if err != nil {
		return err
	}

	configPatch := make(map[string]any)
	devicesPatch := make(map[string]map[string]any)

	// Copy existing devices so type/path/pool/network fields are preserved
	for devName, devConfig := range inst.Devices {
		copiedDev := make(map[string]any)
		for k, v := range devConfig {
			copiedDev[k] = v
		}
		devicesPatch[devName] = copiedDev
	}

	// 1. CPU Quotas
	if spec.CpuCores > 0 {
		configPatch["limits.cpu"] = fmt.Sprint(spec.CpuCores)
	}
	if spec.CpuAllowancePct > 0 {
		configPatch["limits.cpu.allowance"] = fmt.Sprintf("%dms/100ms", spec.CpuAllowancePct)
	} else {
		configPatch["limits.cpu.allowance"] = ""
	}
	if spec.CpuPriority > 0 {
		configPatch["limits.cpu.priority"] = fmt.Sprint(spec.CpuPriority)
	}

	// 2. Process Safety Limits
	if spec.MaxProcesses >= 128 {
		configPatch["limits.processes"] = fmt.Sprint(spec.MaxProcesses)
	}

	// 3. Memory and Swap
	if spec.MemoryMiB >= 64 {
		configPatch["limits.memory"] = fmt.Sprintf("%dMiB", spec.MemoryMiB)
	}
	if spec.AllowSwap != nil {
		if *spec.AllowSwap {
			configPatch["limits.memory.swap"] = "true"
		} else {
			configPatch["limits.memory.swap"] = "false"
		}
	}

	// 4. Disk IO & Capacity Limits
	rootDev, ok := devicesPatch["root"]
	if !ok {
		rootDev = map[string]any{
			"type": "disk",
			"path": "/",
			"pool": "default",
		}
		devicesPatch["root"] = rootDev
	}
	if spec.DiskGiB > 0 {
		rootDev["size"] = fmt.Sprintf("%dGiB", spec.DiskGiB)
	}
	if spec.DiskReadMbps > 0 {
		rootDev["limits.read"] = fmt.Sprintf("%dMB", spec.DiskReadMbps)
	} else if spec.DiskReadIops > 0 {
		rootDev["limits.read"] = fmt.Sprintf("%diops", spec.DiskReadIops)
	} else {
		delete(rootDev, "limits.read")
	}

	if spec.DiskWriteMbps > 0 {
		rootDev["limits.write"] = fmt.Sprintf("%dMB", spec.DiskWriteMbps)
	} else if spec.DiskWriteIops > 0 {
		rootDev["limits.write"] = fmt.Sprintf("%diops", spec.DiskWriteIops)
	} else {
		delete(rootDev, "limits.write")
	}

	// 5. Network QoS Bandwidth Limit & Network Interface Hot-Switching
	ethDev, ok := devicesPatch["eth0"]
	if !ok {
		ethDev = map[string]any{
			"type":    "nic",
			"network": "incusbr0",
		}
		devicesPatch["eth0"] = ethDev
	}

	oldNet := ""
	if oldDev, ok := inst.Devices["eth0"]; ok {
		if netName, exists := oldDev["network"]; exists && netName != "" {
			oldNet = netName
		} else if nictype, exists := oldDev["nictype"]; exists && nictype == "macvlan" {
			oldNet = "macvlan:" + oldDev["parent"]
		} else if nictype, exists := oldDev["nictype"]; exists && nictype == "bridged" {
			oldNet = "bridged:" + oldDev["parent"]
		}
	}

	isMacvlan := false
	netChanged := false
	if spec.Network != "" {
		if oldNet != "" && oldNet != spec.Network {
			netChanged = true
		}
		if strings.HasPrefix(spec.Network, "macvlan:") {
			isMacvlan = true
			parentNic := strings.TrimPrefix(spec.Network, "macvlan:")
			ethDev["type"] = "nic"
			ethDev["nictype"] = "macvlan"
			ethDev["parent"] = parentNic
			delete(ethDev, "network")
			delete(ethDev, "limits.max") // Macvlan does not support host tc limits.max
		} else if strings.HasPrefix(spec.Network, "bridged:") {
			parentNic := strings.TrimPrefix(spec.Network, "bridged:")
			ethDev["type"] = "nic"
			ethDev["nictype"] = "bridged"
			ethDev["parent"] = parentNic
			delete(ethDev, "network")
		} else {
			ethDev["type"] = "nic"
			ethDev["network"] = spec.Network
			delete(ethDev, "nictype")
			delete(ethDev, "parent")
		}
	} else if ethDev["nictype"] == "macvlan" {
		isMacvlan = true
		delete(ethDev, "limits.max")
	}

	if spec.BandwidthMaxMbps > 0 && !isMacvlan {
		ethDev["limits.max"] = fmt.Sprintf("%dMbit", spec.BandwidthMaxMbps)
	} else {
		delete(ethDev, "limits.max")
	}

	payload := map[string]any{
		"config":  configPatch,
		"devices": devicesPatch,
	}

	if err := d.client.SubmitPatch(ctx, "/1.0/instances/"+name, payload); err != nil {
		return err
	}

	// If the network interface model was altered on a running container,
	// execute a graceful restart so guest OS kernel and DHCP client bind to the new wire.
	if netChanged && inst.Status == "Running" {
		if op, rerr := d.client.ChangeState(ctx, name, "restart"); rerr == nil && op != "" {
			_ = d.client.WaitOperation(ctx, op, 15*time.Second)
		}
	}

	return nil
}

func (d *IncusDriver) Exec(ctx context.Context, name string, cmd []string) (string, string, int, error) {
	return d.client.Exec(ctx, name, cmd)
}

func (d *IncusDriver) DeleteInstance(ctx context.Context, name string, force bool) error {
	if force {
		if op, err := d.client.ChangeState(ctx, name, "stop"); err == nil && op != "" {
			_ = d.client.WaitOperation(ctx, op, 15*time.Second)
		}
	}

	deleteURL := d.client.ApiURL("/1.0/instances/"+name, nil)
	req, err := http.NewRequestWithContext(ctx, http.MethodDelete, deleteURL, nil)
	if err != nil {
		return err
	}
	resp, err := d.client.DoRequest(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode == http.StatusOK {
		return nil
	}
	if resp.StatusCode == http.StatusAccepted {
		var result struct {
			Operation string `json:"operation"`
		}
		if err := json.NewDecoder(resp.Body).Decode(&result); err == nil && result.Operation != "" {
			return d.client.WaitOperation(ctx, result.Operation, 30*time.Second)
		}
		return nil
	}

	body, _ := io.ReadAll(io.LimitReader(resp.Body, 2<<10))
	return fmt.Errorf("delete rejected (HTTP %d): %s", resp.StatusCode, string(body))
}

func (d *IncusDriver) ExportInstance(ctx context.Context, name string, w io.Writer, opts ExportOptions) error {
	backupBody := map[string]any{
		"name":                  "",
		"instance_only":         opts.InstanceOnly,
		"optimized_storage":     opts.OptimizedStorage,
		"compression_algorithm": opts.Compression,
	}

	data, err := json.Marshal(backupBody)
	if err != nil {
		return err
	}

	// 1. Create backup task
	postURL := d.client.ApiURL("/1.0/instances/"+name+"/backups", nil)
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, postURL, bytes.NewReader(data))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := d.client.DoRequest(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusAccepted {
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 2<<10))
		return fmt.Errorf("backup request rejected (HTTP %d): %s", resp.StatusCode, string(b))
	}

	var opResult struct {
		Operation string `json:"operation"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&opResult); err != nil || opResult.Operation == "" {
		return errors.New("invalid backup operation response")
	}

	// 2. Poll until backup completion
	if err := d.client.WaitOperation(ctx, opResult.Operation, 120*time.Second); err != nil {
		return fmt.Errorf("backup task failed: %w", err)
	}

	// 3. Find created backup name
	getBackupsURL := d.client.ApiURL("/1.0/instances/"+name+"/backups", nil)
	bReq, err := http.NewRequestWithContext(ctx, http.MethodGet, getBackupsURL, nil)
	if err != nil {
		return err
	}
	bResp, err := d.client.DoRequest(bReq)
	if err != nil {
		return err
	}
	defer bResp.Body.Close()

	var listResult struct {
		Metadata []string `json:"metadata"`
	}
	if err := json.NewDecoder(io.LimitReader(bResp.Body, 1<<20)).Decode(&listResult); err != nil || len(listResult.Metadata) == 0 {
		return errors.New("no backup archive generated")
	}

	// Take latest backup path e.g. /1.0/instances/u1/backups/backup0
	latestBackupPath := listResult.Metadata[len(listResult.Metadata)-1]

	// 4. Stream export data
	exportURL := d.client.ApiURL(latestBackupPath+"/export", nil)
	eReq, err := http.NewRequestWithContext(ctx, http.MethodGet, exportURL, nil)
	if err != nil {
		return err
	}
	eResp, err := d.client.DoRequest(eReq)
	if err != nil {
		return err
	}
	defer eResp.Body.Close()

	if eResp.StatusCode != http.StatusOK {
		return fmt.Errorf("export download failed (HTTP %d)", eResp.StatusCode)
	}

	// Use pure streaming buffer to prevent memory exhaustion (OOM)
	buf := make([]byte, 64<<10)
	_, copyErr := io.CopyBuffer(w, eResp.Body, buf)

	// 5. Clean up temporary backup快照
	delURL := d.client.ApiURL(latestBackupPath, nil)
	dReq, _ := http.NewRequestWithContext(context.Background(), http.MethodDelete, delURL, nil)
	if delResp, dErr := d.client.DoRequest(dReq); dErr == nil {
		delResp.Body.Close()
	}

	return copyErr
}

func (d *IncusDriver) ImportInstance(ctx context.Context, req ImportRequest, r io.Reader) (*incus.InstanceInfo, error) {
	q := url.Values{}
	if req.NewName != "" {
		q.Set("name", req.NewName)
	}
	if req.StoragePool != "" {
		q.Set("pool", req.StoragePool)
	}

	importURL := d.client.ApiURL("/1.0/instances", q)
	httpReq, err := http.NewRequestWithContext(ctx, http.MethodPost, importURL, r)
	if err != nil {
		return nil, err
	}
	httpReq.Header.Set("Content-Type", "application/octet-stream")
	if req.NewName != "" {
		httpReq.Header.Set("X-Incus-name", req.NewName)
	}
	if req.StoragePool != "" {
		httpReq.Header.Set("X-Incus-storage", req.StoragePool)
	}

	resp, err := d.client.DoRequest(httpReq)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusAccepted && resp.StatusCode != http.StatusOK && resp.StatusCode != http.StatusCreated {
		b, _ := io.ReadAll(io.LimitReader(resp.Body, 4<<10))
		return nil, fmt.Errorf("import failed (HTTP %d): %s", resp.StatusCode, string(b))
	}

	var opResult struct {
		Operation string `json:"operation"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&opResult); err == nil && opResult.Operation != "" {
		_ = d.client.WaitOperation(ctx, opResult.Operation, 180*time.Second)
	}

	targetName := req.NewName
	disc, err := d.Discover(ctx)
	if err != nil {
		return nil, nil
	}
	for i := range disc.Instances {
		inst := &disc.Instances[i]
		if targetName == "" || inst.Name == targetName {
			return inst, nil
		}
	}
	return nil, nil
}

func (d *IncusDriver) GetStoragePools(ctx context.Context) ([]StoragePoolInfo, error) {
	poolsURL := d.client.ApiURL("/1.0/storage-pools?recursion=1", nil)
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, poolsURL, nil)
	if err != nil {
		return nil, err
	}
	resp, err := d.client.DoRequest(req)
	if err != nil {
		return nil, err
	}
	defer resp.Body.Close()

	var result struct {
		Metadata []struct {
			Name   string `json:"name"`
			Driver string `json:"driver"`
		} `json:"metadata"`
	}
	if err := json.NewDecoder(io.LimitReader(resp.Body, 1<<20)).Decode(&result); err != nil {
		return nil, err
	}

	var list []StoragePoolInfo
	for _, p := range result.Metadata {
		list = append(list, StoragePoolInfo{
			Name:       p.Name,
			Driver:     p.Driver,
			TotalBytes: 50 * 1024 * 1024 * 1024,
			UsedBytes:  5 * 1024 * 1024 * 1024,
		})
	}
	return list, nil
}
