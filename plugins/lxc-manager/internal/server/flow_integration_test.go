package server

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"net/http"
	"net/http/httptest"
	"testing"

	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
	"lwt/lxc-manager/internal/workflow"
)

// This exercises real plugin components over HTTP, with only Incus simulated.
func TestHTTPCreateReconcileAndLifecycleAgainstIncusFixture(t *testing.T) {
	owner := "0123456789abcdef0123456789abcdef"
	actionKey := "fedcba9876543210fedcba9876543210"
	spec := incus.InstanceSpec{Name: "demo", ImageFingerprint: "abcdef123456", CPU: 1, MemoryMiB: 512, DiskGiB: 8, StoragePool: "pool1", Network: "br0"}
	createCalls := 0
	state := 102
	var created struct {
		Name    string                       `json:"name"`
		Type    string                       `json:"type"`
		Config  map[string]string            `json:"config"`
		Devices map[string]map[string]string `json:"devices"`
		Source  map[string]string            `json:"source"`
	}
	backend := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		switch {
		case r.Method == "POST" && r.URL.Path == "/1.0/instances":
			createCalls++
			if err := json.NewDecoder(r.Body).Decode(&created); err != nil {
				t.Error(err)
			}
			w.WriteHeader(202)
			_, _ = w.Write([]byte(`{"type":"async","status_code":100,"operation":"/1.0/operations/create"}`))
		case r.Method == "PUT" && r.URL.Path == "/1.0/instances/demo/state":
			var req struct {
				Action string `json:"action"`
			}
			if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
				t.Error(err)
			}
			if req.Action == "stop" {
				state = 102
			} else {
				state = 103
			}
			w.WriteHeader(202)
			_, _ = w.Write([]byte(`{"type":"async","status_code":100,"operation":"/1.0/operations/` + req.Action + `"}`))
		case r.Method == "GET" && r.URL.Path == "/1.0/instances/demo":
			cfg := map[string]string{}
			for k, v := range created.Config {
				cfg[k] = v
			}
			cfg["volatile.base_image"] = created.Source["fingerprint"]
			_ = json.NewEncoder(w).Encode(map[string]any{"type": "sync", "status_code": 200, "metadata": map[string]any{"name": created.Name, "type": created.Type, "expanded_config": cfg, "expanded_devices": created.Devices}})
		case r.Method == "GET" && r.URL.Path == "/1.0/instances/demo/state":
			_, _ = fmt.Fprintf(w, `{"type":"sync","status_code":200,"metadata":{"status_code":%d}}`, state)
		case r.Method == "GET" && len(r.URL.Path) > 16 && r.URL.Path[:16] == "/1.0/operations/":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"status_code":200,"err":""}}`))
		default:
			t.Errorf("unexpected backend request %s %s", r.Method, r.URL.Path)
			http.NotFound(w, r)
		}
	}))
	defer backend.Close()
	adapter := incus.Client{HTTP: backend.Client(), Endpoint: backend.URL}
	dir := t.TempDir()
	store, err := jobs.Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	discovery := incus.Discovery{Images: []incus.ImageInfo{{Fingerprint: spec.ImageFingerprint}}, Pools: []incus.PoolInfo{{Name: spec.StoragePool, Status: "Created"}}, Networks: []incus.NetworkInfo{{Name: spec.Network}}}
	evidence := incus.PreflightEvidence{Verified: true, CPUAvailable: 4, MemoryAvailableMiB: 4096, DiskAvailableGiB: 100, QuotaVerifiedForPool: spec.StoragePool}
	creator := workflow.Creator{Store: store, Check: func(_ context.Context, s incus.InstanceSpec) (incus.PreflightResult, error) {
		return incus.Preflight(s, discovery, evidence), nil
	}, Create: adapter.CreateOwned, Observe: adapter.Operation, Verify: adapter.VerifyCreated, Start: func(ctx context.Context, name string) (string, error) { return adapter.ChangeState(ctx, name, "start") }, Running: adapter.IsRunning}
	manager := workflow.Lifecycle{Store: store, Verify: adapter.VerifyCreated, Change: adapter.ChangeState, Observe: adapter.Operation, State: adapter.InstanceState}
	plugin := httptest.NewServer(NewWithManagement("test-secret", func(context.Context) incus.Capability { return incus.Capability{Status: incus.Ready} }, nil, nil, manager, creator))
	defer func() { plugin.Close() }()
	call := func(path string, payload any, want int) jobs.Result {
		data, _ := json.Marshal(payload)
		req, _ := http.NewRequest("POST", plugin.URL+path, bytes.NewReader(data))
		req.Header.Set("Content-Type", "application/json")
		req.Header.Set("X-LWT-Internal-Secret", "test-secret")
		req.Header.Set("X-LWT-Auth-Type", "AdminSession")
		resp, err := plugin.Client().Do(req)
		if err != nil {
			t.Fatal(err)
		}
		defer resp.Body.Close()
		if resp.StatusCode != want {
			t.Fatalf("%s status=%d", path, resp.StatusCode)
		}
		var result jobs.Result
		if err = json.NewDecoder(resp.Body).Decode(&result); err != nil {
			t.Fatal(err)
		}
		return result
	}
	creation := map[string]any{"key": owner, "spec": spec}
	for i := 0; i < 2; i++ {
		if got := call("/api/instances", creation, 202); got.State != jobs.Submitted {
			t.Fatalf("create=%+v", got)
		}
	}
	if createCalls != 1 {
		t.Fatalf("created %d times", createCalls)
	}
	// Simulate process restart: reopen persistent state and rebuild HTTP handlers.
	plugin.Close()
	reopened, err := jobs.Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	creator.Store = reopened
	manager.Store = reopened
	plugin = httptest.NewServer(NewWithManagement("test-secret", func(context.Context) incus.Capability { return incus.Capability{Status: incus.Ready} }, nil, nil, manager, creator))
	if got := call("/api/instances/reconcile", creation, 200); got.State != jobs.Starting {
		t.Fatalf("start=%+v", got)
	}
	if got := call("/api/instances/reconcile", creation, 200); got.State != jobs.Completed {
		t.Fatalf("complete=%+v", got)
	}
	req, _ := http.NewRequest("GET", plugin.URL+"/api/managed", nil)
	req.Header.Set("X-LWT-Internal-Secret", "test-secret")
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	resp, err := plugin.Client().Do(req)
	if err != nil {
		t.Fatal(err)
	}
	var managed []workflow.ManagedInstance
	if err = json.NewDecoder(resp.Body).Decode(&managed); err != nil {
		t.Fatal(err)
	}
	resp.Body.Close()
	if resp.StatusCode != 200 || len(managed) != 1 || !managed[0].Verified || managed[0].Spec != spec {
		t.Fatalf("managed=%+v status=%d", managed, resp.StatusCode)
	}
	action := map[string]any{"key": actionKey, "owner": owner, "spec": spec, "action": "stop"}
	if got := call("/api/lifecycle", action, 202); got.State != jobs.Submitted {
		t.Fatalf("stop=%+v", got)
	}
	if got := call("/api/lifecycle/reconcile", action, 200); got.State != jobs.Completed || state != 102 {
		t.Fatalf("stop complete=%+v state=%d", got, state)
	}
}
