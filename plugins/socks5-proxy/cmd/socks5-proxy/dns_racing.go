package main

import (
	"context"
	"net"
	"strings"
	"time"
)

// ParallelRacingResolver queries multiple upstream DNS servers concurrently
// and returns the first successful IP set, immediately cancelling in-flight queries.
type ParallelRacingResolver struct {
	resolvers []*Ipv4PreferResolver
	servers   []string
	timeout   time.Duration
}

func NewParallelRacingResolver(servers []string, timeout time.Duration) *ParallelRacingResolver {
	if timeout <= 0 {
		timeout = 5 * time.Second
	}
	cleanServers := make([]string, 0, len(servers))
	resolvers := make([]*Ipv4PreferResolver, 0, len(servers))
	for _, srv := range servers {
		s := strings.TrimSpace(srv)
		if s == "" {
			continue
		}
		cleanServers = append(cleanServers, s)
		resolvers = append(resolvers, NewIpv4PreferResolverWithServer(s, timeout))
	}
	return &ParallelRacingResolver{
		resolvers: resolvers,
		servers:   cleanServers,
		timeout:   timeout,
	}
}

func (r *ParallelRacingResolver) Servers() []string {
	cp := make([]string, len(r.servers))
	copy(cp, r.servers)
	return cp
}

func (r *ParallelRacingResolver) Resolve(ctx context.Context, host string) ([]net.IP, error) {
	norm := NormalizeDomain(host)
	if ip := net.ParseIP(norm); ip != nil {
		return []net.IP{ip}, nil
	}

	if len(r.resolvers) == 0 {
		return nil, ErrNoIPResolved
	}
	if len(r.resolvers) == 1 {
		return r.resolvers[0].Resolve(ctx, norm)
	}

	raceCtx, cancel := context.WithTimeout(ctx, r.timeout)
	defer cancel()

	type raceResult struct {
		ips []net.IP
		err error
	}
	resultCh := make(chan raceResult, len(r.resolvers))

	for _, resolver := range r.resolvers {
		go func(res *Ipv4PreferResolver) {
			ips, err := res.Resolve(raceCtx, norm)
			resultCh <- raceResult{ips: ips, err: err}
		}(resolver)
	}

	var lastErr error
	for i := 0; i < len(r.resolvers); i++ {
		select {
		case res := <-resultCh:
			if res.err == nil && len(res.ips) > 0 {
				cancel() // First valid response wins; cancel slower upstreams
				return res.ips, nil
			}
			lastErr = res.err
		case <-ctx.Done():
			return nil, ctx.Err()
		}
	}

	if lastErr != nil {
		return nil, lastErr
	}
	return nil, ErrNoIPResolved
}

func (r *ParallelRacingResolver) PromoteWinningIP(host string, ip net.IP) bool {
	return false
}
