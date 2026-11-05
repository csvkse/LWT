package main

import (
	"context"
	"errors"
	"net"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

var (
	ErrNegativeCached  = errors.New("domain lookup failed (negative cached)")
	ErrNoIPResolved    = errors.New("no ip addresses resolved for host")
	ErrResolverTimeout = errors.New("dns resolution timed out")
)

// NormalizeDomain strips leading/trailing spaces, trailing FQDN dot, and converts to lowercase.
func NormalizeDomain(domain string) string {
	d := strings.TrimSpace(domain)
	d = strings.ToLower(d)
	d = strings.TrimSuffix(d, ".")
	return d
}

// IDnsResolverStrategy defines the strategy interface for DNS resolution.
type IDnsResolverStrategy interface {
	Resolve(ctx context.Context, host string) ([]net.IP, error)
}

// DnsEntryState represents the state of a cached DNS entry in the state machine.
type DnsEntryState int

const (
	DnsStateValid DnsEntryState = iota
	DnsStateNegative
)

// DnsCacheEntry models a single DNS cache record with RFC 5861 dual TTL thresholds.
type DnsCacheEntry struct {
	Host          string
	IPs           []net.IP
	State         DnsEntryState
	CreatedAt     time.Time
	SoftExpiresAt time.Time // Stale-while-revalidate threshold (fresh until soft TTL)
	HardExpiresAt time.Time // Complete eviction threshold
	lastAccessNs  atomic.Int64
	lastTouchNs   atomic.Int64
}

func (e *DnsCacheEntry) IsStale(now time.Time) bool {
	return now.After(e.SoftExpiresAt) && !now.After(e.HardExpiresAt)
}

func (e *DnsCacheEntry) IsExpired(now time.Time) bool {
	return now.After(e.HardExpiresAt)
}

// DnsLruCache is a bounded, concurrent read-mostly cache with dual TTL and
// sampled approximate-LRU eviction. Reads do not acquire the write/eviction lock.
type DnsLruCache struct {
	capacity    int
	softTtl     time.Duration
	hardTtl     time.Duration
	negativeTtl time.Duration
	writeMu     sync.Mutex
	items       sync.Map
	size        int64
	accessSeq   atomic.Int64

	// Metrics
	hits      int64
	misses    int64
	staleHits int64
	evictions int64
}

func NewDnsLruCache(capacity int, softTtl, hardTtl, negativeTtl time.Duration) *DnsLruCache {
	if capacity <= 0 {
		capacity = 2048
	}
	if softTtl <= 0 {
		softTtl = 3 * time.Minute
	}
	if hardTtl <= 0 || hardTtl < softTtl {
		hardTtl = 10 * time.Minute
	}
	if negativeTtl <= 0 {
		negativeTtl = 5 * time.Second
	}
	return &DnsLruCache{
		capacity:    capacity,
		softTtl:     softTtl,
		hardTtl:     hardTtl,
		negativeTtl: negativeTtl,
	}
}

func (c *DnsLruCache) Get(host string) (entry *DnsCacheEntry, isStale bool, found bool) {
	value, found := c.items.Load(host)
	if !found {
		atomic.AddInt64(&c.misses, 1)
		return nil, false, false
	}

	entry = value.(*DnsCacheEntry)
	now := time.Now()
	if entry.IsExpired(now) {
		if c.items.CompareAndDelete(host, entry) {
			atomic.AddInt64(&c.size, -1)
		}
		atomic.AddInt64(&c.misses, 1)
		return nil, false, false
	}

	c.touch(entry, now)
	if entry.IsStale(now) {
		atomic.AddInt64(&c.staleHits, 1)
		return entry, true, true
	}

	atomic.AddInt64(&c.hits, 1)
	return entry, false, true
}

func (c *DnsLruCache) Set(host string, ips []net.IP) {
	now := time.Now()
	entry := &DnsCacheEntry{
		Host:          host,
		IPs:           ips,
		State:         DnsStateValid,
		CreatedAt:     now,
		SoftExpiresAt: now.Add(c.softTtl),
		HardExpiresAt: now.Add(c.hardTtl),
	}
	entry.lastAccessNs.Store(c.accessSeq.Add(1))

	c.store(host, entry)
}

func (c *DnsLruCache) SetNegative(host string) {
	now := time.Now()
	entry := &DnsCacheEntry{
		Host:          host,
		IPs:           nil,
		State:         DnsStateNegative,
		CreatedAt:     now,
		SoftExpiresAt: now.Add(c.negativeTtl),
		HardExpiresAt: now.Add(c.negativeTtl),
	}
	entry.lastAccessNs.Store(c.accessSeq.Add(1))

	c.store(host, entry)
}

func (c *DnsLruCache) store(host string, entry *DnsCacheEntry) {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	for {
		current, exists := c.items.Load(host)
		if !exists {
			if _, loaded := c.items.LoadOrStore(host, entry); loaded {
				continue
			}
			atomic.AddInt64(&c.size, 1)
			break
		}
		if c.items.CompareAndSwap(host, current, entry) {
			break
		}
	}
	c.evictOverflow()
}

func (c *DnsLruCache) evictOverflow() {
	for atomic.LoadInt64(&c.size) > int64(c.capacity) {
		var oldestKey string
		var oldest *DnsCacheEntry
		c.items.Range(func(key, value any) bool {
			entry := value.(*DnsCacheEntry)
			if oldest == nil || entry.lastAccessNs.Load() < oldest.lastAccessNs.Load() {
				oldestKey = key.(string)
				oldest = entry
			}
			return true
		})
		if oldest == nil {
			return
		}
		if !c.items.CompareAndDelete(oldestKey, oldest) {
			continue
		}
		atomic.AddInt64(&c.size, -1)
		atomic.AddInt64(&c.evictions, 1)
	}
}

func (c *DnsLruCache) touch(entry *DnsCacheEntry, now time.Time) {
	nowNs := now.UnixNano()
	lastTouch := entry.lastTouchNs.Load()
	if lastTouch != 0 && nowNs-lastTouch < int64(time.Second) {
		return
	}
	if !entry.lastTouchNs.CompareAndSwap(lastTouch, nowNs) {
		return
	}
	entry.lastAccessNs.Store(c.accessSeq.Add(1))
}

func (c *DnsLruCache) Len() int {
	return int(atomic.LoadInt64(&c.size))
}

func (c *DnsLruCache) Stats() (hits, misses, staleHits, evictions int64, size int) {
	return atomic.LoadInt64(&c.hits), atomic.LoadInt64(&c.misses), atomic.LoadInt64(&c.staleHits), atomic.LoadInt64(&c.evictions), c.Len()
}

// singleflightCall represents an active in-flight DNS query.
type singleflightCall struct {
	done chan struct{}
	val  []net.IP
	err  error
}

// SingleflightGroup deduplicates concurrent requests for the same host.
type SingleflightGroup struct {
	mu sync.Mutex
	m  map[string]*singleflightCall
}

func (g *SingleflightGroup) Do(key string, fn func() ([]net.IP, error)) ([]net.IP, error) {
	return g.DoContext(context.Background(), key, fn)
}

// DoContext shares one background lookup per key while allowing each waiter to
// stop waiting independently. The shared lookup is deliberately not tied to
// the first client's context.
func (g *SingleflightGroup) DoContext(ctx context.Context, key string, fn func() ([]net.IP, error)) ([]net.IP, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	if err := ctx.Err(); err != nil {
		return nil, err
	}
	call, _ := g.start(key, fn)
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-call.done:
		return call.val, call.err
	}
}

func (g *SingleflightGroup) start(key string, fn func() ([]net.IP, error)) (*singleflightCall, bool) {
	g.mu.Lock()
	if g.m == nil {
		g.m = make(map[string]*singleflightCall)
	}
	if call, ok := g.m[key]; ok {
		g.mu.Unlock()
		return call, false
	}
	call := &singleflightCall{done: make(chan struct{})}
	g.m[key] = call
	g.mu.Unlock()

	go func() {
		value, err := fn()
		g.mu.Lock()
		call.val = value
		call.err = err
		delete(g.m, key)
		close(call.done)
		g.mu.Unlock()
	}()
	return call, true
}

func (g *SingleflightGroup) DoAsync(key string, fn func() ([]net.IP, error)) bool {
	_, started := g.start(key, fn)
	return started
}

// Ipv4PreferResolver implements IDnsResolverStrategy, querying IPv4 first to eliminate AAAA IPv6 timeouts.
type Ipv4PreferResolver struct {
	resolver *net.Resolver
	timeout  time.Duration
}

func NewIpv4PreferResolver(timeout time.Duration) *Ipv4PreferResolver {
	if timeout <= 0 {
		timeout = 5 * time.Second
	}
	return &Ipv4PreferResolver{
		resolver: &net.Resolver{
			PreferGo: true,
		},
		timeout: timeout,
	}
}

func (r *Ipv4PreferResolver) Resolve(ctx context.Context, host string) ([]net.IP, error) {
	norm := NormalizeDomain(host)
	if ip := net.ParseIP(norm); ip != nil {
		return []net.IP{ip}, nil
	}

	resolveCtx, cancel := context.WithTimeout(ctx, r.timeout)
	defer cancel()

	// 1. Query IPv4 first (fast path, avoids musl AAAA hang)
	ips, err := r.resolver.LookupIP(resolveCtx, "ip4", norm)
	if err == nil && len(ips) > 0 {
		return ips, nil
	}

	// 2. Fallback to dual-stack lookup if IPv4 query yielded nothing
	ips, err = r.resolver.LookupIP(resolveCtx, "ip", norm)
	if err != nil {
		return nil, err
	}
	if len(ips) == 0 {
		return nil, ErrNoIPResolved
	}
	return ips, nil
}

// CachedDnsResolver combines an LRU Cache with Stale-While-Revalidate, Singleflight, and underlying Strategy.
type CachedDnsResolver struct {
	strategy      IDnsResolverStrategy
	cache         *DnsLruCache
	sf            SingleflightGroup
	lookupTimeout time.Duration
}

func NewCachedDnsResolver(strategy IDnsResolverStrategy, capacity int, softTtl, hardTtl, negativeTtl time.Duration) *CachedDnsResolver {
	return NewCachedDnsResolverWithTimeout(strategy, capacity, softTtl, hardTtl, negativeTtl, 10*time.Second)
}

func NewCachedDnsResolverWithTimeout(strategy IDnsResolverStrategy, capacity int, softTtl, hardTtl, negativeTtl, lookupTimeout time.Duration) *CachedDnsResolver {
	if strategy == nil {
		strategy = NewIpv4PreferResolver(5 * time.Second)
	}
	if lookupTimeout <= 0 {
		lookupTimeout = 10 * time.Second
	}
	return &CachedDnsResolver{
		strategy:      strategy,
		cache:         NewDnsLruCache(capacity, softTtl, hardTtl, negativeTtl),
		lookupTimeout: lookupTimeout,
	}
}

func (r *CachedDnsResolver) Resolve(ctx context.Context, host string) ([]net.IP, error) {
	norm := NormalizeDomain(host)
	if ip := net.ParseIP(norm); ip != nil {
		return []net.IP{ip}, nil
	}

	// 1. Check LRU Cache (including RFC 5861 Stale-While-Revalidate check)
	if entry, isStale, ok := r.cache.Get(norm); ok {
		if entry.State == DnsStateNegative {
			return nil, ErrNegativeCached
		}
		if len(entry.IPs) > 0 {
			if isStale {
				// Only the first stale hit schedules work; later hits return the
				// cached address without creating goroutines that wait on singleflight.
				r.sf.DoAsync("reval:"+norm, func() ([]net.IP, error) {
					bgCtx, cancel := context.WithTimeout(context.Background(), r.lookupTimeout)
					defer cancel()
					resIPs, resErr := r.strategy.Resolve(bgCtx, norm)
					if resErr == nil && len(resIPs) > 0 {
						r.cache.Set(norm, resIPs)
					}
					return resIPs, resErr
				})
			}
			return entry.IPs, nil
		}
	}

	// 2. Cache miss: Singleflight synchronous resolve
	ips, err := r.sf.DoContext(ctx, norm, func() ([]net.IP, error) {
		if entry, _, ok := r.cache.Get(norm); ok {
			if entry.State == DnsStateNegative {
				return nil, ErrNegativeCached
			}
			if len(entry.IPs) > 0 {
				return entry.IPs, nil
			}
		}

		lookupCtx, cancel := context.WithTimeout(context.Background(), r.lookupTimeout)
		defer cancel()
		resIPs, resErr := r.strategy.Resolve(lookupCtx, norm)
		if resErr != nil || len(resIPs) == 0 {
			r.cache.SetNegative(norm)
			if resErr == nil {
				resErr = ErrNoIPResolved
			}
			return nil, resErr
		}

		r.cache.Set(norm, resIPs)
		return resIPs, nil
	})

	return ips, err
}

func (r *CachedDnsResolver) CacheStats() (hits, misses, staleHits, evictions int64, size int) {
	return r.cache.Stats()
}
