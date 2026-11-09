package driver

import (
	"context"
	"io"

	"lwt/lxc-manager/internal/incus"
)

type EngineCapabilities struct {
	SupportsHotSnapshot        bool `json:"supportsHotSnapshot"`
	SupportsWebSocketTerminal  bool `json:"supportsWebSocketTerminal"`
	SupportsStorageProjectQuota bool `json:"supportsStorageProjectQuota"`
	SupportsNativePortForward  bool `json:"supportsNativePortForward"`
}

type ILxcEngineDriver interface {
	Name() string
	Capabilities(ctx context.Context) EngineCapabilities
	Probe(ctx context.Context) incus.Capability
	Discover(ctx context.Context) (incus.Discovery, error)
	SampleResources(ctx context.Context, pool string) (incus.ResourceSample, error)
	CreateOwned(ctx context.Context, s incus.InstanceSpec, jobID string) (string, error)
	ChangeState(ctx context.Context, name, action string) (string, error)
	InstanceState(ctx context.Context, name string) (string, error)
	VerifyCreated(ctx context.Context, name, jobID string, s incus.InstanceSpec) (bool, error)
	IsRunning(ctx context.Context, name string) (bool, error)
	Operation(ctx context.Context, op string) (string, error)
	GetHostInterfaces(ctx context.Context) ([]HostNicInfo, error)
	UpdateHardwareLimits(ctx context.Context, name string, spec HardwareLimitsSpec) error
	DeleteInstance(ctx context.Context, name string, force bool) error
	Exec(ctx context.Context, name string, cmd []string) (string, string, int, error)
	ExportInstance(ctx context.Context, name string, w io.Writer, opts ExportOptions) error
	ImportInstance(ctx context.Context, req ImportRequest, r io.Reader) (*incus.InstanceInfo, error)
	GetStoragePools(ctx context.Context) ([]StoragePoolInfo, error)
}
