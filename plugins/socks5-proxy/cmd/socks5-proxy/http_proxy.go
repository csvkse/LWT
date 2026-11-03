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

// handleHttpProxy handles HTTP forward proxy requests (CONNECT tunnel for HTTPS, or direct GET/POST).
func (s *Server) handleHttpProxy(bConn net.Conn, br *bufio.Reader) error {
	// Read the HTTP request from client
	req, err := http.ReadRequest(br)
	if err != nil {
		return fmt.Errorf("read http proxy request failed: %w", err)
	}
	defer req.Body.Close()

	// 1. Authenticate if required
	authMode, serverUser, serverPass := s.GetAuthConfig()
	if authMode == "password" {
		authHeader := req.Header.Get("Proxy-Authorization")
		if !validateBasicAuth(authHeader, serverUser, serverPass) {
			if s.acl != nil {
				s.acl.authFailures.Add(1)
			}
			s.logger.Warn("http proxy authentication failed",
				"client", bConn.RemoteAddr().String(),
				"target", req.Host,
			)
			resp := "HTTP/1.1 407 Proxy Authentication Required\r\n" +
				"Proxy-Authenticate: Basic realm=\"LWT Dual-Mode Secure Proxy\"\r\n" +
				"Content-Length: 32\r\n" +
				"Connection: close\r\n\r\n" +
				"Proxy authentication required.\n"
			_, _ = bConn.Write([]byte(resp))
			return errors.New("http proxy authentication required")
		}
	}

	// 2. Extract target host and port
	var targetHost, targetPortStr string
	if req.Method == http.MethodConnect {
		// e.g. "www.example.com:443" or "10.126.1.70:5271"
		host, port, err := net.SplitHostPort(req.Host)
		if err != nil {
			targetHost = req.Host
			targetPortStr = "443"
		} else {
			targetHost = host
			targetPortStr = port
		}
	} else {
		// Plain HTTP proxy (e.g. GET http://example.com/index.html HTTP/1.1)
		host := req.URL.Host
		if host == "" {
			host = req.Host
		}
		if h, p, err := net.SplitHostPort(host); err == nil {
			targetHost = h
			targetPortStr = p
		} else {
			targetHost = host
			targetPortStr = "80"
		}
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
		resolver := s.resolver
		if resolver == nil {
			resolver = NewCachedDnsResolver(NewIpv4PreferResolver(s.cfg.ConnectTimeout), 2048, 3*time.Minute, 10*time.Minute, 5*time.Second)
		}
		resolveCtx, cancel := context.WithTimeout(context.Background(), s.cfg.ConnectTimeout)
		ips, err := resolver.Resolve(resolveCtx, targetHost)
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
	upstream, winningIP, err := dialUpstreamWithRace(context.Background(), candidateIPs, targetPort, s.cfg.ConnectTimeout, s.acl)
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

	// 7. Branch: CONNECT tunnel vs Standard HTTP Forward
	if req.Method == http.MethodConnect {
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

	// Standard Plain HTTP forward: strip proxy headers and forward
	req.Header.Del("Proxy-Authorization")
	req.Header.Del("Proxy-Connection")

	if err := req.WriteProxy(upstream); err != nil {
		return fmt.Errorf("write http request to upstream failed: %w", err)
	}

	_ = bConn.SetDeadline(time.Time{})
	_ = upstream.SetDeadline(time.Time{})

	s.logger.Info("http forward request pipelined",
		"client", bConn.RemoteAddr().String(),
		"method", req.Method,
		"target", req.URL.String(),
	)
	return s.relay(bConn, upstream)
}
