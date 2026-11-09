package incus

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
)

func TestClientScopesProjectAwareRequests(t *testing.T) {
	const project = "lwt-isolated"
	seen := map[string]int{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if got := r.URL.Query().Get("project"); got != project {
			t.Errorf("%s %s project query = %q, want %q", r.Method, r.URL.Path, got, project)
		}
		seen[r.Method+" "+r.URL.Path]++

		w.Header().Set("Content-Type", "application/json")
		switch r.Method + " " + r.URL.Path {
		case "GET /1.0/instances", "GET /1.0/images", "GET /1.0/storage-pools", "GET /1.0/networks":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":[]}`))
		case "GET /1.0/resources":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"cpu":{"total":8},"memory":{"total":1000,"used":10}}}`))
		case "GET /1.0/storage-pools/pool1/resources":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"space":{"total":1000,"used":10}}}`))
		case "POST /1.0/instances":
			var body map[string]any
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				t.Errorf("decode create body: %v", err)
			}
			w.WriteHeader(http.StatusAccepted)
			_, _ = w.Write([]byte(`{"type":"async","status_code":100,"operation":"/1.0/operations/op123"}`))
		case "PUT /1.0/instances/demo/state":
			w.WriteHeader(http.StatusAccepted)
			_, _ = w.Write([]byte(`{"type":"async","status_code":100,"operation":"/1.0/operations/op123"}`))
		case "GET /1.0/operations/op123":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"status_code":200,"err":""}}`))
		case "GET /1.0/instances/demo/state":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"status_code":103}}`))
		case "GET /1.0/instances/demo":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"name":"demo","type":"container","expanded_config":{"user.lwt.job":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","security.privileged":"false","security.nesting":"false","limits.cpu":"1","limits.memory":"256MiB","volatile.base_image":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"},"expanded_devices":{"root":{"type":"disk","path":"/","pool":"pool1","size":"1GiB"},"eth0":{"type":"nic","network":"bridge1"}}}}`))
		default:
			http.NotFound(w, r)
		}
	}))
	t.Cleanup(server.Close)

	client := Client{HTTP: server.Client(), Endpoint: server.URL, Project: project}
	if _, err := client.Discover(context.Background()); err != nil {
		t.Fatalf("Discover() error = %v", err)
	}
	if _, err := client.SampleResources(context.Background(), "pool1"); err != nil {
		t.Fatalf("SampleResources() error = %v", err)
	}
	spec := InstanceSpec{
		Name: "demo", ImageFingerprint: strings.Repeat("a", 64), CPU: 1,
		MemoryMiB: 256, DiskGiB: 1, StoragePool: "pool1", Network: "bridge1",
	}
	jobID := strings.Repeat("b", 32)
	if _, err := client.CreateOwned(context.Background(), spec, jobID); err != nil {
		t.Fatalf("CreateOwned() error = %v", err)
	}
	if _, err := client.ChangeState(context.Background(), "demo", "start"); err != nil {
		t.Fatalf("ChangeState() error = %v", err)
	}
	if _, err := client.Operation(context.Background(), "/1.0/operations/op123"); err != nil {
		t.Fatalf("Operation() error = %v", err)
	}
	if state, err := client.InstanceState(context.Background(), "demo"); err != nil || state != "Running" {
		t.Fatalf("InstanceState() = %q, %v; want Running", state, err)
	}
	if verified, err := client.VerifyCreated(context.Background(), spec, jobID); err != nil || !verified {
		t.Fatalf("VerifyCreated() = %v, %v; want true", verified, err)
	}

	for _, path := range []string{
		"GET /1.0/instances", "GET /1.0/images", "GET /1.0/storage-pools", "GET /1.0/networks",
		"GET /1.0/resources", "GET /1.0/storage-pools/pool1/resources", "POST /1.0/instances",
		"PUT /1.0/instances/demo/state", "GET /1.0/operations/op123",
		"GET /1.0/instances/demo/state", "GET /1.0/instances/demo",
	} {
		if seen[path] != 1 {
			t.Errorf("request %q count = %d, want 1", path, seen[path])
		}
	}
}
