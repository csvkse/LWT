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
	"sync"
	"time"
)

type dnsRouteConfig struct {
	Suffix  string   `json:"suffix"`
	Server  string   `json:"server,omitempty"`
	Servers []string `json:"servers,omitempty"`
	SoftTTL string   `json:"softTtl,omitempty"`
	HardTTL string   `json:"hardTtl,omitempty"`
}

type dnsRoute struct {
	suffix   string
	servers  []string
	softTTL  time.Duration
	hardTTL  time.Duration
	resolver *CachedDnsResolver
}

type dnsRoutingResolver struct {
	mu      sync.RWMutex
	base    IDnsResolverStrategy
	routes  []dnsRoute
	configs []dnsRouteConfig
}

func (r *dnsRoutingResolver) Resolve(ctx context.Context, host string) ([]net.IP, error) {
	norm := NormalizeDomain(host)
	if ip := net.ParseIP(norm); ip != nil {
		return []net.IP{ip}, nil
	}

	r.mu.RLock()
	routes := r.routes
	base := r.base
	r.mu.RUnlock()

	for _, route := range routes {
		if route.suffix == "*" || norm == route.suffix || strings.HasSuffix(norm, "."+route.suffix) {
			return route.resolver.Resolve(ctx, norm)
		}
	}
	return base.Resolve(ctx, norm)
}

func (r *dnsRoutingResolver) PromoteWinningIP(host string, ip net.IP) bool {
	norm := NormalizeDomain(host)
	r.mu.RLock()
	routes := r.routes
	base := r.base
	r.mu.RUnlock()

	for _, route := range routes {
		if route.suffix == "*" || norm == route.suffix || strings.HasSuffix(norm, "."+route.suffix) {
			return route.resolver.PromoteWinningIP(norm, ip)
		}
	}
	return base.PromoteWinningIP(norm, ip)
}

func (r *dnsRoutingResolver) Flush() {
	r.mu.RLock()
	defer r.mu.RUnlock()
	if flusher, ok := r.base.(interface{ Flush() }); ok {
		flusher.Flush()
	}
	for _, route := range r.routes {
		route.resolver.Flush()
	}
}

func (r *dnsRoutingResolver) Update(base IDnsResolverStrategy, routes []dnsRoute, configs []dnsRouteConfig) {
	r.mu.Lock()
	defer r.mu.Unlock()
	if base != nil {
		r.base = base
	}
	r.routes = routes
	r.configs = configs
}

func (r *dnsRoutingResolver) GetConfigs() []dnsRouteConfig {
	r.mu.RLock()
	defer r.mu.RUnlock()
	cp := make([]dnsRouteConfig, len(r.configs))
	copy(cp, r.configs)
	return cp
}

func (r *dnsRoutingResolver) RouteCount() int {
	r.mu.RLock()
	defer r.mu.RUnlock()
	return len(r.routes)
}

func (r *dnsRoutingResolver) CacheStats() (hits, misses, staleHits, evictions, promotions int64, size int) {
	r.mu.RLock()
	defer r.mu.RUnlock()

	add := func(s interface {
		CacheStats() (int64, int64, int64, int64, int64, int)
	}) {
		h, m, st, e, p, n := s.CacheStats()
		hits += h
		misses += m
		staleHits += st
		evictions += e
		promotions += p
		size += n
	}
	if s, ok := r.base.(interface {
		CacheStats() (int64, int64, int64, int64, int64, int)
	}); ok {
		add(s)
	}
	for _, route := range r.routes {
		add(route.resolver)
	}
	return
}

func validateDNSServer(rawServer string) (string, error) {
	rawServer = strings.TrimSpace(rawServer)
	host, port, err := net.SplitHostPort(rawServer)
	if err != nil {
		return "", errors.New("must be an IP and port")
	}
	ip := net.ParseIP(host)
	if ip == nil || ip.IsUnspecified() || ip.IsMulticast() || ip.Equal(net.IPv4bcast) {
		return "", errors.New("DNS server IP must be a valid non-multicast unicast IP")
	}
	portNumber, err := strconv.Atoi(port)
	if err != nil || portNumber < 1 || portNumber > 65535 {
		return "", errors.New("invalid DNS server port")
	}
	return net.JoinHostPort(host, strconv.Itoa(portNumber)), nil
}

func validDNSSuffix(s string) bool {
	s = strings.Trim(s, ".")
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

func parseDNSRouteConfigs(raw string) ([]dnsRouteConfig, error) {
	if strings.TrimSpace(raw) == "" {
		return nil, nil
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
	return configs, nil
}

func buildDNSRoutes(configs []dnsRouteConfig, timeout time.Duration) ([]dnsRoute, error) {
	if timeout <= 0 {
		timeout = 10 * time.Second
	}
	var routes []dnsRoute
	seen := make(map[string]bool)

	for i, cfg := range configs {
		suffix := NormalizeDomain(cfg.Suffix)
		suffix = strings.Trim(suffix, ".")
		if suffix == "" {
			suffix = "*"
		}
		if (suffix != "*" && !validDNSSuffix(suffix)) || seen[suffix] {
			return nil, fmt.Errorf("invalid or duplicate DNS rule suffix at index %d: %q", i, cfg.Suffix)
		}
		seen[suffix] = true

		rawServers := cfg.Servers
		if len(rawServers) == 0 && strings.TrimSpace(cfg.Server) != "" {
			rawServers = []string{cfg.Server}
		}
		if len(rawServers) == 0 {
			return nil, fmt.Errorf("no DNS server specified for rule at index %d", i)
		}
		if len(rawServers) > 8 {
			return nil, fmt.Errorf("too many DNS servers for rule at index %d (max 8)", i)
		}

		validatedServers := make([]string, 0, len(rawServers))
		for sIdx, srv := range rawServers {
			validSrv, err := validateDNSServer(srv)
			if err != nil {
				return nil, fmt.Errorf("invalid DNS server %q at index %d, server %d: %w", srv, i, sIdx, err)
			}
			validatedServers = append(validatedServers, validSrv)
		}

		softTTL, hardTTL := 3*time.Minute, 10*time.Minute
		var err error
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

		var strategy IDnsResolverStrategy
		if len(validatedServers) == 1 {
			strategy = NewIpv4PreferResolverWithServer(validatedServers[0], timeout)
		} else {
			strategy = NewParallelRacingResolver(validatedServers, timeout)
		}

		cached := NewCachedDnsResolverWithTimeout(strategy, 512, softTTL, hardTTL, 5*time.Second, timeout)
		routes = append(routes, dnsRoute{
			suffix:   suffix,
			servers:  validatedServers,
			softTTL:  softTTL,
			hardTTL:  hardTTL,
			resolver: cached,
		})
	}

	sort.SliceStable(routes, func(i, j int) bool {
		if routes[i].suffix == "*" {
			return false
		}
		if routes[j].suffix == "*" {
			return true
		}
		return len(routes[i].suffix) > len(routes[j].suffix)
	})

	return routes, nil
}

func buildBaseResolver(upstreams []string, timeout time.Duration) IDnsResolverStrategy {
	if timeout <= 0 {
		timeout = 10 * time.Second
	}
	var strategy IDnsResolverStrategy
	validUpstreams := make([]string, 0, len(upstreams))
	for _, u := range upstreams {
		if val, err := validateDNSServer(u); err == nil {
			validUpstreams = append(validUpstreams, val)
		}
	}
	if len(validUpstreams) == 0 {
		strategy = NewIpv4PreferResolver(timeout)
	} else if len(validUpstreams) == 1 {
		strategy = NewIpv4PreferResolverWithServer(validUpstreams[0], timeout)
	} else {
		strategy = NewParallelRacingResolver(validUpstreams, timeout)
	}
	return NewCachedDnsResolverWithTimeout(strategy, 2048, 3*time.Minute, 10*time.Minute, 5*time.Second, timeout)
}

func buildDNSResolver(base IDnsResolverStrategy, raw string, timeout time.Duration) (IDnsResolverStrategy, error) {
	if base == nil {
		return nil, errors.New("default DNS resolver is required")
	}
	if strings.TrimSpace(raw) == "" {
		return &dnsRoutingResolver{base: base}, nil
	}
	configs, err := parseDNSRouteConfigs(raw)
	if err != nil {
		return nil, err
	}
	if len(configs) == 0 {
		return &dnsRoutingResolver{base: base}, nil
	}
	routes, err := buildDNSRoutes(configs, timeout)
	if err != nil {
		return nil, err
	}
	return &dnsRoutingResolver{base: base, routes: routes, configs: configs}, nil
}

func buildDNSResolverWithConfigs(base IDnsResolverStrategy, configs []dnsRouteConfig, timeout time.Duration) (*dnsRoutingResolver, error) {
	if base == nil {
		return nil, errors.New("default DNS resolver is required")
	}
	if len(configs) == 0 {
		return &dnsRoutingResolver{base: base}, nil
	}
	routes, err := buildDNSRoutes(configs, timeout)
	if err != nil {
		return nil, err
	}
	return &dnsRoutingResolver{base: base, routes: routes, configs: configs}, nil
}
