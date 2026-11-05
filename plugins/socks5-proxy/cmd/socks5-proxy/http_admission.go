package main

import (
	"context"
	"errors"
	"hash/maphash"
	"sync"
	"sync/atomic"
	"time"
)

var (
	ErrHTTPHostQueueFull       = errors.New("HTTP proxy host request queue is full")
	ErrHTTPHostLimiterCapacity = errors.New("HTTP proxy host limiter state capacity is full")
)

const (
	defaultMaxHTTPPendingPerHost = 128
	defaultHTTPQueueTimeout      = 5 * time.Second
	httpHostRequestShardCount    = 32
	maxHTTPHostStatesPerShard    = 1024
)

type HTTPRequestAdmissionSnapshot struct {
	Active   int64
	Pending  int64
	Rejected int64
	TimedOut int64
	Hosts    int
}

type httpHostRequestWaiter struct {
	ready   chan struct{}
	granted bool // protected by the owning shard mutex
}

type httpHostRequestState struct {
	active  int
	waiters []*httpHostRequestWaiter
}

type httpHostRequestShard struct {
	mu    sync.Mutex
	hosts map[string]*httpHostRequestState
}

// HTTPHostRequestLimiter bounds active and queued HTTP proxy requests per
// upstream host. Waiters are granted FIFO permits as active requests finish.
type HTTPHostRequestLimiter struct {
	maxActive int
	maxQueued int
	seed      maphash.Seed
	shards    [httpHostRequestShardCount]httpHostRequestShard
	active    atomic.Int64
	pending   atomic.Int64
	rejected  atomic.Int64
	timedOut  atomic.Int64
	hosts     atomic.Int64
}

type HTTPHostRequestPermit struct {
	limiter  *HTTPHostRequestLimiter
	shard    *httpHostRequestShard
	state    *httpHostRequestState
	released atomic.Bool
}

func NewHTTPHostRequestLimiter(maxActive, maxQueued int) *HTTPHostRequestLimiter {
	if maxActive <= 0 {
		maxActive = 128
	}
	if maxQueued < 0 {
		maxQueued = 0
	}
	limiter := &HTTPHostRequestLimiter{
		maxActive: maxActive,
		maxQueued: maxQueued,
		seed:      maphash.MakeSeed(),
	}
	for index := range limiter.shards {
		limiter.shards[index].hosts = make(map[string]*httpHostRequestState)
	}
	return limiter
}

func (l *HTTPHostRequestLimiter) Acquire(ctx context.Context, host string) (*HTTPHostRequestPermit, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}

	shard := l.shardFor(host)
	shard.mu.Lock()
	state := shard.hosts[host]
	if state == nil {
		if len(shard.hosts) >= maxHTTPHostStatesPerShard {
			for candidate, idle := range shard.hosts {
				if idle.active == 0 && len(idle.waiters) == 0 {
					delete(shard.hosts, candidate)
					l.hosts.Add(-1)
					break
				}
			}
		}
		if len(shard.hosts) >= maxHTTPHostStatesPerShard {
			l.rejected.Add(1)
			shard.mu.Unlock()
			return nil, ErrHTTPHostLimiterCapacity
		}
		state = &httpHostRequestState{}
		shard.hosts[host] = state
		l.hosts.Add(1)
	}
	if err := ctx.Err(); err != nil {
		shard.mu.Unlock()
		return nil, err
	}
	if state.active < l.maxActive && len(state.waiters) == 0 {
		state.active++
		l.active.Add(1)
		shard.mu.Unlock()
		permit := &HTTPHostRequestPermit{limiter: l, shard: shard, state: state}
		if err := ctx.Err(); err != nil {
			permit.Release()
			return nil, err
		}
		return permit, nil
	}
	if len(state.waiters) >= l.maxQueued {
		l.rejected.Add(1)
		shard.mu.Unlock()
		return nil, ErrHTTPHostQueueFull
	}

	waiter := &httpHostRequestWaiter{ready: make(chan struct{})}
	state.waiters = append(state.waiters, waiter)
	l.pending.Add(1)
	shard.mu.Unlock()

	select {
	case <-waiter.ready:
		if err := ctx.Err(); err != nil {
			shard.mu.Lock()
			l.releaseLocked(state)
			shard.mu.Unlock()
			return nil, err
		}
		return &HTTPHostRequestPermit{limiter: l, shard: shard, state: state}, nil
	case <-ctx.Done():
		shard.mu.Lock()
		if waiter.granted {
			// Capacity was handed over concurrently with cancellation. Return it
			// to the next queued request instead of leaking the active slot.
			l.releaseLocked(state)
		} else {
			l.removeWaiterLocked(state, waiter)
		}
		if errors.Is(ctx.Err(), context.DeadlineExceeded) {
			l.timedOut.Add(1)
		}
		shard.mu.Unlock()
		return nil, ctx.Err()
	}
}

func (p *HTTPHostRequestPermit) Release() {
	if p == nil || p.limiter == nil || p.shard == nil || p.state == nil || !p.released.CompareAndSwap(false, true) {
		return
	}
	p.shard.mu.Lock()
	p.limiter.releaseLocked(p.state)
	p.shard.mu.Unlock()
}

func (l *HTTPHostRequestLimiter) releaseLocked(state *httpHostRequestState) {
	if len(state.waiters) > 0 {
		waiter := state.waiters[0]
		copy(state.waiters, state.waiters[1:])
		state.waiters[len(state.waiters)-1] = nil
		state.waiters = state.waiters[:len(state.waiters)-1]
		l.pending.Add(-1)
		waiter.granted = true
		close(waiter.ready)
		return
	}
	if state.active > 0 {
		state.active--
		l.active.Add(-1)
	}
}

func (l *HTTPHostRequestLimiter) removeWaiterLocked(state *httpHostRequestState, waiter *httpHostRequestWaiter) {
	for index, current := range state.waiters {
		if current != waiter {
			continue
		}
		copy(state.waiters[index:], state.waiters[index+1:])
		state.waiters[len(state.waiters)-1] = nil
		state.waiters = state.waiters[:len(state.waiters)-1]
		l.pending.Add(-1)
		break
	}
}

func (l *HTTPHostRequestLimiter) shardFor(host string) *httpHostRequestShard {
	index := maphash.String(l.seed, host) % uint64(len(l.shards))
	return &l.shards[index]
}

func (l *HTTPHostRequestLimiter) Snapshot() HTTPRequestAdmissionSnapshot {
	return HTTPRequestAdmissionSnapshot{
		Active:   l.active.Load(),
		Pending:  l.pending.Load(),
		Rejected: l.rejected.Load(),
		TimedOut: l.timedOut.Load(),
		Hosts:    int(l.hosts.Load()),
	}
}
