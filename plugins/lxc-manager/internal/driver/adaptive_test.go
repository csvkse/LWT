package driver

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"

	"lwt/lxc-manager/internal/incus"
)

func TestAdaptiveDriver_InitialSandboxFallback(t *testing.T) {
	adapter := incus.Client{
		HTTP:     http.DefaultClient,
		Endpoint: "http://invalid-unreachable-incus-test-host",
	}

	adaptive := NewAdaptiveDriver(adapter, false)
	defer adaptive.StopObserver()

	if adaptive.IsReal() {
		t.Fatal("expected mock driver to be active when daemon unreachable")
	}

	cap := adaptive.Probe(context.Background())
	if cap.Status != incus.Ready {
		t.Fatalf("expected sandbox driver to return Ready capability, got %v", cap.Status)
	}

	if adaptive.Name() != "mock-sandbox" {
		t.Fatalf("unexpected driver name: %s", adaptive.Name())
	}
}

func TestAdaptiveDriver_HotSwapToReal(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/1.0" {
			w.Header().Set("Content-Type", "application/json")
			_, _ = w.Write([]byte(`{
				"type": "sync",
				"status": "Success",
				"status_code": 200,
				"metadata": {
					"api_status": "stable",
					"api_version": "1.0",
					"auth": "trusted",
					"environment": {
						"server_version": "6.0.4"
					}
				}
			}`))
			return
		}
		http.NotFound(w, r)
	}))
	defer server.Close()

	adapter := incus.Client{
		HTTP:     server.Client(),
		Endpoint: server.URL,
	}

	adaptive := NewAdaptiveDriver(adapter, false)
	if adaptive.IsReal() {
		t.Fatal("expected initial driver to be mock")
	}

	// Trigger recheck
	adaptive.recheck()

	if !adaptive.IsReal() {
		t.Fatal("expected adaptive driver to hot-swap to real Incus driver")
	}

	if adaptive.Name() != "incus" {
		t.Fatalf("unexpected driver name after hot-swap: %s", adaptive.Name())
	}

	cap := adaptive.Probe(context.Background())
	if cap.Version != "6.0.4" {
		t.Fatalf("expected version 6.0.4, got %s", cap.Version)
	}
}
