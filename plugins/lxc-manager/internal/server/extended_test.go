package server

import (
	"bytes"
	"context"
	"encoding/json"
	"mime/multipart"
	"net/http"
	"net/http/httptest"
	"path/filepath"
	"testing"

	"lwt/lxc-manager/internal/driver"
	"lwt/lxc-manager/internal/evidence"
	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/network"
	"lwt/lxc-manager/internal/terminal"
)

func TestExtendedRoutes(t *testing.T) {
	secret := "test-secret"
	tmpDir := t.TempDir()
	netStore, err := network.OpenStore(filepath.Join(tmpDir, "net"))
	if err != nil {
		t.Fatalf("failed to open network store: %v", err)
	}

	termMgr := terminal.NewSessionManager()
	evProv := evidence.DefaultHostEvidenceProvider()
	mockDriver := driver.NewMockDriver()

	handler := NewServer(ServerOptions{
		Secret:   secret,
		Evidence: evProv,
		Terminal: termMgr,
		Network:  netStore,
		Driver:   mockDriver,
		Discover: func(ctx context.Context) (incus.Discovery, error) {
			return incus.Discovery{
				Pools: []incus.PoolInfo{
					{Name: "default", Driver: "dir", Status: "Created"},
				},
				Networks: []incus.NetworkInfo{
					{Name: "incusbr0"},
				},
				Images: []incus.ImageInfo{
					{Fingerprint: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"},
				},
			}, nil
		},
	})

	// 1. Templates GET
	req := httptest.NewRequest(http.MethodGet, "/api/templates", nil)
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	rec := httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("templates expected 200, got %d", rec.Code)
	}

	// 2. Preflight with evidence POST
	spec := incus.InstanceSpec{
		Name:             "test-node",
		ImageFingerprint: "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678",
		CPU:              1,
		MemoryMiB:        512,
		DiskGiB:          5,
		StoragePool:      "default",
		Network:          "incusbr0",
	}
	body, _ := json.Marshal(spec)
	req = httptest.NewRequest(http.MethodPost, "/api/preflight", bytes.NewReader(body))
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	req.Header.Set("Content-Type", "application/json")
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("preflight expected 200, got %d", rec.Code)
	}
	var pfRes incus.PreflightResult
	_ = json.NewDecoder(rec.Body).Decode(&pfRes)
	if !pfRes.Allowed {
		t.Errorf("preflight with real evidence expected allowed=true, reasons: %v", pfRes.Reasons)
	}

	// 3. Console exec POST
	execBody, _ := json.Marshal(map[string]string{
		"container": "test-node",
		"command":   "whoami",
	})
	req = httptest.NewRequest(http.MethodPost, "/api/console/exec", bytes.NewReader(execBody))
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	req.Header.Set("Content-Type", "application/json")
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("console exec expected 200, got %d", rec.Code)
	}

	// 4. Port mapping POST & GET
	mapBody, _ := json.Marshal(network.PortMapping{
		ID:          "p1",
		ContainerID: "test-node",
		Protocol:    "tcp",
		HostPort:    2222,
		TargetPort:  22,
		TargetIP:    "10.0.3.15",
		Enabled:     true,
	})
	req = httptest.NewRequest(http.MethodPost, "/api/network/mappings", bytes.NewReader(mapBody))
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	req.Header.Set("Content-Type", "application/json")
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("port map POST expected 200, got %d", rec.Code)
	}

	req = httptest.NewRequest(http.MethodGet, "/api/network/mappings?container=test-node", nil)
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("port map GET expected 200, got %d", rec.Code)
	}

	// 5. Host interfaces GET
	req = httptest.NewRequest(http.MethodGet, "/api/network/host-interfaces", nil)
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("host interfaces expected 200, got %d", rec.Code)
	}

	// 6. Storage pools GET
	req = httptest.NewRequest(http.MethodGet, "/api/storage/pools", nil)
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("storage pools expected 200, got %d", rec.Code)
	}

	// Create an instance in mockDriver for export and limits
	_, _ = mockDriver.CreateOwned(context.Background(), spec, "0123456789abcdef0123456789abcdef")

	// 7. Hardware limits PATCH
	limitsBody, _ := json.Marshal(map[string]any{
		"name": "test-node",
		"spec": driver.HardwareLimitsSpec{
			CpuCores:        2,
			CpuAllowancePct: 50,
			MaxProcesses:    256,
		},
	})
	req = httptest.NewRequest(http.MethodPatch, "/api/instances/hardware-limits", bytes.NewReader(limitsBody))
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("hardware limits expected 200, got %d, body: %s", rec.Code, rec.Body.String())
	}

	// 8. Export instance GET
	req = httptest.NewRequest(http.MethodGet, "/api/instances/export?name=test-node", nil)
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)
	if rec.Code != http.StatusOK {
		t.Fatalf("export expected 200, got %d", rec.Code)
	}
	if rec.Header().Get("Content-Type") != "application/gzip" {
		t.Errorf("expected application/gzip, got %s", rec.Header().Get("Content-Type"))
	}
	exportPayload := rec.Body.Bytes()

	// 9. Import instance POST (Multipart)
	mpBuf := new(bytes.Buffer)
	mpWriter := multipart.NewWriter(mpBuf)
	_ = mpWriter.WriteField("name", "imported-node")
	_ = mpWriter.WriteField("pool", "default")
	partWriter, _ := mpWriter.CreateFormFile("file", "imported-node.tar.gz")
	_, _ = partWriter.Write(exportPayload)
	_ = mpWriter.Close()

	req = httptest.NewRequest(http.MethodPost, "/api/instances/import", mpBuf)
	req.Header.Set("X-LWT-Internal-Secret", secret)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	req.Header.Set("Content-Type", mpWriter.FormDataContentType())
	rec = httptest.NewRecorder()
	handler.ServeHTTP(rec, req)
	if rec.Code != http.StatusCreated {
		t.Fatalf("import expected 201, got %d, body: %s", rec.Code, rec.Body.String())
	}
}
