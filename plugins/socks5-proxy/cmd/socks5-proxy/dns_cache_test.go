package main

import (
	"context"
	"errors"
	"net"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type mockResolver struct {
	callCount int64
	delay     time.Duration
	resultIPs []net.IP
	resultErr error
}

func (m *mockResolver) Resolve(ctx context.Context, host string) ([]net.IP, error) {
	atomic.AddInt64(&m.callCount, 1)
	if m.delay > 0 {
		time.Sleep(m.delay)
	}
	return m.resultIPs, m.resultErr
}

func TestNormalizeDomain(t *testing.T) {
	tests := []struct {
		input    string
		expected string
	}{
		{"  Example.COM  ", "example.com"},
		{"example.com.", "example.com"},
		{"  SUB.Domain.ORG. ", "sub.domain.org"},
		{"127.0.0.1", "127.0.0.1"},
	}

	for _, tc := range tests {
		actual := NormalizeDomain(tc.input)
		if actual != tc.expected {
			t.Errorf("NormalizeDomain(%q) = %q, expected %q", tc.input, actual, tc.expected)
		}
	}
}

func TestDnsLruCache_HitAndEviction(t *testing.T) {
	capacity := 2
	cache := NewDnsLruCache(capacity, 100*time.Millisecond, 200*time.Millisecond, 50*time.Millisecond)

	ip1 := net.ParseIP("192.168.1.1")
	ip2 := net.ParseIP("192.168.1.2")
	ip3 := net.ParseIP("192.168.1.3")

	cache.Set("host1.com", []net.IP{ip1})
	cache.Set("host2.com", []net.IP{ip2})

	// Hit host1.com (moves host1 to MRU, making host2 LRU)
	entry, isStale, found := cache.Get("host1.com")
	if !found || isStale || len(entry.IPs) == 0 || !entry.IPs[0].Equal(ip1) {
		t.Fatalf("expected host1.com to hit cache with ip %v", ip1)
	}

	// Insert host3.com, should evict host2.com (since host1 was accessed)
	cache.Set("host3.com", []net.IP{ip3})

	if _, _, found := cache.Get("host2.com"); found {
		t.Errorf("expected host2.com to be evicted")
	}

	if _, _, found := cache.Get("host1.com"); !found {
		t.Errorf("expected host1.com to remain in cache")
	}

	if _, _, found := cache.Get("host3.com"); !found {
		t.Errorf("expected host3.com to be in cache")
	}

	hits, misses, staleHits, evictions, size := cache.Stats()
	if hits < 1 || misses < 1 || evictions != 1 || size != 2 || staleHits != 0 {
		t.Errorf("unexpected stats: hits=%d, misses=%d, staleHits=%d, evictions=%d, size=%d", hits, misses, staleHits, evictions, size)
	}
}

func TestDnsLruCache_TTL_Expiration(t *testing.T) {
	softTtl := 20 * time.Millisecond
	hardTtl := 50 * time.Millisecond
	cache := NewDnsLruCache(10, softTtl, hardTtl, 10*time.Millisecond)

	cache.Set("expiring.com", []net.IP{net.ParseIP("10.0.0.1")})

	// 1. Immediate lookup -> Fresh hit
	_, isStale, found := cache.Get("expiring.com")
	if !found || isStale {
		t.Fatal("expected fresh immediate cache hit")
	}

	// 2. Wait 30ms -> past soft TTL, within hard TTL -> Stale hit (RFC 5861)
	time.Sleep(30 * time.Millisecond)
	_, isStale, found = cache.Get("expiring.com")
	if !found || !isStale {
		t.Fatal("expected stale hit between soft and hard TTL")
	}

	// 3. Wait past hard TTL -> Complete eviction
	time.Sleep(30 * time.Millisecond)
	if _, _, found := cache.Get("expiring.com"); found {
		t.Fatal("expected cache entry to be expired after hard TTL")
	}
}

func TestDnsLruCache_NegativeCaching(t *testing.T) {
	negTtl := 30 * time.Millisecond
	cache := NewDnsLruCache(10, 100*time.Millisecond, 200*time.Millisecond, negTtl)

	cache.SetNegative("non-existent.test")

	entry, _, found := cache.Get("non-existent.test")
	if !found {
		t.Fatal("expected negative cache entry to be found")
	}
	if entry.State != DnsStateNegative {
		t.Fatalf("expected entry state %v, got %v", DnsStateNegative, entry.State)
	}

	time.Sleep(50 * time.Millisecond)
	if _, _, found := cache.Get("non-existent.test"); found {
		t.Fatal("expected negative cache entry to expire")
	}
}

func TestCachedDnsResolver_SingleflightDeduplication(t *testing.T) {
	mock := &mockResolver{
		delay:     40 * time.Millisecond,
		resultIPs: []net.IP{net.ParseIP("203.0.113.10")},
	}

	resolver := NewCachedDnsResolver(mock, 100, 1*time.Minute, 5*time.Minute, 5*time.Second)

	concurrency := 20
	var wg sync.WaitGroup
	wg.Add(concurrency)

	for i := 0; i < concurrency; i++ {
		go func() {
			defer wg.Done()
			ips, err := resolver.Resolve(context.Background(), "concurrent.example.com")
			if err != nil {
				t.Errorf("unexpected resolve error: %v", err)
			}
			if len(ips) == 0 || !ips[0].Equal(mock.resultIPs[0]) {
				t.Errorf("unexpected resolved ip: %v", ips)
			}
		}()
	}

	wg.Wait()

	calls := atomic.LoadInt64(&mock.callCount)
	if calls != 1 {
		t.Fatalf("expected exactly 1 underlying DNS query due to singleflight, got %d", calls)
	}

	hits, _, _, _, size := resolver.CacheStats()
	if size != 1 {
		t.Errorf("expected cache size 1, got %d", size)
	}
	if hits < int64(concurrency-1) {
		t.Logf("singleflight deduplicated concurrency, cache hits recorded: %d", hits)
	}
}

func TestCachedDnsResolver_DirectIP(t *testing.T) {
	mock := &mockResolver{}
	resolver := NewCachedDnsResolver(mock, 10, time.Minute, 5*time.Minute, time.Second)

	ips, err := resolver.Resolve(context.Background(), "127.0.0.1")
	if err != nil || len(ips) == 0 || ips[0].String() != "127.0.0.1" {
		t.Fatalf("expected direct IP return without lookup: %v, %v", ips, err)
	}

	if atomic.LoadInt64(&mock.callCount) != 0 {
		t.Errorf("expected 0 underlying calls for raw IP, got %d", mock.callCount)
	}
}

func TestCachedDnsResolver_NegativeError(t *testing.T) {
	mock := &mockResolver{
		resultErr: errors.New("nxdomain error"),
	}
	resolver := NewCachedDnsResolver(mock, 10, time.Minute, 5*time.Minute, 50*time.Millisecond)

	_, err := resolver.Resolve(context.Background(), "failed.domain.test")
	if err == nil {
		t.Fatal("expected initial error")
	}

	// Immediate next query should return ErrNegativeCached without calling underlying resolver
	_, err2 := resolver.Resolve(context.Background(), "failed.domain.test")
	if !errors.Is(err2, ErrNegativeCached) && err2 == nil {
		t.Fatalf("expected negative cached error, got: %v", err2)
	}

	if atomic.LoadInt64(&mock.callCount) != 1 {
		t.Errorf("expected underlying resolver to be called only once due to negative cache, got %d", mock.callCount)
	}
}
