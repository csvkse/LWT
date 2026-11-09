package main

import (
	"context"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strconv"
	"time"

	"lwt/lxc-manager/internal/driver"
	"lwt/lxc-manager/internal/evidence"
	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
	"lwt/lxc-manager/internal/network"
	"lwt/lxc-manager/internal/server"
	"lwt/lxc-manager/internal/terminal"
	"lwt/lxc-manager/internal/workflow"
)

func main() {
	secret := os.Getenv("LWT_INTERNAL_SECRET")
	if secret == "" {
		log.Fatal("platform internal secret required")
	}
	port, err := strconv.Atoi(os.Getenv("PORT"))
	if err != nil || port < 1 || port > 65535 {
		log.Fatal("valid allocated PORT required")
	}

	stateDir := os.Getenv("LWT_STATE_DIR")
	if stateDir == "" {
		stateDir = filepath.Join(os.TempDir(), "lwt-lxc-manager-state")
	}
	_ = os.MkdirAll(stateDir, 0755)

	// 1. Evidence Provider & Network Store & Terminal Manager
	evidenceProvider := evidence.DefaultHostEvidenceProvider()
	terminalManager := terminal.NewSessionManager()
	netStore, err := network.OpenStore(filepath.Join(stateDir, "network"))
	if err != nil {
		log.Printf("network store warning: %v", err)
	}

	// 2. Select Driver: Check real Incus or fallback to Sandbox Mock
	var activeDriver driver.ILxcEngineDriver

	connectionConfig := incus.ConnectionConfig{
		Endpoint:                os.Getenv("INCUS_ENDPOINT"),
		UnixSocket:              os.Getenv("INCUS_SOCKET"),
		Project:                 os.Getenv("INCUS_PROJECT"),
		ServerCAFile:            os.Getenv("INCUS_TLS_SERVER_CA_FILE"),
		ServerCertificateSHA256: os.Getenv("INCUS_TLS_SERVER_CERT_SHA256"),
		ClientCertificateFile:   os.Getenv("INCUS_TLS_CLIENT_CERT_FILE"),
		ClientKeyFile:           os.Getenv("INCUS_TLS_CLIENT_KEY_FILE"),
		RequestTimeout:          3 * time.Second,
	}

	adapter, err := incus.NewConnection(connectionConfig)
	if err == nil {
		ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
		cap := incus.Probe(ctx, adapter.HTTP, adapter.Endpoint)
		cancel()
		isReady := (cap.Status == incus.Ready)
		adaptive := driver.NewAdaptiveDriver(adapter, isReady)
		adaptive.StartObserver(5 * time.Second)
		activeDriver = adaptive
		if isReady {
			log.Printf("[LXC] Real Incus daemon detected and connected (version: %s)", cap.Version)
		} else {
			log.Printf("[LXC] Incus daemon not reachable at startup; running adaptive Sandbox Mock with active background health probe")
		}
	} else {
		log.Printf("[LXC] Incus connection adapter failed (%v); activating fallback Mock driver", err)
		activeDriver = driver.NewMockDriver()
	}

	terminalManager.SetExecutor(activeDriver)

	probeFunc := func(ctx context.Context) incus.Capability {
		return activeDriver.Probe(ctx)
	}

	// 3. Setup Jobs Store and Workflow
	var flows []server.CreateFlow
	var manager server.ManagementFlow

	store, err := jobs.Open(filepath.Join(stateDir, "jobs"))
	if err != nil {
		log.Printf("[LXC] Task storage unavailable: %v", err)
	} else {
		creator := workflow.Creator{
			Store:   store,
			Create:  activeDriver.CreateOwned,
			Observe: func(ctx context.Context, op string) (incus.OperationStatus, error) {
				st, oerr := activeDriver.Operation(ctx, op)
				return incus.OperationStatus(st), oerr
			},
			Verify:  func(ctx context.Context, spec incus.InstanceSpec, jobID string) (bool, error) {
				return activeDriver.VerifyCreated(ctx, spec.Name, jobID, spec)
			},
			Running: activeDriver.IsRunning,
			Start: func(ctx context.Context, name string) (string, error) {
				return activeDriver.ChangeState(ctx, name, "start")
			},
			Check: func(ctx context.Context, spec incus.InstanceSpec) (incus.PreflightResult, error) {
				resources, derr := activeDriver.Discover(ctx)
				if derr != nil {
					return incus.PreflightResult{}, derr
				}
				ev := evidenceProvider.CollectEvidence(ctx, spec.StoragePool, resources)
				return incus.Preflight(spec, resources, ev), nil
			},
		}
		flows = append(flows, creator)

		manager = workflow.Lifecycle{
			Store: store,
			Verify: func(ctx context.Context, spec incus.InstanceSpec, jobID string) (bool, error) {
				return activeDriver.VerifyCreated(ctx, spec.Name, jobID, spec)
			},
			Change: activeDriver.ChangeState,
			Observe: func(ctx context.Context, op string) (incus.OperationStatus, error) {
				st, oerr := activeDriver.Operation(ctx, op)
				return incus.OperationStatus(st), oerr
			},
			State: activeDriver.InstanceState,
		}
	}

	// 4. Build Server
	handler := server.NewServer(server.ServerOptions{
		Secret:     secret,
		Probe:      probeFunc,
		Discover:   activeDriver.Discover,
		Sample:     activeDriver.SampleResources,
		Evidence:   evidenceProvider,
		Terminal:   terminalManager,
		Network:    netStore,
		Management: manager,
		Control:    activeDriver.ChangeState,
		Flows:      flows,
		Driver:     activeDriver,
	})

	srv := &http.Server{
		Addr:              net.JoinHostPort("127.0.0.1", strconv.Itoa(port)),
		Handler:           handler,
		ReadHeaderTimeout: 5 * time.Second,
		IdleTimeout:       30 * time.Second,
	}

	log.Printf("[LXC] Manager daemon listening on 127.0.0.1:%d", port)
	log.Fatal(srv.ListenAndServe())
}
