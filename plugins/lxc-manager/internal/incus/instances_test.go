package incus

import (
	"context"
	"encoding/json"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestCreateInstanceUsesConfinedConfiguration(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodPost || r.URL.Path != "/1.0/instances" {
			t.Errorf("unexpected request %s %s", r.Method, r.URL.Path)
			w.WriteHeader(404)
			return
		}
		var body struct {
			Name    string                       `json:"name"`
			Type    string                       `json:"type"`
			Config  map[string]string            `json:"config"`
			Devices map[string]map[string]string `json:"devices"`
			Source  map[string]string            `json:"source"`
		}
		if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
			t.Fatal(err)
		}
		if body.Name != "demo-1" || body.Type != "container" || body.Config["user.lwt.job"] != "0123456789abcdef0123456789abcdef" || body.Config["security.privileged"] != "false" || body.Config["limits.cpu"] != "2" || body.Config["limits.memory"] != "512MiB" || body.Devices["root"]["pool"] != "pool1" || body.Devices["root"]["size"] != "8GiB" || body.Devices["eth0"]["network"] != "bridge1" || body.Source["fingerprint"] != "abcdef123456" {
			t.Errorf("unexpected payload: %+v", body)
		}
		w.WriteHeader(http.StatusAccepted)
		_, _ = w.Write([]byte(`{"type":"async","status_code":100,"operation":"/1.0/operations/op123"}`))
	}))
	defer server.Close()
	client := Client{HTTP: server.Client(), Endpoint: server.URL}
	op, err := client.CreateOwned(context.Background(), InstanceSpec{Name: "demo-1", ImageFingerprint: "abcdef123456", CPU: 2, MemoryMiB: 512, DiskGiB: 8, StoragePool: "pool1", Network: "bridge1"}, "0123456789abcdef0123456789abcdef")
	if err != nil || op != "/1.0/operations/op123" {
		t.Fatalf("operation=%q err=%v", op, err)
	}
}

func TestCreateRejectsInvalidSpecsBeforeContact(t *testing.T) {
	calls := 0
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { calls++; w.WriteHeader(500) }))
	defer server.Close()
	client := Client{HTTP: server.Client(), Endpoint: server.URL}
	for _, spec := range []InstanceSpec{{}, {Name: "../bad", ImageFingerprint: "abcdef123456", CPU: 1, MemoryMiB: 256, DiskGiB: 1, StoragePool: "pool", Network: "net"}, {Name: "ok", ImageFingerprint: "images:latest", CPU: 1, MemoryMiB: 256, DiskGiB: 1, StoragePool: "pool", Network: "net"}} {
		if _, err := client.CreateOwned(context.Background(), spec, "0123456789abcdef0123456789abcdef"); err == nil {
			t.Fatalf("accepted invalid spec: %+v", spec)
		}
	}
	if calls != 0 {
		t.Fatalf("invalid requests contacted backend %d times", calls)
	}
}

func TestCreateRejectsUnexpectedOperation(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.WriteHeader(202)
		_, _ = w.Write([]byte(`{"type":"async","status_code":100,"operation":"https://example.com/steal"}`))
	}))
	defer server.Close()
	client := Client{HTTP: server.Client(), Endpoint: server.URL}
	_, err := client.CreateOwned(context.Background(), InstanceSpec{Name: "demo", ImageFingerprint: "abcdef123456", CPU: 1, MemoryMiB: 256, DiskGiB: 1, StoragePool: "pool", Network: "net"}, "0123456789abcdef0123456789abcdef")
	if err == nil {
		t.Fatal("accepted external operation URL")
	}
}

func TestStateChangeAndOperationResult(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.Path {
		case "/1.0/instances/demo/state":
			if r.Method != http.MethodPut {
				t.Errorf("unexpected method: %s", r.Method)
			}
			var payload struct {
				Action string `json:"action"`
				Force  bool   `json:"force"`
			}
			if err := json.NewDecoder(r.Body).Decode(&payload); err != nil || payload.Action != "start" || payload.Force {
				t.Errorf("unexpected state request: %+v, %v", payload, err)
			}
			w.WriteHeader(http.StatusAccepted)
			_, _ = w.Write([]byte(`{"type":"async","status_code":100,"operation":"/1.0/operations/op123"}`))
		case "/1.0/operations/op123":
			_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"status_code":200,"err":""}}`))
		default:
			t.Errorf("unexpected path: %s", r.URL.Path)
			w.WriteHeader(404)
		}
	}))
	defer server.Close()
	client := Client{HTTP: server.Client(), Endpoint: server.URL}
	op, err := client.ChangeState(context.Background(), "demo", "start")
	if err != nil {
		t.Fatal(err)
	}
	result, err := client.Operation(context.Background(), op)
	if err != nil || result != OperationSuccess {
		t.Fatalf("result=%s err=%v", result, err)
	}
	if _, err := client.ChangeState(context.Background(), "../bad", "start"); err == nil {
		t.Fatal("invalid name accepted")
	}
	if _, err := client.ChangeState(context.Background(), "demo", "delete"); err == nil {
		t.Fatal("invalid action accepted")
	}
}

func TestOperationFailureIsNotSuccess(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"status_code":400,"err":"backend failure"}}`))
	}))
	defer server.Close()
	client := Client{HTTP: server.Client(), Endpoint: server.URL}
	result, err := client.Operation(context.Background(), "/1.0/operations/op123")
	if err != nil || result != OperationFailure {
		t.Fatalf("result=%s err=%v", result, err)
	}
	if _, err := client.Operation(context.Background(), "https://example.com/steal"); err == nil {
		t.Fatal("external operation accepted")
	}
}

func TestVerifyCreatedRequiresOwnerAndEffectiveResources(t *testing.T) {
	owner := "0123456789abcdef0123456789abcdef"
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/1.0/instances/demo" || r.Method != "GET" {
			t.Errorf("unexpected request %s %s", r.Method, r.URL.Path)
		}
		_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"name":"demo","type":"container","expanded_config":{"user.lwt.job":"` + owner + `","security.privileged":"false","security.nesting":"false","limits.cpu":"2","limits.memory":"512MiB","volatile.base_image":"abcdef123456"},"expanded_devices":{"root":{"type":"disk","path":"/","pool":"pool1","size":"8GiB"},"eth0":{"type":"nic","network":"br0"}}}}`))
	}))
	defer server.Close()
	c := Client{HTTP: server.Client(), Endpoint: server.URL}
	spec := InstanceSpec{Name: "demo", ImageFingerprint: "abcdef123456", CPU: 2, MemoryMiB: 512, DiskGiB: 8, StoragePool: "pool1", Network: "br0"}
	if ok, err := c.VerifyCreated(context.Background(), spec, owner); err != nil || !ok {
		t.Fatalf("ok=%v err=%v", ok, err)
	}
	if ok, err := c.VerifyCreated(context.Background(), spec, "fedcba9876543210fedcba9876543210"); err != nil || ok {
		t.Fatalf("wrong owner ok=%v err=%v", ok, err)
	}
	spec.MemoryMiB = 1024
	if ok, err := c.VerifyCreated(context.Background(), spec, owner); err != nil || ok {
		t.Fatalf("wrong memory ok=%v err=%v", ok, err)
	}
}

func TestRunningRequiresExplicitIncusRunningStatus(t *testing.T) {
	code := 103
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/1.0/instances/demo/state" {
			t.Errorf("path=%s", r.URL.Path)
		}
		_ = json.NewEncoder(w).Encode(map[string]any{"type": "sync", "status_code": 200, "metadata": map[string]any{"status_code": code}})
	}))
	defer server.Close()
	c := Client{HTTP: server.Client(), Endpoint: server.URL}
	if ok, err := c.IsRunning(context.Background(), "demo"); err != nil || !ok {
		t.Fatalf("ok=%v err=%v", ok, err)
	}
	code = 102
	if ok, err := c.IsRunning(context.Background(), "demo"); err != nil || ok {
		t.Fatalf("stopped ok=%v err=%v", ok, err)
	}
	if state, err := c.InstanceState(context.Background(), "demo"); err != nil || state != "Stopped" {
		t.Fatalf("state=%s err=%v", state, err)
	}
	code = 107
	if state, err := c.InstanceState(context.Background(), "demo"); err != nil || state != "Transitioning" {
		t.Fatalf("state=%s err=%v", state, err)
	}
}
