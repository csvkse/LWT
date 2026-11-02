package main

import (
	"encoding/json"
	"log/slog"
	"net"
	"os"
	"path/filepath"
	"testing"
)

func TestSubnetRulePrefixAndCidr(t *testing.T) {
	engine, err := NewAclEngine("lan", "10., 192.168., 172.20.0.0/16", "", "")
	if err != nil {
		t.Fatalf("failed to create engine: %v", err)
	}

	tests := []struct {
		ip      string
		allowed bool
	}{
		{"10.0.0.1", true},
		{"10.126.1.70", true},
		{"192.168.1.1", true},
		{"192.168.100.5", true},
		{"172.20.5.10", true},
		{"127.0.0.1", true}, // from lan preset
		{"8.8.8.8", false},
		{"1.1.1.1", false},
		{"172.32.0.1", false}, // outside 172.16.0.0/12
	}

	for _, tt := range tests {
		ip := net.ParseIP(tt.ip)
		if got := engine.IsIpAllowed(ip); got != tt.allowed {
			t.Errorf("IsIpAllowed(%s) = %v; want %v", tt.ip, got, tt.allowed)
		}
	}
}

func TestDomainWildcardAndRegex(t *testing.T) {
	engine, err := NewAclEngine("lan", "", "*.internal, api-*.lan, regex:^([a-z0-9-]+\\.)?corp$", "")
	if err != nil {
		t.Fatalf("failed to create engine: %v", err)
	}

	tests := []struct {
		target  string
		allowed bool
	}{
		{"gitlab.internal", true},
		{"auth.sso.internal", true},
		{"internal", true}, // root domain of *.internal
		{"api-v1.lan", true},
		{"api-test.lan", true},
		{"dev.corp", true},
		{"corp", true},
		{"localhost", true},       // from lan preset
		{"myhost.local", true},    // from lan preset (*.local)
		{"public-site.com", false},
		{"evilcorp.com", false},
		{"api-other.org", false},
	}

	for _, tt := range tests {
		allowed, _ := engine.EvaluateTarget(tt.target)
		if allowed != tt.allowed {
			t.Errorf("EvaluateTarget(%s) = %v; want %v", tt.target, allowed, tt.allowed)
		}
	}
}

func TestAclReload(t *testing.T) {
	engine, err := NewAclEngine("loopback", "", "", "")
	if err != nil {
		t.Fatalf("failed to create engine: %v", err)
	}

	// In loopback preset, 10.0.0.1 is denied
	if engine.IsIpAllowed(net.ParseIP("10.0.0.1")) {
		t.Errorf("expected 10.0.0.1 to be denied under loopback preset")
	}

	// Reload with 10.
	err = engine.Reload("loopback", "10.", "", "")
	if err != nil {
		t.Fatalf("reload failed: %v", err)
	}

	// Now 10.0.0.1 should be allowed
	if !engine.IsIpAllowed(net.ParseIP("10.0.0.1")) {
		t.Errorf("expected 10.0.0.1 to be allowed after reload")
	}
}

func TestAclAllowAll(t *testing.T) {
	engine, err := NewAclEngine("all", "", "", "")
	if err != nil {
		t.Fatalf("failed to create engine: %v", err)
	}

	if !engine.IsIpAllowed(net.ParseIP("8.8.8.8")) {
		t.Errorf("expected 8.8.8.8 to be allowed under all preset")
	}

	allowed, needDns := engine.EvaluateTarget("google.com")
	if !allowed || needDns {
		t.Errorf("expected google.com to be allowed immediately under all preset")
	}
}

func TestCidrNormalization(t *testing.T) {
	cases := []struct {
		input       string
		expectedCidr string
	}{
		{"10.", "10.0.0.0/8"},
		{"172.16.", "172.16.0.0/12"},
		{"192.168.", "192.168.0.0/16"},
		{"127.", "127.0.0.0/8"},
		{"169.254.", "169.254.0.0/16"},
		{"10.126.", "10.126.0.0/16"},
		{"192.168.1.100", "192.168.1.100/32"},
		{"10.0.0.0/8", "10.0.0.0/8"},
		{"::1", "::1/128"},
	}

	for _, c := range cases {
		cidr, _ := normalizeSubnetCidr(c.input)
		if cidr != c.expectedCidr {
			t.Errorf("normalizeSubnetCidr(%s) = %s; want %s", c.input, cidr, c.expectedCidr)
		}
	}
}

func TestClientIngressAcl(t *testing.T) {
	// 1. Lan preset default: allow local/lan clients, deny public client
	engineLan, err := NewAclEngine("lan", "", "", "")
	if err != nil {
		t.Fatalf("failed to create lan engine: %v", err)
	}
	if !engineLan.IsClientAllowed("127.0.0.1:54321") {
		t.Errorf("expected 127.0.0.1 to be allowed as client")
	}
	if !engineLan.IsClientAllowed("192.168.1.50:12345") {
		t.Errorf("expected 192.168.1.50 to be allowed as client")
	}
	if engineLan.IsClientAllowed("203.0.113.5:9999") {
		t.Errorf("expected public IP 203.0.113.5 to be denied as client")
	}

	// 2. Loopback preset: only 127.0.0.1 and ::1 allowed
	engineLoop, err := NewAclEngine("loopback", "", "", "")
	if err != nil {
		t.Fatalf("failed to create loopback engine: %v", err)
	}
	if !engineLoop.IsClientAllowed("127.0.0.1:54321") {
		t.Errorf("expected 127.0.0.1 to be allowed in loopback preset")
	}
	if engineLoop.IsClientAllowed("192.168.1.50:12345") {
		t.Errorf("expected 192.168.1.50 to be denied in loopback preset")
	}

	// 3. Custom clients
	engineCustom, err := NewAclEngine("lan", "", "", "10.126.0.0/16, 127.0.0.1/32")
	if err != nil {
		t.Fatalf("failed to create custom engine: %v", err)
	}
	if !engineCustom.IsClientAllowed("10.126.1.70:55555") {
		t.Errorf("expected 10.126.1.70 to be allowed")
	}
	if engineCustom.IsClientAllowed("192.168.1.1:55555") {
		t.Errorf("expected 192.168.1.1 to be denied")
	}
}

func TestAuthFailureMetrics(t *testing.T) {
	engine, err := NewAclEngine("lan", "", "", "")
	if err != nil {
		t.Fatalf("failed to create engine: %v", err)
	}

	engine.RecordAuthFailure()
	engine.RecordAuthFailure()

	stats := engine.GetStats()
	if stats.AuthFailures != 2 {
		t.Errorf("expected AuthFailures=2, got %d", stats.AuthFailures)
	}
}

func TestPersistConfigAtomicAndHydration(t *testing.T) {
	tmpDir := t.TempDir()
	configPath := filepath.Join(tmpDir, "socks5-config.json")

	srv := &Server{
		cfg: Config{
			AuthMode:       "password",
			Username:       "testadmin",
			Password:       "supersecret",
			AclPreset:      "loopback",
			AllowedSubnets: "10.0.0.0/8,192.168.0.0/16",
			AllowedDomains: "*.corp,*.internal",
			AllowedClients: "127.0.0.1",
		},
		logger:     slog.Default(),
		configPath: configPath,
		stateDir:   tmpDir,
	}

	if err := srv.persistConfigAtomic(); err != nil {
		t.Fatalf("persistConfigAtomic failed: %v", err)
	}

	data, err := os.ReadFile(configPath)
	if err != nil {
		t.Fatalf("read persisted config failed: %v", err)
	}

	var p PersistedConfig
	if err := json.Unmarshal(data, &p); err != nil {
		t.Fatalf("unmarshal persisted config failed: %v", err)
	}

	if p.AuthMode != "password" || p.Username != "testadmin" || p.Password != "supersecret" {
		t.Errorf("unexpected auth config: %+v", p)
	}
	if p.AclPreset != "loopback" || p.AllowedSubnets != "10.0.0.0/8,192.168.0.0/16" {
		t.Errorf("unexpected acl config: %+v", p)
	}
}
