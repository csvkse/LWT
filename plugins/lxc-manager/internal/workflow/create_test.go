package workflow

import (
	"context"
	"encoding/json"
	"errors"
	"sync/atomic"
	"testing"

	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
)

func TestConcurrentBrowserRetriesSubmitOnlyOnce(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	started := make(chan struct{})
	release := make(chan struct{})
	done := make(chan error, 1)
	var calls atomic.Int32
	c := Creator{Store: store, Check: func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error) {
		return incus.PreflightResult{Allowed: true}, nil
	}, Create: func(context.Context, incus.InstanceSpec, string) (string, error) {
		calls.Add(1)
		close(started)
		<-release
		return "/1.0/operations/abc-123", nil
	}}
	spec := incus.InstanceSpec{Name: "demo"}
	go func() { _, err := c.Submit(context.Background(), key, spec); done <- err }()
	<-started
	result, err := c.Submit(context.Background(), key, spec)
	if err != nil || result.State != jobs.NeedsReconciliation {
		t.Fatalf("retry=%+v err=%v", result, err)
	}
	close(release)
	if err := <-done; err != nil {
		t.Fatal(err)
	}
	if calls.Load() != 1 {
		t.Fatalf("backend calls=%d", calls.Load())
	}
}

const key = "0123456789abcdef0123456789abcdef"
const otherKey = "ffffffffffffffffffffffffffffffff"

func TestCreateRejectsFailedPreflightWithoutCallingBackend(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	c := Creator{Store: store, Check: func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error) {
		return incus.PreflightResult{Reasons: []string{"quota unknown"}}, nil
	}, Create: func(context.Context, incus.InstanceSpec, string) (string, error) { calls++; return "", nil }}
	_, err = c.Submit(context.Background(), key, incus.InstanceSpec{Name: "demo"})
	if !errors.Is(err, ErrPreflight) || calls != 0 {
		t.Fatalf("err=%v calls=%d", err, calls)
	}
	won, err := store.AcquireAdmission(otherKey)
	if err != nil || !won {
		t.Fatalf("admission not released: %v %v", won, err)
	}
}

func TestCreateRetriesReturnStoredOperationAndKeepAdmission(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	checks, calls := 0, 0
	c := Creator{Store: store, Check: func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error) {
		checks++
		return incus.PreflightResult{Allowed: true}, nil
	}, Create: func(_ context.Context, _ incus.InstanceSpec, owner string) (string, error) {
		if owner != key {
			t.Errorf("owner=%s", owner)
		}
		calls++
		return "/1.0/operations/abc-123", nil
	}}
	spec := incus.InstanceSpec{Name: "demo"}
	for i := 0; i < 2; i++ {
		r, err := c.Submit(context.Background(), key, spec)
		if err != nil || r.State != jobs.Submitted {
			t.Fatalf("result=%+v err=%v", r, err)
		}
	}
	if checks != 1 || calls != 1 {
		t.Fatalf("checks=%d calls=%d", checks, calls)
	}
	_, err = c.Submit(context.Background(), otherKey, spec)
	if !errors.Is(err, jobs.ErrAdmissionBusy) {
		t.Fatalf("err=%v", err)
	}
	_, err = c.Submit(context.Background(), key, incus.InstanceSpec{Name: "different"})
	if !errors.Is(err, jobs.ErrConflict) {
		t.Fatalf("err=%v", err)
	}
}

func TestCreatePersistsSpecBeforeBackendCall(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	c := Creator{Store: store, Check: func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error) {
		return incus.PreflightResult{Allowed: true}, nil
	}, Create: func(_ context.Context, spec incus.InstanceSpec, owner string) (string, error) {
		raw, err := store.Details(owner)
		if err != nil {
			t.Errorf("missing details: %v", err)
		}
		var got incus.InstanceSpec
		if err = json.Unmarshal(raw, &got); err != nil || got != spec {
			t.Errorf("details=%s err=%v", raw, err)
		}
		return "/1.0/operations/create", nil
	}}
	if _, err = c.Submit(context.Background(), key, incus.InstanceSpec{Name: "demo"}); err != nil {
		t.Fatal(err)
	}
}

func TestCreateAmbiguousFailureDoesNotRetry(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	calls := 0
	c := Creator{Store: store, Check: func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error) {
		return incus.PreflightResult{Allowed: true}, nil
	}, Create: func(context.Context, incus.InstanceSpec, string) (string, error) {
		calls++
		return "", errors.New("lost reply")
	}}
	for i := 0; i < 2; i++ {
		r, err := c.Submit(context.Background(), key, incus.InstanceSpec{Name: "demo"})
		if err != nil || r.State != jobs.NeedsReconciliation {
			t.Fatalf("result=%+v err=%v", r, err)
		}
	}
	if calls != 1 {
		t.Fatalf("calls=%d", calls)
	}
}

func TestReconcileReleasesOnlyAfterSuccessAndMatchingInstance(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	status := incus.OperationPending
	verified := false
	running := false
	starts := 0
	c := Creator{Store: store, Check: func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error) {
		return incus.PreflightResult{Allowed: true}, nil
	}, Create: func(context.Context, incus.InstanceSpec, string) (string, error) {
		return "/1.0/operations/abc-123", nil
	}, Observe: func(context.Context, string) (incus.OperationStatus, error) { return status, nil }, Verify: func(context.Context, incus.InstanceSpec, string) (bool, error) { return verified, nil }, Start: func(context.Context, string) (string, error) { starts++; return "/1.0/operations/start-123", nil }, Running: func(context.Context, string) (bool, error) { return running, nil }}
	spec := incus.InstanceSpec{Name: "demo"}
	if _, err = c.Submit(context.Background(), key, spec); err != nil {
		t.Fatal(err)
	}
	for _, phase := range []struct {
		state    incus.OperationStatus
		verified bool
		want     jobs.State
	}{{incus.OperationPending, false, jobs.Submitted}, {incus.OperationFailure, false, jobs.NeedsReconciliation}, {incus.OperationSuccess, false, jobs.NeedsReconciliation}, {incus.OperationSuccess, true, jobs.Starting}} {
		status = phase.state
		verified = phase.verified
		got, err := c.Reconcile(context.Background(), key, spec)
		if err != nil || got.State != phase.want {
			t.Fatalf("result=%+v err=%v", got, err)
		}
		if phase.want != jobs.Completed {
			if _, err = store.AcquireAdmission(otherKey); !errors.Is(err, jobs.ErrAdmissionBusy) {
				t.Fatalf("admission released: %v", err)
			}
		}
	}
	if starts != 1 {
		t.Fatalf("starts=%d", starts)
	}
	got, err := c.Reconcile(context.Background(), key, spec)
	if err != nil || got.State != jobs.Starting {
		t.Fatalf("not running: %+v %v", got, err)
	}
	running = true
	got, err = c.Reconcile(context.Background(), key, spec)
	if err != nil || got.State != jobs.Completed {
		t.Fatalf("running: %+v %v", got, err)
	}
	if won, err := store.AcquireAdmission(otherKey); err != nil || !won {
		t.Fatalf("admission not released: %v", err)
	}
	got, err = c.Reconcile(context.Background(), key, spec)
	if err != nil || got.State != jobs.Completed {
		t.Fatalf("repeat result=%+v err=%v", got, err)
	}
	if _, err = store.AcquireAdmission(key); !errors.Is(err, jobs.ErrAdmissionBusy) {
		t.Fatalf("new admission removed: %v", err)
	}
}

func TestLostStartResponseNeverReplaysStart(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	starts := 0
	c := Creator{Store: store, Check: func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error) {
		return incus.PreflightResult{Allowed: true}, nil
	}, Create: func(context.Context, incus.InstanceSpec, string) (string, error) {
		return "/1.0/operations/create", nil
	}, Observe: func(context.Context, string) (incus.OperationStatus, error) { return incus.OperationSuccess, nil }, Verify: func(context.Context, incus.InstanceSpec, string) (bool, error) { return true, nil }, Start: func(context.Context, string) (string, error) { starts++; return "", errors.New("reply lost") }, Running: func(context.Context, string) (bool, error) { return false, nil }}
	spec := incus.InstanceSpec{Name: "demo"}
	if _, err = c.Submit(context.Background(), key, spec); err != nil {
		t.Fatal(err)
	}
	for i := 0; i < 2; i++ {
		got, err := c.Reconcile(context.Background(), key, spec)
		if err != nil || got.State != jobs.NeedsReconciliation {
			t.Fatalf("result=%+v err=%v", got, err)
		}
	}
	if starts != 1 {
		t.Fatalf("starts=%d", starts)
	}
	if _, err = store.AcquireAdmission(otherKey); !errors.Is(err, jobs.ErrAdmissionBusy) {
		t.Fatalf("admission lost: %v", err)
	}
}
