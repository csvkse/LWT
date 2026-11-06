package workflow

import (
	"context"
	"encoding/json"
	"errors"
	"testing"

	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
)

func TestManagedListRestoresCompletedRecordsAndDetectsDrift(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	spec := incus.InstanceSpec{Name: "demo"}
	hash, _ := specHash(spec)
	_, _ = store.Reserve(key, hash)
	raw, _ := json.Marshal(spec)
	_ = store.RecordDetails(key, raw)
	_ = store.RecordOperation(key, "/1.0/operations/create")
	_ = store.RecordCompleted(key, "/1.0/operations/create")
	l := Lifecycle{Store: store, Verify: func(context.Context, incus.InstanceSpec, string) (bool, error) { return false, nil }}
	items, err := l.Managed(context.Background())
	if err != nil || len(items) != 1 || items[0].Key != key || items[0].Spec.Name != "demo" || items[0].Verified {
		t.Fatalf("items=%+v err=%v", items, err)
	}
}

func TestLifecycleOnlyOperatesOnVerifiedOwnedInstance(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	spec := incus.InstanceSpec{Name: "demo"}
	calls := 0
	verified := false
	l := Lifecycle{Store: store, Verify: func(context.Context, incus.InstanceSpec, string) (bool, error) { return verified, nil }, Change: func(context.Context, string, string) (string, error) { calls++; return "/1.0/operations/stop-1", nil }, Observe: func(context.Context, string) (incus.OperationStatus, error) { return incus.OperationSuccess, nil }, State: func(context.Context, string) (string, error) { return "Stopped", nil }}
	if _, err = l.Submit(context.Background(), otherKey, key, spec, "stop"); err == nil || calls != 0 {
		t.Fatalf("unowned request err=%v calls=%d", err, calls)
	}
	hash, _ := specHash(spec)
	if _, err = store.Reserve(key, hash); err != nil {
		t.Fatal(err)
	}
	if err = store.RecordOperation(key, "/1.0/operations/create"); err != nil {
		t.Fatal(err)
	}
	if err = store.RecordCompleted(key, "/1.0/operations/create"); err != nil {
		t.Fatal(err)
	}
	if _, err = l.Submit(context.Background(), otherKey, key, spec, "stop"); err == nil || calls != 0 {
		t.Fatalf("mismatched owner err=%v calls=%d", err, calls)
	}
	verified = true
	for i := 0; i < 2; i++ {
		result, err := l.Submit(context.Background(), otherKey, key, spec, "stop")
		if err != nil || result.State != jobs.Submitted {
			t.Fatalf("result=%+v err=%v", result, err)
		}
	}
	if calls != 1 {
		t.Fatalf("calls=%d", calls)
	}
	raw, err := store.Details(otherKey)
	if err != nil {
		t.Fatal(err)
	}
	var saved lifecycleSpec
	if err = json.Unmarshal(raw, &saved); err != nil || saved.Owner != key || saved.Spec != spec || saved.Action != "stop" {
		t.Fatalf("saved=%+v err=%v", saved, err)
	}
	result, err := l.Reconcile(context.Background(), otherKey, key, spec, "stop")
	if err != nil || result.State != jobs.Completed {
		t.Fatalf("result=%+v err=%v", result, err)
	}
	items, err := l.Managed(context.Background())
	if err != nil || len(items) != 0 {
		t.Fatalf("unexpected lifecycle record in inventory: %+v %v", items, err)
	}
}

func TestLifecycleKeepsAdmissionOnFailure(t *testing.T) {
	store, err := jobs.Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	spec := incus.InstanceSpec{Name: "demo"}
	hash, _ := specHash(spec)
	_, _ = store.Reserve(key, hash)
	_ = store.RecordOperation(key, "/1.0/operations/create")
	_ = store.RecordCompleted(key, "/1.0/operations/create")
	l := Lifecycle{Store: store, Verify: func(context.Context, incus.InstanceSpec, string) (bool, error) { return true, nil }, Change: func(context.Context, string, string) (string, error) { return "", errors.New("lost") }}
	for i := 0; i < 2; i++ {
		r, err := l.Submit(context.Background(), otherKey, key, spec, "restart")
		if err != nil || r.State != jobs.NeedsReconciliation {
			t.Fatalf("result=%+v err=%v", r, err)
		}
	}
	if _, err := store.AcquireAdmission("11111111111111111111111111111111"); !errors.Is(err, jobs.ErrAdmissionBusy) {
		t.Fatalf("admission=%v", err)
	}
}
