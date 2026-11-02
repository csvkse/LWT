package admin

import (
	"context"
	"fmt"
	"sync"
	"sync/atomic"
	"time"
)

// JobStatus represents short discovery task status.
type JobStatus string

const (
	JobStatusRunning   JobStatus = "running"
	JobStatusCompleted JobStatus = "completed"
	JobStatusFailed    JobStatus = "failed"
	JobStatusCancelled JobStatus = "cancelled"
)

// Job represents an asynchronous admin operation.
type Job struct {
	ID        string      `json:"id"`
	Type      string      `json:"type"`
	ServerID  string      `json:"serverId"`
	Status    JobStatus   `json:"status"`
	Result    any         `json:"result,omitempty"`
	Error     string      `json:"error,omitempty"`
	CreatedAt time.Time   `json:"createdAt"`
	cancel    context.CancelFunc
}

// JobTracker manages background discovery/probe jobs.
type JobTracker struct {
	mu     sync.Mutex
	jobs   map[string]*Job
	seq    atomic.Int64
}

// NewJobTracker creates a tracker for short admin operations.
func NewJobTracker() *JobTracker {
	return &JobTracker{
		jobs: make(map[string]*Job),
	}
}

// Submit starts tracking a new job.
func (t *JobTracker) Submit(jobType string, serverID string, cancel context.CancelFunc) *Job {
	t.mu.Lock()
	defer t.mu.Unlock()

	id := fmt.Sprintf("job-%d-%d", time.Now().Unix(), t.seq.Add(1))
	job := &Job{
		ID:        id,
		Type:      jobType,
		ServerID:  serverID,
		Status:    JobStatusRunning,
		CreatedAt: time.Now(),
		cancel:    cancel,
	}

	t.jobs[id] = job

	// Cleanup old completed jobs if > 20
	if len(t.jobs) > 20 {
		for k, v := range t.jobs {
			if v.Status != JobStatusRunning {
				delete(t.jobs, k)
				break
			}
		}
	}

	return job
}

// Complete marks a job as done.
func (t *JobTracker) Complete(id string, result any, err error) {
	t.mu.Lock()
	defer t.mu.Unlock()

	if job, ok := t.jobs[id]; ok {
		if err != nil {
			job.Status = JobStatusFailed
			job.Error = err.Error()
		} else {
			job.Status = JobStatusCompleted
			job.Result = result
		}
	}
}

// Get returns the job status.
func (t *JobTracker) Get(id string) (*Job, bool) {
	t.mu.Lock()
	defer t.mu.Unlock()
	job, ok := t.jobs[id]
	return job, ok
}

// Cancel cancels a running job.
func (t *JobTracker) Cancel(id string) bool {
	t.mu.Lock()
	defer t.mu.Unlock()
	if job, ok := t.jobs[id]; ok && job.Status == JobStatusRunning {
		job.Status = JobStatusCancelled
		if job.cancel != nil {
			job.cancel()
		}
		return true
	}
	return false
}
