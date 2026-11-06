package jobs

import (
	"context"
	"errors"
	"sync/atomic"
	"testing"
)

func TestSubmitDoesNotRepeatAfterAmbiguousFailure(t *testing.T) {
	store, err := Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	var calls atomic.Int32
	submit := func(context.Context) (string, error) { calls.Add(1); return "", errors.New("connection lost") }
	first, err := Submit(context.Background(), store, key, hash, submit)
	if err != nil || first.State != NeedsReconciliation {
		t.Fatalf("first=%+v err=%v", first, err)
	}
	second, err := Submit(context.Background(), store, key, hash, submit)
	if err != nil || second.State != NeedsReconciliation || calls.Load() != 1 {
		t.Fatalf("second=%+v calls=%d err=%v", second, calls.Load(), err)
	}
}

func TestSubmitReturnsPersistedOperationOnRetry(t *testing.T) {
	store, err := Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	var calls atomic.Int32
	submit := func(context.Context) (string, error) { calls.Add(1); return "/1.0/operations/123-abc", nil }
	for i := 0; i < 2; i++ {
		result, err := Submit(context.Background(), store, key, hash, submit)
		if err != nil || result.State != Submitted || result.Operation != "/1.0/operations/123-abc" {
			t.Fatalf("result=%+v err=%v", result, err)
		}
	}
	if calls.Load() != 1 {
		t.Fatalf("calls=%d", calls.Load())
	}
}
