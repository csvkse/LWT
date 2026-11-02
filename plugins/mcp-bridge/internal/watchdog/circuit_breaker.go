package watchdog

import (
	"sync"
	"time"
)

// CircuitState represents the tripping state of a protected target.
type CircuitState string

const (
	CircuitClosed   CircuitState = "Closed"   // Normal operation
	CircuitOpen     CircuitState = "Open"     // Tripped: stop frequent retries
	CircuitHalfOpen CircuitState = "HalfOpen" // Trial recovery mode
)

// CircuitBreaker guards against process/connection storms when targets persistently fail.
type CircuitBreaker struct {
	mu           sync.Mutex
	maxFailures  int
	coolingPeriod time.Duration
	state        CircuitState
	failureCount int
	lastTrippedAt time.Time
}

// NewCircuitBreaker creates a circuit breaker instance.
func NewCircuitBreaker(maxFailures int, coolingPeriod time.Duration) *CircuitBreaker {
	if maxFailures <= 0 {
		maxFailures = 5
	}
	if coolingPeriod <= 0 {
		coolingPeriod = 60 * time.Second
	}
	return &CircuitBreaker{
		maxFailures:   maxFailures,
		coolingPeriod: coolingPeriod,
		state:         CircuitClosed,
	}
}

// State returns the current circuit condition.
func (cb *CircuitBreaker) State() CircuitState {
	cb.mu.Lock()
	defer cb.mu.Unlock()

	if cb.state == CircuitOpen && time.Since(cb.lastTrippedAt) >= cb.coolingPeriod {
		cb.state = CircuitHalfOpen
	}
	return cb.state
}

// AllowExecution determines whether a retry or execution should be permitted.
func (cb *CircuitBreaker) AllowExecution() bool {
	cb.mu.Lock()
	defer cb.mu.Unlock()

	if cb.state == CircuitClosed {
		return true
	}
	if cb.state == CircuitOpen {
		if time.Since(cb.lastTrippedAt) >= cb.coolingPeriod {
			cb.state = CircuitHalfOpen
			return true
		}
		return false
	}
	// HalfOpen: allow one probe
	return true
}

// RecordSuccess resets failure counts upon successful probe or reconnection.
func (cb *CircuitBreaker) RecordSuccess() {
	cb.mu.Lock()
	defer cb.mu.Unlock()
	cb.failureCount = 0
	cb.state = CircuitClosed
}

// RecordFailure records an error and trips the breaker if threshold is reached.
func (cb *CircuitBreaker) RecordFailure() (trippedNow bool) {
	cb.mu.Lock()
	defer cb.mu.Unlock()

	cb.failureCount++
	if cb.state == CircuitHalfOpen || cb.failureCount >= cb.maxFailures {
		trippedNow = (cb.state != CircuitOpen)
		cb.state = CircuitOpen
		cb.lastTrippedAt = time.Now()
		return trippedNow
	}
	return false
}

// FailureCount returns the current count of consecutive failures.
func (cb *CircuitBreaker) FailureCount() int {
	cb.mu.Lock()
	defer cb.mu.Unlock()
	return cb.failureCount
}
