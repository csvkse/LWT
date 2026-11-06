package incus

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestDiscoverFiltersAndProjectsResults(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method != "GET" || r.URL.Query().Get("recursion") != "1" {
			t.Errorf("unexpected request: %s", r.URL.String())
		}
		var payload string
		switch r.URL.Path {
		case "/1.0/instances":
			payload = `[{"name":"c1","type":"container","status":"Running","config":{"security.privileged":"false"}},{"name":"vm1","type":"virtual-machine"}]`
		case "/1.0/images":
			payload = `[{"fingerprint":"abcdef123456","type":"container","architecture":"x86_64","properties":{"description":"Debian"}},{"fingerprint":"999999999999","type":"virtual-machine"}]`
		case "/1.0/storage-pools":
			payload = `[{"name":"pool1","driver":"zfs","status":"Created"}]`
		case "/1.0/networks":
			payload = `[{"name":"br0","type":"bridge","managed":true},{"name":"eth0","type":"physical","managed":false}]`
		default:
			t.Errorf("unexpected path %s", r.URL.Path)
			w.WriteHeader(404)
			return
		}
		_, _ = w.Write([]byte(`{"type":"sync","status_code":200,"metadata":` + payload + `}`))
	}))
	defer server.Close()
	c := Client{HTTP: server.Client(), Endpoint: server.URL}
	got, err := c.Discover(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if len(got.Instances) != 1 || got.Instances[0].Name != "c1" || len(got.Images) != 1 || got.Images[0].Fingerprint != "abcdef123456" || len(got.Pools) != 1 || got.Pools[0].Driver != "zfs" || len(got.Networks) != 1 || got.Networks[0].Name != "br0" {
		t.Fatalf("result=%+v", got)
	}
	if len(got.OccupiedNames) != 2 || got.OccupiedNames[1] != "vm1" {
		t.Fatalf("missing VM collision: %+v", got.OccupiedNames)
	}
}

func TestDiscoverFailureIsNotEmptySuccess(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { http.Error(w, "denied", http.StatusForbidden) }))
	defer server.Close()
	_, err := (Client{HTTP: server.Client(), Endpoint: server.URL}).Discover(context.Background())
	if err == nil {
		t.Fatal("backend failure treated as empty success")
	}
}
