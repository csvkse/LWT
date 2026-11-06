package incus

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestProbeTrustedServer(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/1.0" || r.Method != http.MethodGet {
			t.Errorf("unexpected request: %s %s", r.Method, r.URL.Path)
			w.WriteHeader(404)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"auth":"trusted","environment":{"server_version":"6.0"},"api_extensions":["network_forward"]}}`))
	}))
	defer server.Close()
	got := Probe(context.Background(), server.Client(), server.URL)
	if got.Status != Ready || got.Version != "6.0" || !got.Extensions["network_forward"] {
		t.Fatalf("unexpected result: %+v", got)
	}
}

func TestProbeUntrustedOrUnavailable(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":{"auth":"untrusted"}}`))
	}))
	defer server.Close()
	if got := Probe(context.Background(), server.Client(), server.URL); got.Status != RequiresSetup {
		t.Fatalf("untrusted: %+v", got)
	}
	server.Close()
	if got := Probe(context.Background(), server.Client(), server.URL); got.Status != Unknown {
		t.Fatalf("unavailable: %+v", got)
	}
}
