// Package jobs stores durable idempotency reservations before backend calls.
package jobs

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"strings"
)

var ErrConflict = errors.New("idempotency key reused with different payload")
var ErrAdmissionBusy = errors.New("another creation requires completion or reconciliation")
var keyPattern = regexp.MustCompile(`^[a-f0-9]{32}$`)
var hashPattern = regexp.MustCompile(`^[a-f0-9]{64}$`)
var opPattern = regexp.MustCompile(`^/1\.0/operations/[a-zA-Z0-9-]+$`)

type Store struct{ dir string }
type reservation struct {
	Hash string `json:"hash"`
}

func Open(dir string) (*Store, error) {
	if dir == "" {
		return nil, errors.New("state directory required")
	}
	if err := os.MkdirAll(dir, 0700); err != nil {
		return nil, err
	}
	if err := syncDir(filepath.Dir(filepath.Clean(dir))); err != nil {
		return nil, err
	}
	return &Store{dir: dir}, nil
}

// AcquireAdmission serializes creation across processes sharing the state
// directory. An abandoned or corrupt marker fails closed after a crash.
func (s *Store) AcquireAdmission(key string) (bool, error) {
	if !keyPattern.MatchString(key) {
		return false, errors.New("invalid key")
	}
	unlock, err := lockAdmission(s.dir)
	if err != nil {
		return false, err
	}
	defer unlock()
	path := filepath.Join(s.dir, "admission.lock")
	created, err := writeExclusive(path, key)
	if err != nil || created {
		return created, err
	}
	data, err := os.ReadFile(path)
	if err != nil {
		return false, err
	}
	var owner string
	if json.Unmarshal(data, &owner) != nil || !keyPattern.MatchString(owner) {
		return false, ErrAdmissionBusy
	}
	if owner == key {
		return false, nil
	}
	return false, ErrAdmissionBusy
}

// ReleaseAdmission must be called only after a no-side-effect rejection or
// after the operation and resource state have been reconciled.
func (s *Store) ReleaseAdmission(key string) error {
	return s.releaseAdmission(key, false)
}

// ReleaseCompletedAdmission is idempotent and never removes another owner's marker.
func (s *Store) ReleaseCompletedAdmission(key string) error { return s.releaseAdmission(key, true) }

func (s *Store) releaseAdmission(key string, completed bool) error {
	if !keyPattern.MatchString(key) {
		return errors.New("invalid key")
	}
	unlock, err := lockAdmission(s.dir)
	if err != nil {
		return err
	}
	defer unlock()
	path := filepath.Join(s.dir, "admission.lock")
	data, err := os.ReadFile(path)
	if completed && errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	var owner string
	if json.Unmarshal(data, &owner) != nil || !keyPattern.MatchString(owner) {
		return ErrAdmissionBusy
	}
	if completed && owner != key {
		return nil
	}
	if owner != key {
		return ErrAdmissionBusy
	}
	if err = os.Remove(path); err != nil {
		return err
	}
	return syncDir(s.dir)
}

func writeExclusive(path string, value any) (bool, error) {
	data, err := json.Marshal(value)
	if err != nil {
		return false, err
	}
	f, err := os.OpenFile(path, os.O_WRONLY|os.O_CREATE|os.O_EXCL, 0600)
	if errors.Is(err, os.ErrExist) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	defer f.Close()
	if _, err = f.Write(data); err != nil {
		return false, err
	}
	if err = f.Sync(); err != nil {
		return false, err
	}
	if err = f.Close(); err != nil {
		return false, err
	}
	if err = syncDir(filepath.Dir(path)); err != nil {
		return false, err
	}
	return true, nil
}

func (s *Store) Reserve(key, hash string) (bool, error) {
	if !keyPattern.MatchString(key) || !hashPattern.MatchString(hash) {
		return false, errors.New("invalid key or hash")
	}
	path := filepath.Join(s.dir, key+".job")
	created, err := writeExclusive(path, reservation{Hash: hash})
	if err != nil || created {
		return created, err
	}
	raw, err := os.ReadFile(path)
	if err != nil {
		return false, err
	}
	var prior reservation
	if json.Unmarshal(raw, &prior) != nil || !hashPattern.MatchString(prior.Hash) {
		return false, errors.New("incomplete reservation requires manual reconciliation")
	}
	if prior.Hash != hash {
		return false, ErrConflict
	}
	return false, nil
}

// RecordDetails stores the canonical non-secret request payload only when it
// matches the immutable reservation hash, before the backend is contacted.
func (s *Store) RecordDetails(key string, raw []byte) error {
	if !keyPattern.MatchString(key) || len(raw) == 0 || len(raw) > 16<<10 || !json.Valid(raw) {
		return errors.New("invalid job details")
	}
	canonical, err := json.Marshal(json.RawMessage(raw))
	if err != nil {
		return err
	}
	sum := sha256.Sum256(canonical)
	hash := hex.EncodeToString(sum[:])
	_, found, err := s.Lookup(key, hash)
	if err != nil {
		return err
	}
	if !found {
		return errors.New("job reservation missing")
	}
	created, err := writeExclusive(filepath.Join(s.dir, key+".details"), json.RawMessage(canonical))
	if err != nil {
		return err
	}
	if created {
		return nil
	}
	prior, err := s.Details(key)
	if err != nil {
		return err
	}
	if string(prior) != string(canonical) {
		return ErrConflict
	}
	return nil
}

func (s *Store) Details(key string) ([]byte, error) {
	if !keyPattern.MatchString(key) {
		return nil, errors.New("invalid key")
	}
	raw, err := os.ReadFile(filepath.Join(s.dir, key+".details"))
	if err != nil {
		return nil, err
	}
	if len(raw) == 0 || len(raw) > 16<<10 || !json.Valid(raw) {
		return nil, errors.New("invalid job details")
	}
	sum := sha256.Sum256(raw)
	_, found, err := s.Lookup(key, hex.EncodeToString(sum[:]))
	if err != nil {
		return nil, err
	}
	if !found {
		return nil, errors.New("job reservation missing")
	}
	return raw, nil
}

type CompletedDetail struct {
	Key     string          `json:"key"`
	Details json.RawMessage `json:"details"`
}

type PendingTask struct {
	Key     string          `json:"key"`
	State   State           `json:"state"`
	Details json.RawMessage `json:"details,omitempty"`
}

// PendingTasks includes completed jobs still holding admission after a crash.
func (s *Store) PendingTasks() ([]PendingTask, error) {
	unlock, err := lockAdmission(s.dir)
	if err != nil {
		return nil, err
	}
	data, readErr := os.ReadFile(filepath.Join(s.dir, "admission.lock"))
	owner := ""
	if readErr == nil {
		if json.Unmarshal(data, &owner) != nil || !keyPattern.MatchString(owner) {
			unlock()
			return nil, ErrAdmissionBusy
		}
	} else if !errors.Is(readErr, os.ErrNotExist) {
		unlock()
		return nil, readErr
	}
	unlock()
	entries, err := os.ReadDir(s.dir)
	if err != nil {
		return nil, err
	}
	if len(entries) > 100000 {
		return nil, errors.New("too many job records")
	}
	result := []PendingTask{}
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".job") {
			continue
		}
		key := strings.TrimSuffix(entry.Name(), ".job")
		if !keyPattern.MatchString(key) {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(s.dir, entry.Name()))
		if err != nil {
			return nil, err
		}
		var reservation reservation
		if json.Unmarshal(raw, &reservation) != nil || !hashPattern.MatchString(reservation.Hash) {
			return nil, errors.New("invalid job reservation")
		}
		job, found, err := s.Lookup(key, reservation.Hash)
		if err != nil {
			return nil, err
		}
		if !found {
			continue
		}
		if job.State == Completed && owner != key {
			continue
		}
		details, err := s.Details(key)
		if err != nil && !errors.Is(err, os.ErrNotExist) {
			return nil, err
		}
		result = append(result, PendingTask{Key: key, State: job.State, Details: json.RawMessage(details)})
	}
	return result, nil
}

// CompletedDetails returns only intact records with a persisted completion.
func (s *Store) CompletedDetails() ([]CompletedDetail, error) {
	entries, err := os.ReadDir(s.dir)
	if err != nil {
		return nil, err
	}
	if len(entries) > 100000 {
		return nil, errors.New("too many job records")
	}
	result := []CompletedDetail{}
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".details") {
			continue
		}
		key := strings.TrimSuffix(entry.Name(), ".details")
		if !keyPattern.MatchString(key) {
			continue
		}
		raw, err := s.Details(key)
		if err != nil {
			return nil, err
		}
		sum := sha256.Sum256(raw)
		job, found, err := s.Lookup(key, hex.EncodeToString(sum[:]))
		if err != nil {
			return nil, err
		}
		if found && job.State == Completed {
			result = append(result, CompletedDetail{Key: key, Details: json.RawMessage(raw)})
		}
	}
	return result, nil
}

// Lookup never creates a reservation and preserves ambiguous outcomes.
func (s *Store) Lookup(key, hash string) (Result, bool, error) {
	if !keyPattern.MatchString(key) || !hashPattern.MatchString(hash) {
		return Result{}, false, errors.New("invalid key or hash")
	}
	raw, err := os.ReadFile(filepath.Join(s.dir, key+".job"))
	if errors.Is(err, os.ErrNotExist) {
		return Result{}, false, nil
	}
	if err != nil {
		return Result{}, false, err
	}
	var prior reservation
	if json.Unmarshal(raw, &prior) != nil || !hashPattern.MatchString(prior.Hash) {
		return Result{}, true, errors.New("incomplete reservation requires manual reconciliation")
	}
	if prior.Hash != hash {
		return Result{}, true, ErrConflict
	}
	op, err := s.Operation(key)
	if errors.Is(err, os.ErrNotExist) {
		return Result{State: NeedsReconciliation}, true, nil
	}
	if err != nil {
		return Result{}, true, err
	}
	completed, err := s.completed(key, op)
	if err != nil {
		return Result{}, true, err
	}
	if completed {
		return Result{State: Completed, Operation: op}, true, nil
	}
	return Result{State: Submitted, Operation: op}, true, nil
}

func (s *Store) completed(key, op string) (bool, error) {
	data, err := os.ReadFile(filepath.Join(s.dir, key+".completed"))
	if errors.Is(err, os.ErrNotExist) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	var stored string
	if json.Unmarshal(data, &stored) != nil || stored != op {
		return false, errors.New("invalid completion record")
	}
	return true, nil
}

// RecordCompleted persists the verified terminal result before admission release.
func (s *Store) RecordCompleted(key, op string) error {
	actual, err := s.Operation(key)
	if err != nil {
		return err
	}
	if actual != op {
		return ErrConflict
	}
	created, err := writeExclusive(filepath.Join(s.dir, key+".completed"), op)
	if err != nil {
		return err
	}
	if created {
		return nil
	}
	ok, err := s.completed(key, op)
	if err != nil {
		return err
	}
	if !ok {
		return errors.New("missing completion record")
	}
	return nil
}

func (s *Store) RecordOperation(key, op string) error {
	if !keyPattern.MatchString(key) || !opPattern.MatchString(op) {
		return errors.New("invalid key or operation")
	}
	if _, err := os.Stat(filepath.Join(s.dir, key+".job")); err != nil {
		return err
	}
	path := filepath.Join(s.dir, key+".operation")
	created, err := writeExclusive(path, op)
	if err != nil {
		return err
	}
	if created {
		return nil
	}
	prior, err := s.Operation(key)
	if err != nil {
		return err
	}
	if prior != op {
		return fmt.Errorf("operation already recorded")
	}
	return nil
}

// ReserveStart persists intent before sending start. An interrupted intent is
// ambiguous and must not be replayed automatically.
func (s *Store) ReserveStart(key string) (bool, error) {
	if !keyPattern.MatchString(key) {
		return false, errors.New("invalid key")
	}
	if _, err := s.Operation(key); err != nil {
		return false, err
	}
	return writeExclusive(filepath.Join(s.dir, key+".start-intent"), key)
}

func (s *Store) RecordStartOperation(key, op string) error {
	if !keyPattern.MatchString(key) || !opPattern.MatchString(op) {
		return errors.New("invalid key or operation")
	}
	if _, err := os.Stat(filepath.Join(s.dir, key+".start-intent")); err != nil {
		return err
	}
	created, err := writeExclusive(filepath.Join(s.dir, key+".start-operation"), op)
	if err != nil {
		return err
	}
	if created {
		return nil
	}
	prior, err := s.StartOperation(key)
	if err != nil {
		return err
	}
	if prior != op {
		return ErrConflict
	}
	return nil
}

func (s *Store) StartOperation(key string) (string, error) {
	if !keyPattern.MatchString(key) {
		return "", errors.New("invalid key")
	}
	data, err := os.ReadFile(filepath.Join(s.dir, key+".start-operation"))
	if err != nil {
		return "", err
	}
	var op string
	if json.Unmarshal(data, &op) != nil || !opPattern.MatchString(op) {
		return "", errors.New("incomplete start operation")
	}
	return op, nil
}

func (s *Store) Operation(key string) (string, error) {
	if !keyPattern.MatchString(key) {
		return "", errors.New("invalid key")
	}
	data, err := os.ReadFile(filepath.Join(s.dir, key+".operation"))
	if err != nil {
		return "", err
	}
	var op string
	if json.Unmarshal(data, &op) != nil || !opPattern.MatchString(op) {
		return "", errors.New("incomplete operation requires manual reconciliation")
	}
	return op, nil
}
