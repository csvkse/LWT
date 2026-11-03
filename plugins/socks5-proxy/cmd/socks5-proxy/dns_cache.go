package main

import (
	"container/list"
	"context"
	"errors"
	"net"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

var (
	ErrNegativeCached   = errors.New("domain lookup failed (negative cached)")
	ErrNoIPResolved     = errors.New("no ip addresses resolved for host")
	ErrResolverTimeout  = errors.New("dns resolution timed out")
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

// DnsCacheEntry models a single DNS cache record.
type DnsCacheEntry struct {
	Host      string
	IPs       []net.IP
	State     DnsEntryState
	CreatedAt time.Time
	ExpiresAt time.Time
}

func (e *DnsCacheEntry) IsExpired(now time.Time) bool {
	return now.After(e.ExpiresAt)
}

type lruItem struct {
	key   string
	entry *DnsCacheEntry
}

// DnsLruCache is a thread-safe LRU cache with capacity limits and TTL expiration.
type DnsLruCache struct {
	mu          sync.RWMutex
	capacity    int
	positiveTtl time.Duration
	negativeTtl time.Duration
	items       map[string]*list.Element
	evictList   *list.List

	// Metrics
	hits      int64
	misses    int64
	evictions int64
}

func NewDnsLruCache(capacity int, positiveTtl, negativeTtl time.Duration) *DnsLruCache {
	if capacity <= 0 {
		capacity = 2048
	}
	if positiveTtl <= 0 {
		positiveTtl = 5 * time.Minute
	}
	if negativeTtl <= 0 {
		negativeTtl = 5 * time.Second
	}
	return &DnsLruCache{
		capacity:    capacity,
		positiveTtl: positiveTtl,
		negativeTtl: negativeTtl,
		items:       make(map[string]*list.Element),
		evictList:   list.New(),
	}
}

func (c *DnsLruCache) Get(host string) (*DnsCacheEntry, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()

	elem, found := c.items[host]
	if !found {
		atomic.AddInt64(&c.misses, 1)
		return nil, false
	}

	item := elem.Value.(*lruItem)
	now := time.Now()
	if item.entry.IsExpired(now) {
		c.removeElement(elem)
		atomic.AddInt64(&c.misses, 1)
		return nil, false
	}

	c.evictList.MoveToFront(elem)
	atomic.AddInt64(&c.hits, 1)
	return item.entry, true
}

func (c *DnsLruCache) Set(host string, ips []net.IP) {
	c.mu.Lock()
	defer c.mu.Unlock()

	now := time.Now()
	entry := &DnsCacheEntry{
		Host:      host,
		IPs:       ips,
		State:     DnsStateValid,
		CreatedAt: now,
		ExpiresAt: now.Add(c.positiveTtl),
	}

	c.insertOrUpdate(host, entry)
}

func (c *DnsLruCache) SetNegative(host string) {
	c.mu.Lock()
	defer c.mu.Unlock()

	now := time.Now()
	entry := &DnsCacheEntry{
		Host:      host,
		IPs:       nil,
		State:     DnsStateNegative,
		CreatedAt: now,
		ExpiresAt: now.Add(c.negativeTtl),
	}

	c.insertOrUpdate(host, entry)
}

func (c *DnsLruCache) insertOrUpdate(host string, entry *DnsCacheEntry) {
	if elem, found := c.items[host]; found {
		elem.Value.(*lruItem).entry = entry
		c.evictList.MoveToFront(elem)
		return
	}

	for c.evictList.Len() >= c.capacity {
		c.removeOldest()
	}

	elem := c.evictList.PushFront(&lruItem{key: host, entry: entry})
	c.items[host] = elem
}

func (c *DnsLruCache) removeElement(elem *list.Element) {
	c.evictList.Remove(elem)
	item := elem.Value.(*lruItem)
	delete(c.items, item.key)
}

func (c *DnsLruCache) removeOldest() {
	elem := c.evictList.Back()
	if elem != nil {
		c.removeElement(elem)
		atomic.AddInt64(&c.evictions, 1)
	}
}

func (c *DnsLruCache) Len() int {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return len(c.items)
}

func (c *DnsLruCache) Stats() (hits, misses, evictions int64, size int) {
	c.mu.RLock()
	defer c.mu.RUnlock()
	return atomic.LoadInt64(&c.hits), atomic.LoadInt64(&c.misses), atomic.LoadInt64(&c.evictions), len(c.items)
}

// singleflightCall represents an active or completed in-flight DNS query.
type singleflightCall struct {
	wg  sync.WaitGroup
	val []net.IP
	err error
}

// SingleflightGroup deduplicates concurrent requests for the same host.
type SingleflightGroup struct {
	mu sync.Mutex
	m  map[string]*singleflightCall
}

func (g *SingleflightGroup) Do(key string, fn func() ([]net.IP, error)) ([]net.IP, error) {
	g.mu.Lock()
	if g.m == nil {
		g.m = make(map[string]*singleflightCall)
	}
	if c, ok := g.m[key]; ok {
		g.mu.Unlock()
		c.wg.Wait()
		return c.val, c.err
	}
	c := new(singleflightCall)
	c.wg.Add(1)
	g.m[key] = c
	g.mu.Unlock()

	c.val, c.err = fn()
	c.wg.Done()

	g.mu.Lock()
	delete(g.m, key)
	g.mu.Unlock()

	return c.val, c.err
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

// CachedDnsResolver combines an LRU Cache, Singleflight, and underlying Resolution Strategy.
type CachedDnsResolver struct {
	strategy IDnsResolverStrategy
	cache    *DnsLruCache
	sf       SingleflightGroup
}

func NewCachedDnsResolver(strategy IDnsResolverStrategy, capacity int, positiveTtl, negativeTtl time.Duration) *CachedDnsResolver {
	if strategy == nil {
		strategy = NewIpv4PreferResolver(5 * time.Second)
	}
	return &CachedDnsResolver{
		strategy: strategy,
		cache:    NewDnsLruCache(capacity, positiveTtl, negativeTtl),
	}
}

func (r *CachedDnsResolver) Resolve(ctx context.Context, host string) ([]net.IP, error) {
	norm := NormalizeDomain(host)
	if ip := net.ParseIP(norm); ip != nil {
		return []net.IP{ip}, nil
	}

	// 1. Check LRU Cache
	if entry, ok := r.cache.Get(norm); ok {
		if entry.State == DnsStateNegative {
			return nil, ErrNegativeCached
		}
		if len(entry.IPs) > 0 {
			return entry.IPs, nil
		}
	}

	// 2. Use Singleflight to deduplicate concurrent queries for the same host
	ips, err := r.sf.Do(norm, func() ([]net.IP, error) {
		// Double-check cache inside singleflight lock window
		if entry, ok := r.cache.Get(norm); ok {
			if entry.State == DnsStateNegative {
				return nil, ErrNegativeCached
			}
			if len(entry.IPs) > 0 {
				return entry.IPs, nil
			}
		}

		resIPs, resErr := r.strategy.Resolve(ctx, norm)
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

func (r *CachedDnsResolver) CacheStats() (hits, misses, evictions int64, size int) {
	return r.cache.Stats()
}
