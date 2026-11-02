package watchdog

import (
	"math/rand"
	"time"
)

// ExponentialBackoff computes backoff durations with full jitter to avoid thundering herd.
type ExponentialBackoff struct {
	InitialInterval     time.Duration
	Multiplier          float64
	MaxInterval         time.Duration
	RandomizationFactor float64
	rnd                 *rand.Rand
}

// NewDefaultBackoff creates an exponential backoff controller with safe defaults.
func NewDefaultBackoff() *ExponentialBackoff {
	return &ExponentialBackoff{
		InitialInterval:     2 * time.Second,
		Multiplier:          2.0,
		MaxInterval:         60 * time.Second,
		RandomizationFactor: 0.2,
		rnd:                 rand.New(rand.NewSource(time.Now().UnixNano())),
	}
}

// NextDelay calculates the duration to wait before the next retry attempt (jittered).
func (b *ExponentialBackoff) NextDelay(retryCount int) time.Duration {
	if retryCount <= 0 {
		return b.InitialInterval
	}

	d := float64(b.InitialInterval)
	for i := 0; i < retryCount && d < float64(b.MaxInterval); i++ {
		d *= b.Multiplier
	}

	if d > float64(b.MaxInterval) {
		d = float64(b.MaxInterval)
	}

	// Apply jitter: [d - factor * d, d + factor * d]
	delta := b.RandomizationFactor * d
	minVal := d - delta
	maxVal := d + delta
	if minVal < float64(b.InitialInterval) {
		minVal = float64(b.InitialInterval)
	}

	jittered := minVal + b.rnd.Float64()*(maxVal-minVal)
	return time.Duration(jittered)
}
