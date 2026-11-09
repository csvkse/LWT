package main

import (
	"bufio"
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net"
	"net/http"
	"sync/atomic"
	"testing"
	"time"
)

func startTestDNSServer(t *testing.T, lastByte byte) (string, *atomic.Int64) {
	t.Helper()
	conn, err := net.ListenPacket("udp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = conn.Close() })
	calls := &atomic.Int64{}
	go func() {
		buf := make([]byte, 2048)
		for {
			n, addr, err := conn.ReadFrom(buf)
			if err != nil {
				return
			}
			if n < 17 {
				continue
			}
			end := 12
			for end < n && buf[end] != 0 {
				end += int(buf[end]) + 1
			}
			end += 5
			if end > n {
				continue
			}
			calls.Add(1)
			response := make([]byte, 12, 12+end-12+16)
			copy(response[:2], buf[:2])
			binary.BigEndian.PutUint16(response[2:4], 0x8180)
			binary.BigEndian.PutUint16(response[4:6], 1)
			binary.BigEndian.PutUint16(response[6:8], 1)
			response = append(response, buf[12:end]...)
			response = append(response, 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 203, 0, 113, lastByte)
			_, _ = conn.WriteTo(response, addr)
		}
	}()
	return conn.LocalAddr().String(), calls
}

func TestDNSRoutingLongestSuffixAndIndependentCaches(t *testing.T) {
	first, firstCalls := startTestDNSServer(t, 10)
	second, secondCalls := startTestDNSServer(t, 20)
	base := &mockResolver{resultIPs: []net.IP{net.ParseIP("203.0.113.30")}}
	rules, _ := json.Marshal([]map[string]string{{"suffix": "example.test", "server": first}, {"suffix": "special.example.test", "server": second}})
	resolver, err := buildDNSResolver(base, string(rules), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct{ host, want string }{
		{"WWW.Special.Example.Test.", "203.0.113.20"},
		{"www.example.test", "203.0.113.10"},
		{"notexample.test", "203.0.113.30"},
		{"www.example.test", "203.0.113.10"},
		{"203.0.113.99", "203.0.113.99"},
	} {
		ips, err := resolver.Resolve(context.Background(), tc.host)
		if err != nil || len(ips) != 1 || ips[0].String() != tc.want {
			t.Fatalf("Resolve(%q) = %v, %v; want %s", tc.host, ips, err, tc.want)
		}
	}
	if firstCalls.Load() != 1 || secondCalls.Load() != 1 || atomic.LoadInt64(&base.callCount) != 1 {
		t.Fatalf("unexpected calls: first=%d second=%d default=%d", firstCalls.Load(), secondCalls.Load(), atomic.LoadInt64(&base.callCount))
	}
	stats, ok := resolver.(interface {
		CacheStats() (int64, int64, int64, int64, int64, int)
	})
	if !ok {
		t.Fatal("routing resolver lost cache statistics")
	}
	_, _, _, _, _, size := stats.CacheStats()
	if size != 2 {
		t.Fatalf("aggregate cache size = %d, want 2", size)
	}
}

func TestDNSRoutingRejectsBadConfiguration(t *testing.T) {
	for _, config := range []string{
		`not-json`,
		`[{"suffix":"example.test","server":"dns.example.test:53"}]`,
		`[{"suffix":"example.test","server":"0.0.0.0:53"}]`,
		`[{"suffix":"example.test","server":"224.0.0.1:53"}]`,
		`[{"suffix":"*.example.test","server":"127.0.0.1:53"}]`,
		`[{"suffix":"example.test","server":"127.0.0.1:53"},{"suffix":"EXAMPLE.TEST.","server":"127.0.0.1:53"}]`,
		`[{"suffix":"example.test","server":"127.0.0.1:53","softTtl":"2m","hardTtl":"1m"}]`,
		`[{"suffix":"example.test","server":"127.0.0.1:53","softTtl":"100ms"}]`,
		`[{"suffix":"example.test","server":"127.0.0.1:53","unknown":"x"}]`,
		`[{"suffix":"example.test","server":"127.0.0.1:53"}] true`,
	} {
		if _, err := buildDNSResolver(&mockResolver{}, config, time.Second); err == nil {
			t.Fatalf("expected invalid DNS config to be rejected: %s", config)
		}
	}
}

func TestDNSRoutingResolvedAddressStillEnforcesACL(t *testing.T) {
	server, _ := startTestDNSServer(t, 10)
	rules, _ := json.Marshal([]map[string]string{{"suffix": "example.test", "server": server}})
	resolver, err := buildDNSResolver(&mockResolver{}, string(rules), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	ips, err := resolver.Resolve(context.Background(), "cdn.example.test")
	if err != nil {
		t.Fatal(err)
	}
	acl, err := NewAclEngine("loopback", "", "", "")
	if err != nil {
		t.Fatal(err)
	}
	conn, _, err := dialUpstreamWithRace(context.Background(), ips, 443, time.Second, acl)
	if conn != nil {
		_ = conn.Close()
		t.Fatal("unexpected connection to forbidden address")
	}
	if !errors.Is(err, ErrAclForbidden) {
		t.Fatalf("expected ACL rejection, got %v", err)
	}
}

func TestSOCKS5SplitDNSAppliesACLOnResolvedAddress(t *testing.T) {
	server, calls := startTestDNSServer(t, 10)
	rules, _ := json.Marshal([]map[string]string{{"suffix": "example.test", "server": server}})
	resolver, err := buildDNSResolver(&mockResolver{}, string(rules), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	acl, err := NewAclEngine("loopback", "", "", "")
	if err != nil {
		t.Fatal(err)
	}
	srv := &Server{cfg: Config{AuthMode: "none", ConnectTimeout: time.Second}, resolver: resolver, acl: acl, logger: slog.New(slog.NewTextHandler(io.Discard, nil))}
	client, serverConn := net.Pipe()
	defer client.Close()
	done := make(chan error, 1)
	go func() { defer serverConn.Close(); done <- srv.handleConn(serverConn) }()
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))
	if _, err := client.Write([]byte{5, 1, 0}); err != nil {
		t.Fatal(err)
	}
	greeting := make([]byte, 2)
	if _, err := io.ReadFull(client, greeting); err != nil || greeting[0] != 5 || greeting[1] != 0 {
		t.Fatalf("greeting = %v, %v", greeting, err)
	}
	host := []byte("cdn.example.test")
	request := append([]byte{5, 1, 0, 3, byte(len(host))}, host...)
	request = append(request, 1, 187)
	if _, err := client.Write(request); err != nil {
		t.Fatal(err)
	}
	reply := make([]byte, 10)
	if _, err := io.ReadFull(client, reply); err != nil {
		t.Fatal(err)
	}
	if reply[0] != 5 || reply[1] != 2 {
		t.Fatalf("SOCKS reply = %v, want ACL rejection", reply)
	}
	if calls.Load() != 1 {
		t.Fatalf("DNS queries = %d, want 1", calls.Load())
	}
	select {
	case err := <-done:
		if !errors.Is(err, ErrAclForbidden) {
			t.Fatalf("handler error = %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("handler did not finish")
	}
}

func TestHTTPConnectSplitDNSAppliesACLOnResolvedAddress(t *testing.T) {
	server, calls := startTestDNSServer(t, 10)
	rules, _ := json.Marshal([]map[string]string{{"suffix": "example.test", "server": server}})
	resolver, err := buildDNSResolver(&mockResolver{}, string(rules), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	acl, err := NewAclEngine("loopback", "", "", "")
	if err != nil {
		t.Fatal(err)
	}
	srv := &Server{cfg: Config{AuthMode: "none", ConnectTimeout: time.Second}, resolver: resolver, acl: acl, logger: slog.New(slog.NewTextHandler(io.Discard, nil))}
	client, serverConn := net.Pipe()
	defer client.Close()
	done := make(chan error, 1)
	go func() { defer serverConn.Close(); done <- srv.handleConn(serverConn) }()
	_ = client.SetDeadline(time.Now().Add(3 * time.Second))
	if _, err := io.WriteString(client, "CONNECT cdn.example.test:443 HTTP/1.1\r\nHost: cdn.example.test:443\r\nConnection: close\r\n\r\n"); err != nil {
		t.Fatal(err)
	}
	response, err := http.ReadResponse(bufio.NewReader(client), &http.Request{Method: http.MethodConnect})
	if err != nil {
		t.Fatal(err)
	}
	_, _ = io.Copy(io.Discard, response.Body)
	_ = response.Body.Close()
	if response.StatusCode != http.StatusForbidden {
		t.Fatalf("HTTP status = %d, want 403", response.StatusCode)
	}
	if calls.Load() != 1 {
		t.Fatalf("DNS queries = %d, want 1", calls.Load())
	}
	_ = client.Close()
	select {
	case <-done:
	case <-time.After(time.Second):
		t.Fatal("handler did not finish")
	}
}

func TestDNSRoutingDoesNotFallbackOnUpstreamFailure(t *testing.T) {
	base := &mockResolver{resultIPs: []net.IP{net.ParseIP("203.0.113.30")}}
	rules := `[{"suffix":"example.test","server":"127.0.0.1:1"}]`
	resolver, err := buildDNSResolver(base, rules, 100*time.Millisecond)
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), 300*time.Millisecond)
	defer cancel()
	if _, err := resolver.Resolve(ctx, "secret.example.test"); err == nil {
		t.Fatal("expected configured resolver failure")
	}
	if atomic.LoadInt64(&base.callCount) != 0 {
		t.Fatal("failed rule leaked lookup to default resolver")
	}
}

func TestDNSRoutingCatchAllAndSpecificRule(t *testing.T) {
	defaultServer, defaultCalls := startTestDNSServer(t, 10)
	specificServer, specificCalls := startTestDNSServer(t, 20)
	base := &mockResolver{resultIPs: []net.IP{net.ParseIP("203.0.113.30")}}
	rules, _ := json.Marshal([]map[string]string{{"suffix": "*", "server": defaultServer}, {"suffix": "x.test", "server": specificServer}})
	resolver, err := buildDNSResolver(base, string(rules), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	for _, tc := range []struct{ host, want string }{{"cdn.x.test", "203.0.113.20"}, {"other.test", "203.0.113.10"}} {
		ips, err := resolver.Resolve(context.Background(), tc.host)
		if err != nil || len(ips) != 1 || ips[0].String() != tc.want {
			t.Fatalf("Resolve(%q) = %v, %v; want %s", tc.host, ips, err, tc.want)
		}
	}
	if defaultCalls.Load() != 1 || specificCalls.Load() != 1 || atomic.LoadInt64(&base.callCount) != 0 {
		t.Fatal("catch-all did not route as expected")
	}
}

func TestDNSRoutingFallsBackToTCPOnTruncatedUDP(t *testing.T) {
	tcp, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer tcp.Close()
	udp, err := net.ListenPacket("udp", tcp.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer udp.Close()
	var tcpCalls atomic.Int64
	answer := func(q []byte, truncated bool) []byte {
		end := 12
		for end < len(q) && q[end] != 0 {
			end += int(q[end]) + 1
		}
		end += 5
		if end > len(q) {
			return nil
		}
		response := make([]byte, 12)
		copy(response[:2], q[:2])
		flags := uint16(0x8180)
		if truncated {
			flags |= 0x0200
		} else {
			binary.BigEndian.PutUint16(response[6:8], 1)
		}
		binary.BigEndian.PutUint16(response[2:4], flags)
		binary.BigEndian.PutUint16(response[4:6], 1)
		response = append(response, q[12:end]...)
		if !truncated {
			response = append(response, 0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 203, 0, 113, 40)
		}
		return response
	}
	go func() {
		buf := make([]byte, 2048)
		for {
			n, addr, err := udp.ReadFrom(buf)
			if err != nil {
				return
			}
			if n >= 17 {
				_, _ = udp.WriteTo(answer(buf[:n], true), addr)
			}
		}
	}()
	go func() {
		for {
			conn, err := tcp.Accept()
			if err != nil {
				return
			}
			go func() {
				defer conn.Close()
				var size [2]byte
				if _, err := io.ReadFull(conn, size[:]); err != nil {
					return
				}
				q := make([]byte, binary.BigEndian.Uint16(size[:]))
				if _, err := io.ReadFull(conn, q); err != nil {
					return
				}
				tcpCalls.Add(1)
				response := answer(q, false)
				binary.BigEndian.PutUint16(size[:], uint16(len(response)))
				_, _ = conn.Write(append(size[:], response...))
			}()
		}
	}()
	rules, _ := json.Marshal([]map[string]string{{"suffix": "example.test", "server": tcp.Addr().String()}})
	resolver, err := buildDNSResolver(&mockResolver{}, string(rules), time.Second)
	if err != nil {
		t.Fatal(err)
	}
	ips, err := resolver.Resolve(context.Background(), "www.example.test")
	if err != nil || len(ips) != 1 || ips[0].String() != "203.0.113.40" {
		t.Fatalf("TCP fallback = %v, %v", ips, err)
	}
	if tcpCalls.Load() == 0 {
		t.Fatal("DNS TCP fallback was not used")
	}
}
