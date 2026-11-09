package main

import (
	"context"
	"encoding/binary"
	"net"
	"sync/atomic"
	"testing"
	"time"
)

func startDelayedDNSServer(t *testing.T, lastByte byte, delay time.Duration) (string, *atomic.Int64) {
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
			if delay > 0 {
				time.Sleep(delay)
			}
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

func TestParallelRacingResolver_FastestWins(t *testing.T) {
	fastServer, fastCalls := startDelayedDNSServer(t, 10, 0)
	slowServer, _ := startDelayedDNSServer(t, 20, 200*time.Millisecond)

	racing := NewParallelRacingResolver([]string{slowServer, fastServer}, 2*time.Second)

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()

	ips, err := racing.Resolve(ctx, "speedtest.example.com")
	if err != nil {
		t.Fatalf("racing resolve failed: %v", err)
	}
	if len(ips) == 0 {
		t.Fatalf("expected at least 1 resolved IP")
	}

	// Fast server returned 203.0.113.10
	expectedIP := net.ParseIP("203.0.113.10")
	if !ips[0].Equal(expectedIP) {
		t.Fatalf("expected fastest server IP %v, got %v", expectedIP, ips[0])
	}

	if fastCalls.Load() == 0 {
		t.Fatalf("expected fast server to receive query")
	}
}

func TestParallelRacingResolver_OneDeadOneHealthy(t *testing.T) {
	deadServer := "127.0.0.1:1" // Closed port
	healthyServer, _ := startDelayedDNSServer(t, 55, 0)

	racing := NewParallelRacingResolver([]string{deadServer, healthyServer}, 2*time.Second)

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()

	ips, err := racing.Resolve(ctx, "failover.example.com")
	if err != nil {
		t.Fatalf("expected healthy server to succeed despite dead server, got: %v", err)
	}

	expectedIP := net.ParseIP("203.0.113.55")
	if !ips[0].Equal(expectedIP) {
		t.Fatalf("expected healthy IP %v, got %v", expectedIP, ips[0])
	}
}

func TestBuildDNSRoutes_MultiServers(t *testing.T) {
	configs := []dnsRouteConfig{
		{
			Suffix:  "bilibili.com",
			Servers: []string{"223.5.5.5:53", "119.29.29.29:53"},
			SoftTTL: "5m",
			HardTTL: "15m",
		},
		{
			Suffix:  "single.example.com",
			Server:  "1.1.1.1:53",
		},
	}

	routes, err := buildDNSRoutes(configs, 5*time.Second)
	if err != nil {
		t.Fatalf("buildDNSRoutes failed: %v", err)
	}
	if len(routes) != 2 {
		t.Fatalf("expected 2 routes, got %d", len(routes))
	}

	// Longest suffix first
	if routes[0].suffix != "single.example.com" {
		t.Fatalf("expected single.example.com first due to length, got %s", routes[0].suffix)
	}
	if routes[1].suffix != "bilibili.com" {
		t.Fatalf("expected bilibili.com second, got %s", routes[1].suffix)
	}
	if len(routes[1].servers) != 2 {
		t.Fatalf("expected 2 servers for bilibili.com route, got %d", len(routes[1].servers))
	}
}
