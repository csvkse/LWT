package main

import (
	"sync"
	"sync/atomic"
)

const defaultMaxActiveSessions = 512

// ConnectionAdmission bounds accepted sessions without occupying a worker while
// waiting for capacity. A permit belongs to one accepted session and is safe to
// release from multiple cleanup paths.
type ConnectionAdmission struct {
	limit  int64
	active atomic.Int64
}

type ConnectionPermit struct {
	admission *ConnectionAdmission
	released  sync.Once
}

func NewConnectionAdmission(limit int) *ConnectionAdmission {
	if limit <= 0 {
		limit = defaultMaxActiveSessions
	}
	return &ConnectionAdmission{limit: int64(limit)}
}

func (a *ConnectionAdmission) TryAcquire() (*ConnectionPermit, bool) {
	if a == nil {
		return nil, false
	}
	for current := a.active.Load(); ; current = a.active.Load() {
		if current >= a.limit {
			return nil, false
		}
		if a.active.CompareAndSwap(current, current+1) {
			return &ConnectionPermit{admission: a}, true
		}
	}
}

func (a *ConnectionAdmission) Active() int64 {
	if a == nil {
		return 0
	}
	return a.active.Load()
}

func (p *ConnectionPermit) Release() {
	if p == nil || p.admission == nil {
		return
	}
	p.released.Do(func() {
		p.admission.active.Add(-1)
	})
}
