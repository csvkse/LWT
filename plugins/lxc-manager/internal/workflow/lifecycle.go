package workflow

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"

	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
)

// Lifecycle manages only instances with a completed creation job and matching
// backend ownership. It is intentionally not exposed by HTTP until verified.
type Lifecycle struct {
	Store   *jobs.Store
	Verify  func(context.Context, incus.InstanceSpec, string) (bool, error)
	Change  func(context.Context, string, string) (string, error)
	Observe func(context.Context, string) (incus.OperationStatus, error)
	State   func(context.Context, string) (string, error)
}
type lifecycleSpec struct {
	Owner  string             `json:"owner"`
	Spec   incus.InstanceSpec `json:"spec"`
	Action string             `json:"action"`
}

type ManagedInstance struct {
	Key      string             `json:"key"`
	Spec     incus.InstanceSpec `json:"spec"`
	Verified bool               `json:"verified"`
}

func (l Lifecycle) Pending(ctx context.Context) ([]jobs.PendingTask, error) {
	if l.Store == nil {
		return nil, errors.New("task store unavailable")
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	return l.Store.PendingTasks()
}

// Managed restores completed creation records and reports drift explicitly.
func (l Lifecycle) Managed(ctx context.Context) ([]ManagedInstance, error) {
	if l.Store == nil || l.Verify == nil {
		return nil, errors.New("managed inventory unavailable")
	}
	records, err := l.Store.CompletedDetails()
	if err != nil {
		return nil, err
	}
	if len(records) > 100 {
		return nil, errors.New("managed inventory limit exceeded")
	}
	result := make([]ManagedInstance, 0, len(records))
	for _, record := range records {
		var kind struct {
			Owner string `json:"owner"`
		}
		if json.Unmarshal(record.Details, &kind) != nil {
			return nil, errors.New("invalid managed record")
		}
		if kind.Owner != "" {
			continue
		}
		var spec incus.InstanceSpec
		if json.Unmarshal(record.Details, &spec) != nil || spec.Name == "" {
			return nil, errors.New("invalid managed record")
		}
		ok, err := l.Verify(ctx, spec, record.Key)
		if err != nil {
			return nil, err
		}
		result = append(result, ManagedInstance{Key: record.Key, Spec: spec, Verified: ok})
	}
	return result, nil
}

func lifecycleHash(owner string, spec incus.InstanceSpec, action string) (string, error) {
	if action != "start" && action != "stop" && action != "restart" {
		return "", errors.New("unsupported lifecycle action")
	}
	data, err := json.Marshal(lifecycleSpec{owner, spec, action})
	if err != nil {
		return "", err
	}
	sum := sha256.Sum256(data)
	return hex.EncodeToString(sum[:]), nil
}

func (l Lifecycle) owned(ctx context.Context, owner string, spec incus.InstanceSpec) error {
	hash, err := specHash(spec)
	if err != nil {
		return err
	}
	result, found, err := l.Store.Lookup(owner, hash)
	if err != nil {
		return err
	}
	if !found || result.State != jobs.Completed {
		return fmt.Errorf("%w: instance ownership not established", ErrPreflight)
	}
	ok, err := l.Verify(ctx, spec, owner)
	if err != nil {
		return err
	}
	if !ok {
		return fmt.Errorf("%w: instance ownership changed", ErrPreflight)
	}
	return nil
}

func (l Lifecycle) Submit(ctx context.Context, key, owner string, spec incus.InstanceSpec, action string) (jobs.Result, error) {
	if l.Store == nil || l.Verify == nil || l.Change == nil {
		return jobs.Result{}, errors.New("lifecycle dependencies unavailable")
	}
	hash, err := lifecycleHash(owner, spec, action)
	if err != nil {
		return jobs.Result{}, err
	}
	result, found, err := l.Store.Lookup(key, hash)
	if err != nil || found {
		return result, err
	}
	acquired, err := l.Store.AcquireAdmission(key)
	if err != nil {
		return jobs.Result{}, err
	}
	if !acquired {
		return jobs.Result{State: jobs.NeedsReconciliation}, nil
	}
	result, found, err = l.Store.Lookup(key, hash)
	if err != nil || found {
		if e := l.Store.ReleaseAdmission(key); e != nil {
			return jobs.Result{}, e
		}
		return result, err
	}
	if err = l.owned(ctx, owner, spec); err != nil {
		if e := l.Store.ReleaseAdmission(key); e != nil {
			return jobs.Result{}, e
		}
		return jobs.Result{}, err
	}
	return jobs.Submit(ctx, l.Store, key, hash, func(ctx context.Context) (string, error) {
		raw, err := json.Marshal(lifecycleSpec{owner, spec, action})
		if err != nil {
			return "", err
		}
		if err = l.Store.RecordDetails(key, raw); err != nil {
			return "", err
		}
		return l.Change(ctx, spec.Name, action)
	})
}

func (l Lifecycle) Reconcile(ctx context.Context, key, owner string, spec incus.InstanceSpec, action string) (jobs.Result, error) {
	if l.Store == nil || l.Verify == nil || l.Observe == nil || l.State == nil {
		return jobs.Result{}, errors.New("lifecycle reconciliation unavailable")
	}
	hash, err := lifecycleHash(owner, spec, action)
	if err != nil {
		return jobs.Result{}, err
	}
	result, found, err := l.Store.Lookup(key, hash)
	if err != nil {
		return jobs.Result{}, err
	}
	if !found {
		return jobs.Result{}, errors.New("job not found")
	}
	if result.State == jobs.Completed {
		if err = l.Store.ReleaseCompletedAdmission(key); err != nil {
			return result, err
		}
		return result, nil
	}
	if result.State == jobs.NeedsReconciliation {
		return result, nil
	}
	status, err := l.Observe(ctx, result.Operation)
	if err != nil {
		return result, err
	}
	if status == incus.OperationPending {
		return result, nil
	}
	if status != incus.OperationSuccess {
		return jobs.Result{State: jobs.NeedsReconciliation, Operation: result.Operation}, nil
	}
	if err = l.owned(ctx, owner, spec); err != nil {
		return jobs.Result{State: jobs.NeedsReconciliation, Operation: result.Operation}, nil
	}
	state, err := l.State(ctx, spec.Name)
	if err != nil {
		return result, err
	}
	expected := "Running"
	if action == "stop" {
		expected = "Stopped"
	}
	if state != expected {
		return result, nil
	}
	if err = l.Store.RecordCompleted(key, result.Operation); err != nil {
		return result, err
	}
	result.State = jobs.Completed
	if err = l.Store.ReleaseCompletedAdmission(key); err != nil {
		return result, err
	}
	return result, nil
}
