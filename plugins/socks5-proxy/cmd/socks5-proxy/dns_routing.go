package main

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"sort"
	"strconv"
	"strings"
	"time"
)

type dnsRouteConfig struct {
	Suffix  string `json:"suffix"`
	Server  string `json:"server"`
	SoftTTL string `json:"softTtl,omitempty"`
	HardTTL string `json:"hardTtl,omitempty"`
}

type dnsRoute struct {
	suffix   string
	resolver *CachedDnsResolver
}

type dnsRoutingResolver struct {
	base   IDnsResolverStrategy
	routes []dnsRoute
}

func (r *dnsRoutingResolver) Resolve(ctx context.Context, host string) ([]net.IP, error) {
	norm := NormalizeDomain(host)
	if ip := net.ParseIP(norm); ip != nil {
		return []net.IP{ip}, nil
	}
	for _, route := range r.routes {
		if route.suffix == "*" || norm == route.suffix || strings.HasSuffix(norm, "."+route.suffix) {
			return route.resolver.Resolve(ctx, norm)
		}
	}
	return r.base.Resolve(ctx, norm)
}

func (r *dnsRoutingResolver) CacheStats() (hits, misses, staleHits, evictions int64, size int) {
	add := func(s interface {
		CacheStats() (int64, int64, int64, int64, int)
	}) {
		h, m, st, e, n := s.CacheStats()
		hits += h
		misses += m
		staleHits += st
		evictions += e
		size += n
	}
	if s, ok := r.base.(interface {
		CacheStats() (int64, int64, int64, int64, int)
	}); ok {
		add(s)
	}
	for _, route := range r.routes {
		add(route.resolver)
	}
	return
}

func validDNSSuffix(s string) bool {
	if len(s) == 0 || len(s) > 253 || net.ParseIP(s) != nil {
		return false
	}
	for _, label := range strings.Split(s, ".") {
		if len(label) == 0 || len(label) > 63 || label[0] == '-' || label[len(label)-1] == '-' {
			return false
		}
		for _, c := range label {
			if !(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-') {
				return false
			}
		}
	}
	return true
}

func buildDNSResolver(base IDnsResolverStrategy, raw string, timeout time.Duration) (IDnsResolverStrategy, error) {
	if base == nil {
		return nil, errors.New("default DNS resolver is required")
	}
	if strings.TrimSpace(raw) == "" {
		return base, nil
	}
	var configs []dnsRouteConfig
	decoder := json.NewDecoder(strings.NewReader(raw))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&configs); err != nil || configs == nil {
		return nil, errors.New("invalid DNS rules JSON")
	}
	if err := decoder.Decode(new(any)); err != io.EOF {
		return nil, errors.New("invalid DNS rules trailing data")
	}
	if len(configs) > 64 {
		return nil, errors.New("too many DNS rules")
	}
	if len(configs) == 0 {
		return base, nil
	}
	if timeout <= 0 {
		timeout = 10 * time.Second
	}
	router := &dnsRoutingResolver{base: base}
	seen := make(map[string]bool)
	for i, cfg := range configs {
		suffix := NormalizeDomain(cfg.Suffix)
		if (suffix != "*" && !validDNSSuffix(suffix)) || seen[suffix] {
			return nil, fmt.Errorf("invalid or duplicate DNS rule suffix at index %d", i)
		}
		seen[suffix] = true
		host, port, err := net.SplitHostPort(strings.TrimSpace(cfg.Server))
		ip := net.ParseIP(host)
		if err != nil || ip == nil || ip.IsUnspecified() || ip.IsMulticast() || ip.Equal(net.IPv4bcast) {
			return nil, fmt.Errorf("DNS server must be an IP and port at index %d", i)
		}
		portNumber, err := strconv.Atoi(port)
		if err != nil || portNumber < 1 || portNumber > 65535 {
			return nil, fmt.Errorf("invalid DNS server port at index %d", i)
		}
		server := net.JoinHostPort(host, strconv.Itoa(portNumber))
		softTTL, hardTTL := 3*time.Minute, 10*time.Minute
		if cfg.SoftTTL != "" {
			softTTL, err = time.ParseDuration(cfg.SoftTTL)
			if err != nil {
				return nil, fmt.Errorf("invalid DNS soft TTL at index %d", i)
			}
		}
		if cfg.HardTTL != "" {
			hardTTL, err = time.ParseDuration(cfg.HardTTL)
			if err != nil {
				return nil, fmt.Errorf("invalid DNS hard TTL at index %d", i)
			}
		}
		if softTTL < time.Second || hardTTL < softTTL || hardTTL > 24*time.Hour {
			return nil, fmt.Errorf("invalid DNS TTL bounds at index %d", i)
		}
		custom := &Ipv4PreferResolver{timeout: timeout, resolver: &net.Resolver{PreferGo: true, Dial: func(ctx context.Context, network, _ string) (net.Conn, error) {
			return (&net.Dialer{}).DialContext(ctx, network, server)
		}}}
		router.routes = append(router.routes, dnsRoute{suffix: suffix, resolver: NewCachedDnsResolverWithTimeout(custom, 512, softTTL, hardTTL, 5*time.Second, timeout)})
	}
	sort.SliceStable(router.routes, func(i, j int) bool {
		if router.routes[i].suffix == "*" {
			return false
		}
		if router.routes[j].suffix == "*" {
			return true
		}
		return len(router.routes[i].suffix) > len(router.routes[j].suffix)
	})
	return router, nil
}
