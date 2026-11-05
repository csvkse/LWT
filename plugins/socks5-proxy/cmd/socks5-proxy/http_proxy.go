package main

import (
	"bufio"
	"context"
	"crypto/subtle"
	"encoding/base64"
	"errors"
	"fmt"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"
)

// isHttpProxyMethod checks whether the first byte is a recognized HTTP method character.
func isHttpProxyMethod(b byte) bool {
	return b == 'C' || b == 'G' || b == 'P' || b == 'H' || b == 'D' || b == 'O' || b == 'T'
}

// validateBasicAuth validates RFC 7617 HTTP Basic Authentication against expected user & password.
func validateBasicAuth(authHeader, expectedUser, expectedPass string) bool {
	if !strings.HasPrefix(authHeader, "Basic ") {
		return false
	}
	rawB64 := strings.TrimSpace(strings.TrimPrefix(authHeader, "Basic "))
	decoded, err := base64.StdEncoding.DecodeString(rawB64)
	if err != nil {
		return false
	}
	u, p, ok := strings.Cut(string(decoded), ":")
	if !ok {
		return false
	}
	userMatch := subtle.ConstantTimeCompare([]byte(u), []byte(expectedUser)) == 1
	passMatch := subtle.ConstantTimeCompare([]byte(p), []byte(expectedPass)) == 1
	return userMatch && passMatch
}

type httpForwardRouteContextKey struct{}

type httpForwardRoute struct {
	host string
	port int
	ips  []net.IP
}

// handleHttpProxy adapts an already accepted connection to net/http's server
// lifecycle so active requests inherit client-disconnect cancellation.
func (s *Server) handleHttpProxy(bConn net.Conn, _ *bufio.Reader) error {
	return s.serveHTTPProxy(bConn)
}

func (s *Server) handleHTTPConnect(bConn net.Conn, req *http.Request) error {
	// 2. Extract target host and port
	// e.g. "www.example.com:443" or "127.0.0.1:5271"
	var targetHost, targetPortStr string
	host, port, splitErr := net.SplitHostPort(req.Host)
	if splitErr != nil {
		targetHost = strings.Trim(req.Host, "[]")
		targetPortStr = "443"
	} else {
		targetHost = host
		targetPortStr = port
	}

	targetHost = NormalizeDomain(targetHost)
	targetPort, err := strconv.Atoi(targetPortStr)
	if err != nil || targetPort <= 0 || targetPort > 65535 {
		_, _ = bConn.Write([]byte("HTTP/1.1 400 Bad Request\r\nContent-Length: 20\r\nConnection: close\r\n\r\nInvalid target port.\n"))
		return fmt.Errorf("invalid target port: %s", targetPortStr)
	}

	// 3. ACL Evaluation (Target domain rule)
	if s.acl != nil {
		allowed, needDns := s.acl.EvaluateTarget(targetHost)
		if !allowed && !needDns {
			s.logger.Warn("http connect target rejected by domain policy",
				"client", bConn.RemoteAddr().String(),
				"target", targetHost,
			)
			_, _ = bConn.Write([]byte("HTTP/1.1 403 Forbidden\r\nContent-Length: 26\r\nConnection: close\r\n\r\nTarget forbidden by ACL.\n"))
			s.acl.RecordTargetResult(false)
			return fmt.Errorf("target %s rejected by domain whitelist policy", targetHost)
		}
	}

	// 4. Resolve Target IP (LRU Cache + Singleflight + IPv4-prefer)
	var candidateIPs []net.IP
	if ip := net.ParseIP(targetHost); ip != nil {
		candidateIPs = []net.IP{ip}
	} else {
		resolver := s.getDnsResolver()
		resolveCtx, cancel := context.WithTimeout(req.Context(), s.connectTimeout())
		resolveStarted := time.Now()
		ips, err := resolver.Resolve(resolveCtx, targetHost)
		s.metrics.DNSResolve.Observe(time.Since(resolveStarted))
		cancel()
		if err != nil || len(ips) == 0 {
			_, _ = bConn.Write([]byte("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 20\r\nConnection: close\r\n\r\nDNS lookup failed.\n"))
			if s.acl != nil {
				s.acl.RecordTargetResult(false)
			}
			return fmt.Errorf("dns lookup failed for %s: %w", targetHost, err)
		}
		candidateIPs = ips
	}

	// 5. Connect to upstream target via Happy Eyeballs Race (RFC 8305)
	dialStarted := time.Now()
	upstream, winningIP, err := dialUpstreamWithRace(req.Context(), candidateIPs, targetPort, s.connectTimeout(), s.acl)
	s.metrics.UpstreamDial.Observe(time.Since(dialStarted))
	if err != nil {
		if errors.Is(err, ErrAclForbidden) {
			_, _ = bConn.Write([]byte("HTTP/1.1 403 Forbidden\r\nContent-Length: 35\r\nConnection: close\r\n\r\nResolved IP disallowed by subnet.\n"))
			if s.acl != nil {
				s.acl.RecordTargetResult(false)
			}
			return fmt.Errorf("resolved IP for %s is disbarred by subnet policy: %w", targetHost, err)
		}
		_, _ = bConn.Write([]byte("HTTP/1.1 504 Gateway Timeout\r\nContent-Length: 24\r\nConnection: close\r\n\r\nUpstream connect failed.\n"))
		if s.acl != nil {
			s.acl.RecordTargetResult(false)
		}
		return fmt.Errorf("upstream dial to %s failed: %w", targetHost, err)
	}
	defer upstream.Close()

	if s.acl != nil {
		s.acl.RecordTargetResult(true)
	}
	_ = winningIP

	// Respond 200 Connection Established to client
	if _, err := bConn.Write([]byte("HTTP/1.1 200 Connection Established\r\n\r\n")); err != nil {
		return fmt.Errorf("send 200 connection established failed: %w", err)
	}

	_ = bConn.SetDeadline(time.Time{})
	_ = upstream.SetDeadline(time.Time{})

	s.logger.Info("http connect tunnel established",
		"client", bConn.RemoteAddr().String(),
		"target", targetHost+":"+targetPortStr,
	)
	return s.relay(bConn, upstream)
}

var errInvalidHTTPProxyTarget = errors.New("invalid HTTP proxy target")

func (s *Server) prepareHTTPForwardRoute(req *http.Request) (httpForwardRoute, error) {
	if req.URL == nil {
		return httpForwardRoute{}, errInvalidHTTPProxyTarget
	}
	scheme := strings.ToLower(req.URL.Scheme)
	if scheme == "" {
		scheme = "http"
	}
	if scheme != "http" && scheme != "https" {
		return httpForwardRoute{}, errInvalidHTTPProxyTarget
	}
	if req.URL.Host == "" {
		req.URL.Host = req.Host
	}
	host := NormalizeDomain(req.URL.Hostname())
	if host == "" {
		return httpForwardRoute{}, errInvalidHTTPProxyTarget
	}
	port := 80
	if scheme == "https" {
		port = 443
	}
	if portText := req.URL.Port(); portText != "" {
		parsedPort, err := strconv.Atoi(portText)
		if err != nil || parsedPort < 1 || parsedPort > 65535 {
			return httpForwardRoute{}, errInvalidHTTPProxyTarget
		}
		port = parsedPort
	}
	req.URL.Scheme = scheme
	req.URL.Host = net.JoinHostPort(host, strconv.Itoa(port))
	return s.resolveHTTPForwardRoute(req.Context(), host, port)
}

func (s *Server) getHTTPForwardTransport() *http.Transport {
	s.httpForwardMu.Lock()
	defer s.httpForwardMu.Unlock()
	if s.httpForwardTransport == nil {
		s.httpForwardLimiter = NewHTTPHostRequestLimiter(s.effectiveMaxHTTPConnsPerHost(), s.effectiveMaxHTTPPendingPerHost())
		s.httpForwardTransport = &http.Transport{
			DialContext:           s.dialHTTPForwardTarget,
			MaxIdleConns:          128,
			MaxIdleConnsPerHost:   16,
			MaxConnsPerHost:       s.effectiveMaxHTTPConnsPerHost(),
			IdleConnTimeout:       90 * time.Second,
			TLSHandshakeTimeout:   s.connectTimeout(),
			ResponseHeaderTimeout: s.effectiveHTTPResponseHeaderTimeout(),
			ExpectContinueTimeout: time.Second,
		}
	}
	return s.httpForwardTransport
}

func (s *Server) getHTTPForwardRequestLimiter() *HTTPHostRequestLimiter {
	s.httpForwardMu.Lock()
	defer s.httpForwardMu.Unlock()
	if s.httpForwardLimiter == nil {
		s.httpForwardLimiter = NewHTTPHostRequestLimiter(s.effectiveMaxHTTPConnsPerHost(), s.effectiveMaxHTTPPendingPerHost())
	}
	return s.httpForwardLimiter
}

func (s *Server) effectiveMaxHTTPConnsPerHost() int {
	if s.cfg.MaxHTTPConnsPerHost > 0 {
		return s.cfg.MaxHTTPConnsPerHost
	}
	return 128
}

func (s *Server) effectiveMaxHTTPPendingPerHost() int {
	if s.cfg.MaxHTTPPendingPerHost > 0 {
		return s.cfg.MaxHTTPPendingPerHost
	}
	return defaultMaxHTTPPendingPerHost
}

func (s *Server) effectiveHTTPQueueTimeout() time.Duration {
	if s.cfg.HTTPQueueTimeout > 0 {
		return s.cfg.HTTPQueueTimeout
	}
	return defaultHTTPQueueTimeout
}

func (s *Server) effectiveHTTPResponseHeaderTimeout() time.Duration {
	if s.cfg.HTTPResponseHeaderTimeout > 0 {
		return s.cfg.HTTPResponseHeaderTimeout
	}
	return 30 * time.Second
}

func (s *Server) closeHTTPForwardIdleConnections() {
	s.httpForwardMu.Lock()
	transport := s.httpForwardTransport
	s.httpForwardMu.Unlock()
	if transport != nil {
		transport.CloseIdleConnections()
	}
}

func (s *Server) dialHTTPForwardTarget(ctx context.Context, network, addr string) (net.Conn, error) {
	host, portText, err := net.SplitHostPort(addr)
	if err != nil {
		return nil, fmt.Errorf("parse HTTP upstream address: %w", err)
	}
	port, err := strconv.Atoi(portText)
	if err != nil || port < 1 || port > 65535 {
		return nil, errInvalidHTTPProxyTarget
	}
	host = NormalizeDomain(host)
	var route httpForwardRoute
	if candidate, ok := ctx.Value(httpForwardRouteContextKey{}).(httpForwardRoute); ok && candidate.host == host && candidate.port == port {
		route = candidate
	} else {
		route, err = s.resolveHTTPForwardRoute(ctx, host, port)
		if err != nil {
			return nil, err
		}
	}
	conn, _, err := s.dialHTTPForwardRoute(ctx, route)
	return conn, err
}

func (s *Server) resolveHTTPForwardRoute(ctx context.Context, host string, port int) (httpForwardRoute, error) {
	if s.acl != nil {
		allowed, needDns := s.acl.EvaluateTarget(host)
		if !allowed && !needDns {
			return httpForwardRoute{}, ErrAclForbidden
		}
	}
	var ips []net.IP
	if ip := net.ParseIP(host); ip != nil {
		ips = []net.IP{ip}
	} else {
		resolveCtx, cancel := context.WithTimeout(ctx, s.connectTimeout())
		started := time.Now()
		var resolveErr error
		ips, resolveErr = s.getDnsResolver().Resolve(resolveCtx, host)
		s.metrics.DNSResolve.Observe(time.Since(started))
		cancel()
		if resolveErr != nil {
			return httpForwardRoute{}, fmt.Errorf("resolve %s: %w", host, resolveErr)
		}
	}
	if len(ips) == 0 {
		return httpForwardRoute{}, errors.New("DNS lookup returned no addresses")
	}
	if s.acl != nil {
		allowedIP := false
		for _, ip := range ips {
			if s.acl.IsIpAllowed(ip) {
				allowedIP = true
				break
			}
		}
		if !allowedIP {
			return httpForwardRoute{}, ErrAclForbidden
		}
		s.acl.RecordTargetResult(true)
	}
	return httpForwardRoute{host: host, port: port, ips: ips}, nil
}

func (s *Server) dialHTTPForwardRoute(ctx context.Context, route httpForwardRoute) (net.Conn, net.IP, error) {
	started := time.Now()
	conn, ip, err := dialUpstreamWithRace(ctx, route.ips, route.port, s.connectTimeout(), s.acl)
	s.metrics.UpstreamDial.Observe(time.Since(started))
	return conn, ip, err
}

func (s *Server) connectTimeout() time.Duration {
	if s.cfg.ConnectTimeout > 0 {
		return s.cfg.ConnectTimeout
	}
	return 10 * time.Second
}
