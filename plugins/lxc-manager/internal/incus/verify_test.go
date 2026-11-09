package incus

import (
	"context"
	"net/http"
	"net/http/httptest"
	"testing"
)

func TestVerifyCreated_MacvlanAndBridged(t *testing.T) {
	jobID := "0123456789abcdef0123456789abcdef"

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/1.0/instances/macvlan-ct" {
			w.Write([]byte(`{
				"type": "sync",
				"status_code": 200,
				"metadata": {
					"name": "macvlan-ct",
					"type": "container",
					"expanded_config": {
						"user.lwt.job": "0123456789abcdef0123456789abcdef",
						"security.privileged": "false",
						"security.nesting": "false",
						"limits.cpu": "2",
						"limits.memory": "512MiB",
						"volatile.base_image": "0123456789abcdef0123456789abcdef"
					},
					"expanded_devices": {
						"root": {"type": "disk", "path": "/", "pool": "default", "size": "8GiB"},
						"eth0": {"type": "nic", "nictype": "macvlan", "parent": "ens2"}
					}
				}
			}`))
			return
		}
		if r.URL.Path == "/1.0/instances/bridged-ct" {
			w.Write([]byte(`{
				"type": "sync",
				"status_code": 200,
				"metadata": {
					"name": "bridged-ct",
					"type": "container",
					"expanded_config": {
						"user.lwt.job": "0123456789abcdef0123456789abcdef",
						"security.privileged": "false",
						"security.nesting": "false",
						"limits.cpu": "1",
						"limits.memory": "1024MiB",
						"volatile.base_image": "0123456789abcdef0123456789abcdef"
					},
					"expanded_devices": {
						"root": {"type": "disk", "path": "/", "pool": "default", "size": "10GiB"},
						"eth0": {"type": "nic", "network": "incusbr0"}
					}
				}
			}`))
			return
		}
		http.NotFound(w, r)
	}))
	defer server.Close()

	client := Client{HTTP: server.Client(), Endpoint: server.URL}

	// 1. Verify Macvlan instance
	specMacvlan := InstanceSpec{
		Name:             "macvlan-ct",
		ImageFingerprint: "0123456789abcdef0123456789abcdef",
		CPU:              2,
		MemoryMiB:        512,
		DiskGiB:          8,
		StoragePool:      "default",
		Network:          "macvlan:ens2",
	}
	ok, err := client.VerifyCreated(context.Background(), specMacvlan, jobID)
	if err != nil || !ok {
		t.Fatalf("expected macvlan verification to succeed, got ok=%v, err=%v", ok, err)
	}

	// 2. Verify Bridged instance
	specBridged := InstanceSpec{
		Name:             "bridged-ct",
		ImageFingerprint: "0123456789abcdef0123456789abcdef",
		CPU:              1,
		MemoryMiB:        1024,
		DiskGiB:          10,
		StoragePool:      "default",
		Network:          "incusbr0",
	}
	ok2, err2 := client.VerifyCreated(context.Background(), specBridged, jobID)
	if err2 != nil || !ok2 {
		t.Fatalf("expected bridged verification to succeed, got ok=%v, err=%v", ok2, err2)
	}

	// 3. Mismatched network must fail verification
	specMismatched := specMacvlan
	specMismatched.Network = "incusbr0"
	ok3, err3 := client.VerifyCreated(context.Background(), specMismatched, jobID)
	if err3 != nil || ok3 {
		t.Fatalf("expected mismatched network verification to fail, got ok=%v, err=%v", ok3, err3)
	}
}
