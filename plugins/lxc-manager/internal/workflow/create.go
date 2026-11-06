// Package workflow coordinates safe creation; it is not yet exposed by HTTP.
package workflow

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"

	"lwt/lxc-manager/internal/incus"
	"lwt/lxc-manager/internal/jobs"
)

var ErrPreflight = errors.New("preflight rejected")

type Creator struct {
	Store   *jobs.Store
	Check   func(context.Context, incus.InstanceSpec) (incus.PreflightResult, error)
	Create  func(context.Context, incus.InstanceSpec, string) (string, error)
	Observe func(context.Context, string) (incus.OperationStatus, error)
	Verify  func(context.Context, incus.InstanceSpec, string) (bool, error)
	Start   func(context.Context, string) (string, error)
	Running func(context.Context, string) (bool, error)
}

func specHash(spec incus.InstanceSpec) (string, error) {
	data, err := json.Marshal(spec)
	if err != nil {
		return "", err
	}
	digest := sha256.Sum256(data)
	return hex.EncodeToString(digest[:]), nil
}

func (c Creator) Submit(ctx context.Context, key string, spec incus.InstanceSpec) (jobs.Result, error) {
	if c.Store == nil || c.Check == nil || c.Create == nil {
		return jobs.Result{}, errors.New("creation dependencies unavailable")
	}
	hash, err := specHash(spec)
	if err != nil {
		return jobs.Result{}, err
	}
	result, found, err := c.Store.Lookup(key, hash)
	if err != nil || found {
		return result, err
	}
	acquired, err := c.Store.AcquireAdmission(key)
	if err != nil {
		return jobs.Result{}, err
	}
	if !acquired {
		return jobs.Result{State: jobs.NeedsReconciliation}, nil
	}
	// Recheck after acquiring the cross-process admission marker.
	result, found, err = c.Store.Lookup(key, hash)
	if err != nil || found {
		if releaseErr := c.Store.ReleaseAdmission(key); releaseErr != nil {
			return jobs.Result{}, releaseErr
		}
		return result, err
	}
	preflight, err := c.Check(ctx, spec)
	if err != nil || !preflight.Allowed {
		if releaseErr := c.Store.ReleaseAdmission(key); releaseErr != nil {
			return jobs.Result{}, releaseErr
		}
		if err != nil {
			return jobs.Result{}, err
		}
		return jobs.Result{}, fmt.Errorf("%w: %v", ErrPreflight, preflight.Reasons)
	}
	// Keep admission held after submission, including failures with unknown
	// outcome, until operation and actual resource state are reconciled.
	return jobs.Submit(ctx, c.Store, key, hash, func(ctx context.Context) (string, error) {
		data, err := json.Marshal(spec)
		if err != nil {
			return "", err
		}
		if err = c.Store.RecordDetails(key, data); err != nil {
			return "", err
		}
		return c.Create(ctx, spec, key)
	})
}

// Reconcile requires a known successful operation and verified owned instance.
// Missing operation IDs and failed or mismatched results remain held for review.
func (c Creator) Reconcile(ctx context.Context, key string, spec incus.InstanceSpec) (jobs.Result, error) {
	if c.Store == nil || c.Observe == nil || c.Verify == nil || c.Start == nil || c.Running == nil {
		return jobs.Result{}, errors.New("reconciliation dependencies unavailable")
	}
	hash, err := specHash(spec)
	if err != nil {
		return jobs.Result{}, err
	}
	result, found, err := c.Store.Lookup(key, hash)
	if err != nil {
		return jobs.Result{}, err
	}
	if !found {
		return jobs.Result{}, errors.New("job not found")
	}
	if result.State == jobs.Completed {
		if err := c.Store.ReleaseCompletedAdmission(key); err != nil {
			return result, err
		}
		return result, nil
	}
	if result.State == jobs.NeedsReconciliation {
		return result, nil
	}
	state, err := c.Observe(ctx, result.Operation)
	if err != nil {
		return result, err
	}
	if state == incus.OperationPending {
		return result, nil
	}
	if state != incus.OperationSuccess {
		return jobs.Result{State: jobs.NeedsReconciliation, Operation: result.Operation}, nil
	}
	ok, err := c.Verify(ctx, spec, key)
	if err != nil {
		return result, err
	}
	if !ok {
		return jobs.Result{State: jobs.NeedsReconciliation, Operation: result.Operation}, nil
	}
	startOp, err := c.Store.StartOperation(key)
	if errors.Is(err, os.ErrNotExist) {
		acquired, reserveErr := c.Store.ReserveStart(key)
		if reserveErr != nil {
			return result, reserveErr
		}
		if !acquired {
			return jobs.Result{State: jobs.NeedsReconciliation, Operation: result.Operation}, nil
		}
		startOp, err = c.Start(ctx, spec.Name)
		if err != nil {
			return jobs.Result{State: jobs.NeedsReconciliation, Operation: result.Operation}, nil
		}
		if err = c.Store.RecordStartOperation(key, startOp); err != nil {
			return jobs.Result{State: jobs.NeedsReconciliation, Operation: result.Operation}, err
		}
		return jobs.Result{State: jobs.Starting, Operation: startOp}, nil
	}
	if err != nil {
		return result, err
	}
	startStatus, err := c.Observe(ctx, startOp)
	if err != nil {
		return result, err
	}
	if startStatus == incus.OperationPending {
		return jobs.Result{State: jobs.Starting, Operation: startOp}, nil
	}
	if startStatus != incus.OperationSuccess {
		return jobs.Result{State: jobs.NeedsReconciliation, Operation: startOp}, nil
	}
	running, err := c.Running(ctx, spec.Name)
	if err != nil {
		return result, err
	}
	if !running {
		return jobs.Result{State: jobs.Starting, Operation: startOp}, nil
	}
	if err = c.Store.RecordCompleted(key, result.Operation); err != nil {
		return result, err
	}
	result.State = jobs.Completed
	if err = c.Store.ReleaseCompletedAdmission(key); err != nil {
		return result, err
	}
	return result, nil
}
