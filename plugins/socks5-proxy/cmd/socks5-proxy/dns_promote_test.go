package main

import (
	"context"
	"net"
	"testing"
	"time"
)

func TestDnsLruCache_PromoteWinningIP(t *testing.T) {
	cache := NewDnsLruCache(10, 5*time.Minute, 15*time.Minute, 5*time.Second)

	ipA := net.ParseIP("192.0.2.1")
	ipB := net.ParseIP("192.0.2.2")
	ipC := net.ParseIP("192.0.2.3")

	domain := "video.bilibili.com"
	cache.Set(domain, []net.IP{ipA, ipB, ipC})

	// Initial check: index 0 is ipA
	entry, _, found := cache.Get(domain)
	if !found || len(entry.IPs) != 3 {
		t.Fatalf("expected 3 IPs in cache entry")
	}
	if !entry.IPs[0].Equal(ipA) {
		t.Fatalf("expected ipA at index 0, got %v", entry.IPs[0])
	}

	// Promote ipB (middle candidate)
	promoted := cache.PromoteWinningIP(domain, ipB)
	if !promoted {
		t.Fatalf("expected PromoteWinningIP to return true")
	}

	// Verify ipB is now at index 0
	entry, _, found = cache.Get(domain)
	if !found {
		t.Fatalf("expected entry to be found after promotion")
	}
	if !entry.IPs[0].Equal(ipB) {
		t.Fatalf("expected ipB at index 0, got %v", entry.IPs[0])
	}
	if !entry.IPs[1].Equal(ipA) || !entry.IPs[2].Equal(ipC) {
		t.Fatalf("expected remaining IPs [ipA, ipC], got [%v, %v]", entry.IPs[1], entry.IPs[2])
	}

	// Re-promoting ipB (already at index 0) should be a no-op
	if cache.PromoteWinningIP(domain, ipB) {
		t.Fatalf("expected second promotion of already-first IP to return false")
	}

	// Promote ipC (last candidate)
	if !cache.PromoteWinningIP(domain, ipC) {
		t.Fatalf("expected ipC promotion to succeed")
	}
	entry, _, _ = cache.Get(domain)
	if !entry.IPs[0].Equal(ipC) {
		t.Fatalf("expected ipC at index 0, got %v", entry.IPs[0])
	}

	// Verify promotion counter
	_, _, _, _, promotions, _ := cache.Stats()
	if promotions != 2 {
		t.Fatalf("expected 2 promotions, got %d", promotions)
	}

	// Promoting non-existent IP returns false
	foreignIP := net.ParseIP("198.51.100.99")
	if cache.PromoteWinningIP(domain, foreignIP) {
		t.Fatalf("promoting unlisted IP should return false")
	}

	// Promoting unknown domain returns false
	if cache.PromoteWinningIP("unknown.domain.org", ipA) {
		t.Fatalf("promoting unknown domain should return false")
	}
}

func TestDnsRoutingResolver_PromoteWinningIP(t *testing.T) {
	baseMock := &mockResolver{resultIPs: []net.IP{net.ParseIP("203.0.113.1"), net.ParseIP("203.0.113.2")}}
	baseCached := NewCachedDnsResolver(baseMock, 10, 5*time.Minute, 15*time.Minute, 5*time.Second)

	routeMock := &mockResolver{resultIPs: []net.IP{net.ParseIP("198.51.100.1"), net.ParseIP("198.51.100.2")}}
	routeCached := NewCachedDnsResolver(routeMock, 10, 5*time.Minute, 15*time.Minute, 5*time.Second)

	router := &dnsRoutingResolver{
		base: baseCached,
		routes: []dnsRoute{
			{suffix: "bilibili.com", resolver: routeCached},
		},
	}

	ctx := context.Background()

	// Resolve route domain
	biliIPs, err := router.Resolve(ctx, "api.bilibili.com")
	if err != nil || len(biliIPs) != 2 {
		t.Fatalf("failed to resolve api.bilibili.com: %v", err)
	}

	// Promote winning IP in route
	winningBiliIP := net.ParseIP("198.51.100.2")
	if !router.PromoteWinningIP("api.bilibili.com", winningBiliIP) {
		t.Fatalf("expected PromoteWinningIP on route to succeed")
	}

	// Verify subsequent resolve returns winning IP at index 0
	biliIPs, _ = router.Resolve(ctx, "api.bilibili.com")
	if !biliIPs[0].Equal(winningBiliIP) {
		t.Fatalf("expected winning IP %v at index 0, got %v", winningBiliIP, biliIPs[0])
	}

	// Resolve base domain (not matching route)
	otherIPs, err := router.Resolve(ctx, "github.com")
	if err != nil || len(otherIPs) != 2 {
		t.Fatalf("failed to resolve github.com: %v", err)
	}

	// Promote winning IP on base
	winningBaseIP := net.ParseIP("203.0.113.2")
	if !router.PromoteWinningIP("github.com", winningBaseIP) {
		t.Fatalf("expected PromoteWinningIP on base to succeed")
	}

	otherIPs, _ = router.Resolve(ctx, "github.com")
	if !otherIPs[0].Equal(winningBaseIP) {
		t.Fatalf("expected winning IP %v at index 0 on base, got %v", winningBaseIP, otherIPs[0])
	}
}

func TestDnsLruCache_Flush(t *testing.T) {
	cache := NewDnsLruCache(10, 5*time.Minute, 15*time.Minute, 5*time.Second)
	cache.Set("d1.com", []net.IP{net.ParseIP("192.0.2.1")})
	cache.Set("d2.com", []net.IP{net.ParseIP("192.0.2.2")})

	if cache.Len() != 2 {
		t.Fatalf("expected 2 items, got %d", cache.Len())
	}

	cache.Flush()

	if cache.Len() != 0 {
		t.Fatalf("expected 0 items after flush, got %d", cache.Len())
	}
	if _, _, found := cache.Get("d1.com"); found {
		t.Fatalf("expected d1.com to be flushed")
	}
}
