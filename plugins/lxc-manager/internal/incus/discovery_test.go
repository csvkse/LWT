package incus

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestDiscoverFiltersAndProjectsResults(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		expectedRecursion := "1"
		if r.URL.Path == "/1.0/instances" {
			expectedRecursion = "2"
		}
		if r.Method != "GET" || r.URL.Query().Get("recursion") != expectedRecursion {
			t.Errorf("unexpected request: %s (expected recursion %s)", r.URL.String(), expectedRecursion)
		}
		var payload string
		switch r.URL.Path {
		case "/1.0/instances":
			payload = `[
				{
					"name":"c1",
					"type":"container",
					"status":"Running",
					"expanded_config":{"limits.cpu":"2","limits.memory":"1024MiB"},
					"expanded_devices":{"root":{"pool":"pool1","size":"10GiB"},"eth0":{"type":"nic","network":"br0"}},
					"state":{"network":{"eth0":{"hwaddr":"10:66:6a:11:22:33","addresses":[{"address":"10.0.3.50","family":"inet","scope":"global"},{"address":"fd42::50","family":"inet6","scope":"global"}]}}}
				},
				{
					"name":"c2",
					"type":"container",
					"status":"Running",
					"expanded_config":{"limits.cpu":"1","limits.memory":"512MiB"},
					"expanded_devices":{"root":{"pool":"pool1","size":"5GiB"},"eth0":{"type":"nic","network":"br0"}},
					"state":{"network":{"eth0":{"hwaddr":"10:66:6a:44:55:66","addresses":[{"address":"10.0.3.51","family":"inet","scope":"global"},{"address":"fd42::51","family":"inet6","scope":"global"}]}}}
				},
				{
					"name":"c3",
					"type":"container",
					"status":"Running",
					"expanded_config":{"limits.cpu":"2","limits.memory":"512MiB"},
					"expanded_devices":{"root":{"pool":"pool1","size":"8GiB"},"eth0":{"type":"nic","nictype":"macvlan","parent":"ens2"}},
					"state":{"network":{"eth0":{"hwaddr":"10:66:6a:77:88:99","addresses":[{"address":"10.126.1.116","family":"inet","scope":"global"}]}}}
				},
				{"name":"vm1","type":"virtual-machine"}
			]`
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
	if len(got.Instances) != 3 || got.Instances[0].Name != "c1" || len(got.Images) != 1 || got.Images[0].Fingerprint != "abcdef123456" || len(got.Pools) != 1 || got.Pools[0].Driver != "zfs" || len(got.Networks) != 1 || got.Networks[0].Name != "br0" {
		t.Fatalf("result=%+v", got)
	}
	// Assert multi-instance IP extraction & uniqueness
	c1 := got.Instances[0]
	c2 := got.Instances[1]
	c3 := got.Instances[2]
	if c1.IPv4 != "10.0.3.50" || c2.IPv4 != "10.0.3.51" || c3.IPv4 != "10.126.1.116" {
		t.Fatalf("expected distinct IPs, got c1=%s, c2=%s, c3=%s", c1.IPv4, c2.IPv4, c3.IPv4)
	}
	if c1.IPv4 == c2.IPv4 || c1.IPv4 == c3.IPv4 {
		t.Fatalf("GATE VIOLATION: multiple running instances must not have identical IPs: %s, %s, %s", c1.IPv4, c2.IPv4, c3.IPv4)
	}
	if c1.CPU != "2" || c1.Memory != "1024MiB" || c1.Disk != "10GiB" || c1.StoragePool != "pool1" || c1.NetworkName != "br0" {
		t.Fatalf("unexpected metadata projection for c1: %+v", c1)
	}
	if c3.NetworkName != "macvlan:ens2" {
		t.Fatalf("GATE VIOLATION: expected c3 NetworkName to be macvlan:ens2, got %s", c3.NetworkName)
	}
	if len(got.OccupiedNames) != 4 || got.OccupiedNames[3] != "vm1" {
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
