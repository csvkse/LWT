package jobs

import (
	"context"
	"errors"
	"os"
)

type State string

const (
	Submitted           State = "Submitted"
	Starting            State = "Starting"
	NeedsReconciliation State = "NeedsReconciliation"
	Completed           State = "Completed"
)

type Result struct {
	State     State  `json:"state"`
	Operation string `json:"operation,omitempty"`
}

// Submit reserves the key before contacting the backend. If the outcome is
// ambiguous it never resubmits; an operator must reconcile actual state.
func Submit(ctx context.Context, store *Store, key, hash string, submit func(context.Context) (string, error)) (Result, error) {
	created, err := store.Reserve(key, hash)
	if err != nil {
		return Result{}, err
	}
	if !created {
		operation, err := store.Operation(key)
		if errors.Is(err, os.ErrNotExist) {
			return Result{State: NeedsReconciliation}, nil
		}
		if err != nil {
			return Result{}, err
		}
		return Result{State: Submitted, Operation: operation}, nil
	}
	operation, err := submit(ctx)
	if err != nil {
		return Result{State: NeedsReconciliation}, nil
	}
	if err = store.RecordOperation(key, operation); err != nil {
		return Result{State: NeedsReconciliation}, err
	}
	return Result{State: Submitted, Operation: operation}, nil
}
