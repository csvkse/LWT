package main

import (
	"runtime"
	"testing"
)

func TestCheckTunEnvironment(t *testing.T) {
	status := CheckTunEnvironment()
	if status.InterfaceName != "lwt-tun0" {
		t.Errorf("expected interfaceName lwt-tun0, got %s", status.InterfaceName)
	}
	if status.RecommendedMode != "gvisor_netstack" {
		t.Errorf("expected recommendedMode gvisor_netstack, got %s", status.RecommendedMode)
	}

	if runtime.GOOS == "windows" {
		if status.PlatformSupported {
			t.Errorf("expected PlatformSupported false on Windows")
		}
		if status.StatusMessage == "" {
			t.Errorf("expected non-empty status message")
		}
	}
}

func TestDefaultTunConfig(t *testing.T) {
	cfg := DefaultTunConfig()
	if cfg.Enabled {
		t.Errorf("expected default disabled")
	}
	if cfg.IPv4Subnet != "198.18.0.1/16" {
		t.Errorf("expected default 198.18.0.1/16, got %s", cfg.IPv4Subnet)
	}
	if !cfg.DNSHijack {
		t.Errorf("expected default DNSHijack true")
	}
	if len(cfg.BypassSubnets) < 4 {
		t.Errorf("expected default bypass subnets, got %v", cfg.BypassSubnets)
	}
}
