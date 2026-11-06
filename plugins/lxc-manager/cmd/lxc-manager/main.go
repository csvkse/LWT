package main

import (
	"context"
	"log"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strconv"
	"time"

	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
	"lwt/lxc-manager/internal/server"
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
	socket := os.Getenv("INCUS_SOCKET")
	if socket == "" {
		socket = "/var/lib/incus/unix.socket"
	}
	transport := &http.Transport{DialContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
		return (&net.Dialer{}).DialContext(ctx, "unix", socket)
	}}
	client := &http.Client{Transport: transport, Timeout: 5 * time.Second, CheckRedirect: func(*http.Request, []*http.Request) error { return http.ErrUseLastResponse }}
	adapter := incus.Client{HTTP: client, Endpoint: "http://incus"}
	handler := server.NewWithResources(secret, func(ctx context.Context) incus.Capability { return incus.Probe(ctx, client, "http://incus") }, adapter.Discover, adapter.SampleResources)
	if dir := os.Getenv("LWT_STATE_DIR"); dir != "" && runtime.GOOS == "linux" {
		store, err := jobs.Open(filepath.Join(dir, "jobs"))
		if err != nil {
			log.Printf("creation unavailable: cannot initialize task storage")
		} else {
			creator := workflow.Creator{Store: store, Create: adapter.CreateOwned, Observe: adapter.Operation, Verify: adapter.VerifyCreated, Running: adapter.IsRunning,
				Start: func(ctx context.Context, name string) (string, error) { return adapter.ChangeState(ctx, name, "start") },
				Check: func(ctx context.Context, spec incus.InstanceSpec) (incus.PreflightResult, error) {
					resources, err := adapter.Discover(ctx)
					if err != nil {
						return incus.PreflightResult{}, err
					}
					// No server-side capacity/quota evidence source exists yet: fail closed.
					return incus.Preflight(spec, resources, incus.PreflightEvidence{}), nil
				},
			}
			manager := workflow.Lifecycle{Store: store, Verify: adapter.VerifyCreated, Change: adapter.ChangeState, Observe: adapter.Operation, State: adapter.InstanceState}
			handler = server.NewWithManagement(secret, func(ctx context.Context) incus.Capability { return incus.Probe(ctx, client, "http://incus") }, adapter.Discover, adapter.SampleResources, manager, creator)
		}
	}
	srv := &http.Server{Addr: net.JoinHostPort("127.0.0.1", strconv.Itoa(port)), Handler: handler, ReadHeaderTimeout: 5 * time.Second, IdleTimeout: 30 * time.Second}
	log.Fatal(srv.ListenAndServe())
}
