package driver

import (
	"context"
	"io"
	"log"
	"sync"
	"time"

	"lwt/lxc-manager/internal/incus"
)

// AdaptiveDriver provides hot-swapping between real Incus REST and Sandbox Mock drivers.
type AdaptiveDriver struct {
	mu           sync.RWMutex
	current      ILxcEngineDriver
	mockDriver   ILxcEngineDriver
	incusDriver  ILxcEngineDriver
	adapter      incus.Client
	isRealIncus  bool
	stopObserver chan struct{}
}

// NewAdaptiveDriver constructs an AdaptiveDriver that wraps both drivers.
func NewAdaptiveDriver(adapter incus.Client, preferReal bool) *AdaptiveDriver {
	mock := NewMockDriver()
	var incusDr ILxcEngineDriver
	if adapter.HTTP != nil {
		incusDr = NewIncusDriver(adapter)
	}

	var initialDriver ILxcEngineDriver = mock
	isReal := false
	if preferReal && incusDr != nil {
		initialDriver = incusDr
		isReal = true
	}

	ad := &AdaptiveDriver{
		current:      initialDriver,
		mockDriver:   mock,
		incusDriver:  incusDr,
		adapter:      adapter,
		isRealIncus:  isReal,
		stopObserver: make(chan struct{}),
	}
	return ad
}

// StartObserver launches a background goroutine to probe the Incus daemon and hot-swap drivers.
func (a *AdaptiveDriver) StartObserver(interval time.Duration) {
	if a.incusDriver == nil {
		return
	}
	go func() {
		ticker := time.NewTicker(interval)
		defer ticker.Stop()
		for {
			select {
			case <-a.stopObserver:
				return
			case <-ticker.C:
				a.recheck()
			}
		}
	}()
}

// StopObserver cancels background health probing.
func (a *AdaptiveDriver) StopObserver() {
	select {
	case <-a.stopObserver:
	default:
		close(a.stopObserver)
	}
}

// IsReal returns whether the real physical Incus driver is currently active.
func (a *AdaptiveDriver) IsReal() bool {
	a.mu.RLock()
	defer a.mu.RUnlock()
	return a.isRealIncus
}

func (a *AdaptiveDriver) recheck() {
	if a.incusDriver == nil {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	cap := incus.Probe(ctx, a.adapter.HTTP, a.adapter.Endpoint)
	cancel()

	a.mu.Lock()
	defer a.mu.Unlock()

	if cap.Status == incus.Ready {
		if !a.isRealIncus {
			a.current = a.incusDriver
			a.isRealIncus = true
			log.Printf("[LXC] Hot-Swap: Incus daemon ready (v%s); transitioned to physical driver", cap.Version)
		}
	} else {
		if a.isRealIncus {
			a.current = a.mockDriver
			a.isRealIncus = false
			log.Printf("[LXC] Hot-Swap: Incus daemon unreachable; degraded to sandbox mock driver")
		}
	}
}

func (a *AdaptiveDriver) getDriver() ILxcEngineDriver {
	a.mu.RLock()
	defer a.mu.RUnlock()
	return a.current
}

func (a *AdaptiveDriver) Name() string {
	return a.getDriver().Name()
}

func (a *AdaptiveDriver) Capabilities(ctx context.Context) EngineCapabilities {
	return a.getDriver().Capabilities(ctx)
}

func (a *AdaptiveDriver) Probe(ctx context.Context) incus.Capability {
	return a.getDriver().Probe(ctx)
}

func (a *AdaptiveDriver) Discover(ctx context.Context) (incus.Discovery, error) {
	return a.getDriver().Discover(ctx)
}

func (a *AdaptiveDriver) SampleResources(ctx context.Context, pool string) (incus.ResourceSample, error) {
	return a.getDriver().SampleResources(ctx, pool)
}

func (a *AdaptiveDriver) CreateOwned(ctx context.Context, s incus.InstanceSpec, jobID string) (string, error) {
	return a.getDriver().CreateOwned(ctx, s, jobID)
}

func (a *AdaptiveDriver) ChangeState(ctx context.Context, name, action string) (string, error) {
	return a.getDriver().ChangeState(ctx, name, action)
}

func (a *AdaptiveDriver) InstanceState(ctx context.Context, name string) (string, error) {
	return a.getDriver().InstanceState(ctx, name)
}

func (a *AdaptiveDriver) VerifyCreated(ctx context.Context, name, jobID string, s incus.InstanceSpec) (bool, error) {
	return a.getDriver().VerifyCreated(ctx, name, jobID, s)
}

func (a *AdaptiveDriver) IsRunning(ctx context.Context, name string) (bool, error) {
	return a.getDriver().IsRunning(ctx, name)
}

func (a *AdaptiveDriver) Operation(ctx context.Context, op string) (string, error) {
	return a.getDriver().Operation(ctx, op)
}

func (a *AdaptiveDriver) GetHostInterfaces(ctx context.Context) ([]HostNicInfo, error) {
	return a.getDriver().GetHostInterfaces(ctx)
}

func (a *AdaptiveDriver) UpdateHardwareLimits(ctx context.Context, name string, spec HardwareLimitsSpec) error {
	return a.getDriver().UpdateHardwareLimits(ctx, name, spec)
}

func (a *AdaptiveDriver) DeleteInstance(ctx context.Context, name string, force bool) error {
	return a.getDriver().DeleteInstance(ctx, name, force)
}

func (a *AdaptiveDriver) Exec(ctx context.Context, name string, cmd []string) (string, string, int, error) {
	return a.getDriver().Exec(ctx, name, cmd)
}

func (a *AdaptiveDriver) ExportInstance(ctx context.Context, name string, w io.Writer, opts ExportOptions) error {
	return a.getDriver().ExportInstance(ctx, name, w, opts)
}

func (a *AdaptiveDriver) ImportInstance(ctx context.Context, req ImportRequest, r io.Reader) (*incus.InstanceInfo, error) {
	return a.getDriver().ImportInstance(ctx, req, r)
}

func (a *AdaptiveDriver) GetStoragePools(ctx context.Context) ([]StoragePoolInfo, error) {
	return a.getDriver().GetStoragePools(ctx)
}
