package jobs

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
)

func TestDetailsPersistAndRejectTampering(t *testing.T) {
	dir := t.TempDir()
	s, err := Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	raw := []byte(`{"name":"demo"}`)
	sum := sha256.Sum256(raw)
	digest := hex.EncodeToString(sum[:])
	if _, err = s.Reserve(key, digest); err != nil {
		t.Fatal(err)
	}
	if err = s.RecordDetails(key, raw); err != nil {
		t.Fatal(err)
	}
	s, err = Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	got, err := s.Details(key)
	if err != nil || string(got) != string(raw) {
		t.Fatalf("details=%s err=%v", got, err)
	}
	if err = os.WriteFile(filepath.Join(dir, key+".details"), []byte(`{"name":"other"}`), 0600); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Details(key); err == nil {
		t.Fatal("tampered details accepted")
	}
}

func TestCompletedDetailsListsOnlyVerifiedCompletedRecords(t *testing.T) {
	s, err := Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	raw := []byte(`{"name":"demo"}`)
	sum := sha256.Sum256(raw)
	digest := hex.EncodeToString(sum[:])
	if _, err = s.Reserve(key, digest); err != nil {
		t.Fatal(err)
	}
	if err = s.RecordDetails(key, raw); err != nil {
		t.Fatal(err)
	}
	items, err := s.CompletedDetails()
	if err != nil || len(items) != 0 {
		t.Fatalf("items=%+v err=%v", items, err)
	}
	if err = s.RecordOperation(key, "/1.0/operations/create"); err != nil {
		t.Fatal(err)
	}
	if err = s.RecordCompleted(key, "/1.0/operations/create"); err != nil {
		t.Fatal(err)
	}
	items, err = s.CompletedDetails()
	if err != nil || len(items) != 1 || items[0].Key != key || string(items[0].Details) != string(raw) {
		t.Fatalf("items=%+v err=%v", items, err)
	}
}

func TestPendingTasksIncludesLostDetailsAndExcludesCompleted(t *testing.T) {
	s, err := Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.Reserve(key, hash); err != nil {
		t.Fatal(err)
	}
	tasks, err := s.PendingTasks()
	if err != nil || len(tasks) != 1 || tasks[0].Key != key || tasks[0].State != NeedsReconciliation || len(tasks[0].Details) != 0 {
		t.Fatalf("tasks=%+v err=%v", tasks, err)
	}
	if err = s.RecordOperation(key, "/1.0/operations/create"); err != nil {
		t.Fatal(err)
	}
	if err = s.RecordCompleted(key, "/1.0/operations/create"); err != nil {
		t.Fatal(err)
	}
	tasks, err = s.PendingTasks()
	if err != nil || len(tasks) != 0 {
		t.Fatalf("completed task listed: %+v %v", tasks, err)
	}
}

func TestPendingTasksIncludesCompletedJobHoldingAdmission(t *testing.T) {
	s, err := Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	if _, err = s.AcquireAdmission(key); err != nil {
		t.Fatal(err)
	}
	if _, err = s.Reserve(key, hash); err != nil {
		t.Fatal(err)
	}
	if err = s.RecordOperation(key, "/1.0/operations/create"); err != nil {
		t.Fatal(err)
	}
	if err = s.RecordCompleted(key, "/1.0/operations/create"); err != nil {
		t.Fatal(err)
	}
	tasks, err := s.PendingTasks()
	if err != nil || len(tasks) != 1 || tasks[0].State != Completed {
		t.Fatalf("tasks=%+v err=%v", tasks, err)
	}
}

const key = "0123456789abcdef0123456789abcdef"
const hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

func TestReservationPersistsAndRejectsChangedPayload(t *testing.T) {
	dir := t.TempDir()
	s, err := Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	created, err := s.Reserve(key, hash)
	if err != nil || !created {
		t.Fatalf("created=%v err=%v", created, err)
	}
	if err = s.RecordOperation(key, "/1.0/operations/123-abc"); err != nil {
		t.Fatal(err)
	}
	s, err = Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	created, err = s.Reserve(key, hash)
	if err != nil || created {
		t.Fatalf("created=%v err=%v", created, err)
	}
	op, err := s.Operation(key)
	if err != nil || op != "/1.0/operations/123-abc" {
		t.Fatalf("op=%s err=%v", op, err)
	}
	_, err = s.Reserve(key, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")
	if !errors.Is(err, ErrConflict) {
		t.Fatalf("err=%v", err)
	}
}

func TestConcurrentReservationHasExactlyOneWinner(t *testing.T) {
	s, err := Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	var won atomic.Int32
	var wg sync.WaitGroup
	for i := 0; i < 20; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			created, err := s.Reserve(key, hash)
			if err == nil && created {
				won.Add(1)
			}
		}()
	}
	wg.Wait()
	if won.Load() != 1 {
		t.Fatalf("winners=%d", won.Load())
	}
}

func TestInterruptedReservationDoesNotRetry(t *testing.T) {
	dir := t.TempDir()
	s, err := Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	if err = os.WriteFile(filepath.Join(dir, key+".job"), nil, 0600); err != nil {
		t.Fatal(err)
	}
	created, err := s.Reserve(key, hash)
	if created || err == nil {
		t.Fatalf("created=%v err=%v", created, err)
	}
	if _, err = s.Reserve("../escape", hash); err == nil {
		t.Fatal("invalid key accepted")
	}
}

func TestLookupDistinguishesNewUncertainAndSubmitted(t *testing.T) {
	s, err := Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	_, found, err := s.Lookup(key, hash)
	if err != nil || found {
		t.Fatalf("found=%v err=%v", found, err)
	}
	if _, err = s.Reserve(key, hash); err != nil {
		t.Fatal(err)
	}
	r, found, err := s.Lookup(key, hash)
	if err != nil || !found || r.State != NeedsReconciliation {
		t.Fatalf("result=%+v found=%v err=%v", r, found, err)
	}
	if err = s.RecordOperation(key, "/1.0/operations/123-abc"); err != nil {
		t.Fatal(err)
	}
	r, found, err = s.Lookup(key, hash)
	if err != nil || !found || r.State != Submitted {
		t.Fatalf("result=%+v found=%v err=%v", r, found, err)
	}
}

func TestExclusiveAdmissionSurvivesReopen(t *testing.T) {
	dir := t.TempDir()
	s, err := Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	won, err := s.AcquireAdmission(key)
	if err != nil || !won {
		t.Fatalf("won=%v err=%v", won, err)
	}
	s, err = Open(dir)
	if err != nil {
		t.Fatal(err)
	}
	won, err = s.AcquireAdmission(key)
	if err != nil || won {
		t.Fatalf("duplicate won=%v err=%v", won, err)
	}
	_, err = s.AcquireAdmission("fedcba9876543210fedcba9876543210")
	if !errors.Is(err, ErrAdmissionBusy) {
		t.Fatalf("err=%v", err)
	}
	if err = s.ReleaseAdmission(key); err != nil {
		t.Fatal(err)
	}
	won, err = s.AcquireAdmission("fedcba9876543210fedcba9876543210")
	if err != nil || !won {
		t.Fatalf("won=%v err=%v", won, err)
	}
}

func TestReleaseCompletedAdmissionCannotRemoveNewOwner(t *testing.T) {
	s, err := Open(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	other := "fedcba9876543210fedcba9876543210"
	if won, err := s.AcquireAdmission(key); !won || err != nil {
		t.Fatalf("won=%v err=%v", won, err)
	}
	if err = s.ReleaseCompletedAdmission(key); err != nil {
		t.Fatal(err)
	}
	if won, err := s.AcquireAdmission(other); !won || err != nil {
		t.Fatalf("won=%v err=%v", won, err)
	}
	if err = s.ReleaseCompletedAdmission(key); err != nil {
		t.Fatal(err)
	}
	if _, err = s.AcquireAdmission(key); !errors.Is(err, ErrAdmissionBusy) {
		t.Fatalf("new owner lost: %v", err)
	}
}
