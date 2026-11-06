package server

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
	"lwt/lxc-manager/internal/workflow"
)

type fakeCreator struct{ calls int }

type fakeManagement struct{ calls int }

func (f *fakeManagement) Pending(context.Context) ([]jobs.PendingTask, error) {
	f.calls++
	return []jobs.PendingTask{{Key: "0123456789abcdef0123456789abcdef", State: jobs.NeedsReconciliation}}, nil
}

func (f *fakeManagement) Managed(context.Context) ([]workflow.ManagedInstance, error) {
	f.calls++
	return []workflow.ManagedInstance{{Key: "0123456789abcdef0123456789abcdef", Verified: true}}, nil
}
func (f *fakeManagement) Submit(context.Context, string, string, incus.InstanceSpec, string) (jobs.Result, error) {
	f.calls++
	return jobs.Result{State: jobs.Submitted}, nil
}
func (f *fakeManagement) Reconcile(context.Context, string, string, incus.InstanceSpec, string) (jobs.Result, error) {
	f.calls++
	return jobs.Result{State: jobs.Completed}, nil
}

func TestManagedLifecycleRequiresAdminAndValidRequest(t *testing.T) {
	f := &fakeManagement{}
	h := NewWithManagement("test-secret", func(context.Context) incus.Capability { return incus.Capability{} }, nil, nil, f)
	key := "0123456789abcdef0123456789abcdef"
	body := `{"key":"` + key + `","owner":"` + key + `","spec":{"name":"demo"},"action":"stop"}`
	for _, tc := range []struct {
		method, path, auth, body string
		want                     int
	}{{"GET", "/api/managed", "ApiKey", "", 403}, {"GET", "/api/managed", "AdminSession", "", 200}, {"GET", "/api/tasks", "AdminSession", "", 200}, {"POST", "/api/lifecycle", "AdminSession", body, 202}, {"POST", "/api/lifecycle/reconcile", "AdminSession", body, 200}, {"POST", "/api/lifecycle", "AdminSession", `{"key":"bad"}`, 400}} {
		req := httptest.NewRequest(tc.method, tc.path, strings.NewReader(tc.body))
		req.Header.Set("X-LWT-Internal-Secret", "test-secret")
		req.Header.Set("X-LWT-Auth-Type", tc.auth)
		req.Header.Set("Content-Type", "application/json")
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		if rec.Code != tc.want {
			t.Fatalf("%s %s got=%d want=%d", tc.method, tc.path, rec.Code, tc.want)
		}
	}
	if f.calls != 4 {
		t.Fatalf("calls=%d", f.calls)
	}
}

func TestManagedLifecycleUsesPersistedOwnershipAndIdempotency(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	owner := "0123456789abcdef0123456789abcdef"
	actionKey := "fedcba9876543210fedcba9876543210"
	spec := incus.InstanceSpec{Name: "demo"}
	calls := 0
	creator := workflow.Creator{Store: store, Check: func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error) {
		return incus.PreflightResult{Allowed: true}, nil
	}, Create: func(context.Context, incus.InstanceSpec, string) (string, error) {
		return "/1.0/operations/create", nil
	}, Observe: func(context.Context, string) (incus.OperationStatus, error) { return incus.OperationSuccess, nil }, Verify: func(context.Context, incus.InstanceSpec, string) (bool, error) { return true, nil }, Start: func(context.Context, string) (string, error) { return "/1.0/operations/start", nil }, Running: func(context.Context, string) (bool, error) { return true, nil }}
	if _, err = creator.Submit(context.Background(), owner, spec); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 2; i++ {
		if _, err = creator.Reconcile(context.Background(), owner, spec); err != nil {
			t.Fatal(err)
		}
	}
	manager := workflow.Lifecycle{Store: store, Verify: func(context.Context, incus.InstanceSpec, string) (bool, error) { return true, nil }, Change: func(context.Context, string, string) (string, error) { calls++; return "/1.0/operations/stop", nil }, Observe: func(context.Context, string) (incus.OperationStatus, error) { return incus.OperationSuccess, nil }, State: func(context.Context, string) (string, error) { return "Stopped", nil }}
	h := NewWithManagement("test-secret", func(context.Context) incus.Capability { return incus.Capability{} }, nil, nil, manager)
	do := func(path string, body any) int {
		raw, _ := json.Marshal(body)
		req := httptest.NewRequest("POST", path, strings.NewReader(string(raw)))
		req.Header.Set("X-LWT-Internal-Secret", "test-secret")
		req.Header.Set("X-LWT-Auth-Type", "AdminSession")
		req.Header.Set("Content-Type", "application/json")
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		return rec.Code
	}
	body := map[string]any{"key": actionKey, "owner": owner, "spec": spec, "action": "stop"}
	for i := 0; i < 2; i++ {
		if status := do("/api/lifecycle", body); status != 202 {
			t.Fatalf("submit status=%d", status)
		}
	}
	if status := do("/api/lifecycle/reconcile", body); status != 200 {
		t.Fatalf("reconcile=%d", status)
	}
	if calls != 1 {
		t.Fatalf("backend calls=%d", calls)
	}
	body["key"] = "11111111111111111111111111111111"
	body["spec"] = incus.InstanceSpec{Name: "other"}
	if status := do("/api/lifecycle", body); status != 409 {
		t.Fatalf("foreign spec=%d", status)
	}
	if calls != 1 {
		t.Fatalf("unauthorized backend call: %d", calls)
	}
}

func TestResourceSampleIsAdminOnly(t *testing.T) {
	calls := 0
	h := NewWithResources("test-secret", func(context.Context) incus.Capability { return incus.Capability{} }, nil, func(_ context.Context, pool string) (incus.ResourceSample, error) {
		calls++
		if pool != "pool1" {
			t.Errorf("pool=%s", pool)
		}
		return incus.ResourceSample{CPUThreads: 8}, nil
	})
	for _, tc := range []struct {
		auth string
		want int
	}{{"ApiKey", 403}, {"AdminSession", 200}} {
		req := httptest.NewRequest("GET", "/api/resources?pool=pool1", nil)
		req.Header.Set("X-LWT-Internal-Secret", "test-secret")
		req.Header.Set("X-LWT-Auth-Type", tc.auth)
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		if rec.Code != tc.want {
			t.Fatalf("status=%d", rec.Code)
		}
	}
	if calls != 1 {
		t.Fatalf("calls=%d", calls)
	}
}

func (f *fakeCreator) Submit(_ context.Context, key string, _ incus.InstanceSpec) (jobs.Result, error) {
	f.calls++
	return jobs.Result{State: jobs.Submitted, Operation: "/1.0/operations/test"}, nil
}
func (f *fakeCreator) Reconcile(_ context.Context, key string, _ incus.InstanceSpec) (jobs.Result, error) {
	f.calls++
	return jobs.Result{State: jobs.Completed}, nil
}

func TestCreateAPIRequiresAdminAndReturnsTaskState(t *testing.T) {
	f := &fakeCreator{}
	h := New("test-secret", func(context.Context) incus.Capability { return incus.Capability{} }, nil, f)
	body := `{"key":"0123456789abcdef0123456789abcdef","spec":{"name":"demo"}}`
	for _, tc := range []struct {
		path, auth string
		want       int
	}{{"/api/instances", "ApiKey", 403}, {"/api/instances", "AdminSession", 202}, {"/api/instances/reconcile", "AdminSession", 200}} {
		req := httptest.NewRequest("POST", tc.path, strings.NewReader(body))
		req.Header.Set("X-LWT-Internal-Secret", "test-secret")
		req.Header.Set("X-LWT-Auth-Type", tc.auth)
		req.Header.Set("Content-Type", "application/json")
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		if rec.Code != tc.want {
			t.Fatalf("path=%s status=%d", tc.path, rec.Code)
		}
	}
	if f.calls != 2 {
		t.Fatalf("calls=%d", f.calls)
	}
}

func TestCreateAPIRejectsUnverifiedCapacityBeforeBackend(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	creator := workflow.Creator{Store: store, Check: func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error) {
		return incus.PreflightResult{Allowed: false, Reasons: []string{"capacity unverified"}}, nil
	}, Create: func(context.Context, incus.InstanceSpec, string) (string, error) { calls++; return "", nil }}
	h := New("test-secret", func(context.Context) incus.Capability { return incus.Capability{} }, nil, creator)
	req := httptest.NewRequest("POST", "/api/instances", strings.NewReader(`{"key":"0123456789abcdef0123456789abcdef","spec":{"name":"demo"}}`))
	req.Header.Set("X-LWT-Internal-Secret", "test-secret")
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	req.Header.Set("Content-Type", "application/json")
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	if rec.Code != 409 || calls != 0 {
		t.Fatalf("status=%d backend calls=%d", rec.Code, calls)
	}
	var body struct {
		Code string `json:"code"`
	}
	if err := json.NewDecoder(rec.Body).Decode(&body); err != nil || body.Code != "not_submitted" {
		t.Fatalf("body=%+v err=%v", body, err)
	}
}

func TestOnlyExplicitPreflightSubmitRejectionIsSafeToDiscard(t *testing.T) {
	for _, tc := range []struct {
		err    error
		submit bool
		safe   bool
	}{{workflow.ErrPreflight, true, true}, {workflow.ErrPreflight, false, false}, {jobs.ErrConflict, true, false}, {jobs.ErrAdmissionBusy, true, false}, {errors.New("network error"), true, false}} {
		rec := httptest.NewRecorder()
		writeTaskError(rec, tc.err, tc.submit)
		var body struct {
			Code string `json:"code"`
		}
		_ = json.Unmarshal(rec.Body.Bytes(), &body)
		if (body.Code == "not_submitted") != tc.safe {
			t.Fatalf("err=%v submit=%v response=%s", tc.err, tc.submit, rec.Body.String())
		}
	}
}

func TestPreflightPreviewDoesNotPermitUnverifiedQuota(t *testing.T) {
	h := New("test-secret", func(context.Context) incus.Capability { return incus.Capability{Status: incus.Ready} }, func(context.Context) (incus.Discovery, error) {
		return incus.Discovery{Images: []incus.ImageInfo{{Fingerprint: "abcdef123456"}}, Pools: []incus.PoolInfo{{Name: "pool1", Status: "Created"}}, Networks: []incus.NetworkInfo{{Name: "br0"}}}, nil
	})
	req := httptest.NewRequest(http.MethodPost, "/api/preflight", strings.NewReader(`{"name":"demo","imageFingerprint":"abcdef123456","cpu":1,"memoryMiB":512,"diskGiB":8,"storagePool":"pool1","network":"br0"}`))
	req.Header.Set("X-LWT-Internal-Secret", "test-secret")
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	req.Header.Set("Content-Type", "application/json")
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	var result incus.PreflightResult
	if rec.Code != 200 || json.NewDecoder(rec.Body).Decode(&result) != nil || result.Allowed {
		t.Fatalf("code=%d result=%+v", rec.Code, result)
	}
}

func TestDiscoveryRequiresAdminAndSurfacesBackendErrors(t *testing.T) {
	calls := 0
	h := New("test-secret", func(context.Context) incus.Capability { return incus.Capability{Status: incus.Ready} }, func(context.Context) (incus.Discovery, error) {
		calls++
		return incus.Discovery{}, errors.New("backend unavailable")
	})
	for _, tc := range []struct {
		auth   string
		status int
	}{{"ApiKey", 403}, {"AdminSession", 502}} {
		req := httptest.NewRequest(http.MethodGet, "/api/discovery", nil)
		req.Header.Set("X-LWT-Internal-Secret", "test-secret")
		req.Header.Set("X-LWT-Auth-Type", tc.auth)
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		if rec.Code != tc.status {
			t.Fatalf("status=%d", rec.Code)
		}
	}
	if calls != 1 {
		t.Fatalf("calls=%d", calls)
	}
}

func TestCapabilitiesRequireInternalSecretAndAdmin(t *testing.T) {
	calls := 0
	h := New("test-secret", func(context.Context) incus.Capability { calls++; return incus.Capability{Status: incus.RequiresSetup} }, nil)
	for _, tc := range []struct {
		secret, auth string
		want         int
	}{
		{"", "AdminSession", 401}, {"wrong", "AdminSession", 401}, {"test-secret", "ApiKey", 403}, {"test-secret", "AdminSession", 200},
	} {
		req := httptest.NewRequest(http.MethodGet, "/api/capabilities", nil)
		req.Header.Set("X-LWT-Internal-Secret", tc.secret)
		req.Header.Set("X-LWT-Auth-Type", tc.auth)
		rec := httptest.NewRecorder()
		h.ServeHTTP(rec, req)
		if rec.Code != tc.want {
			t.Fatalf("auth %q status=%d want=%d", tc.auth, rec.Code, tc.want)
		}
		if tc.want == 200 {
			var got incus.Capability
			if err := json.NewDecoder(rec.Body).Decode(&got); err != nil || got.Status != incus.RequiresSetup {
				t.Fatalf("result=%+v err=%v", got, err)
			}
		}
	}
	if calls != 1 {
		t.Fatalf("backend calls=%d", calls)
	}
}

func TestNoSecretFailsClosedAndWritesDisabled(t *testing.T) {
	h := New("", func(context.Context) incus.Capability { t.Fatal("probe called"); return incus.Capability{} }, nil)
	req := httptest.NewRequest(http.MethodGet, "/api/capabilities", nil)
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	rec := httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	if rec.Code != 401 {
		t.Fatalf("empty secret status=%d", rec.Code)
	}
	h = New("test-secret", func(context.Context) incus.Capability { t.Fatal("probe called"); return incus.Capability{} }, nil)
	req = httptest.NewRequest(http.MethodPost, "/api/instances", nil)
	req.Header.Set("X-LWT-Internal-Secret", "test-secret")
	req.Header.Set("X-LWT-Auth-Type", "AdminSession")
	rec = httptest.NewRecorder()
	h.ServeHTTP(rec, req)
	if rec.Code != 404 {
		t.Fatalf("write status=%d", rec.Code)
	}
}
