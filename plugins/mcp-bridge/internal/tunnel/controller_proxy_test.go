package tunnel

import (
	"os"
	"testing"
)

func TestNormalizeProxyURL(t *testing.T) {
	tests := []struct {
		name     string
		input    string
		expected string
	}{
		{
			name:     "empty proxy",
			input:    "",
			expected: "",
		},
		{
			name:     "whitespace only",
			input:    "   ",
			expected: "",
		},
		{
			name:     "socks5 to socks5h upgrade for remote DNS",
			input:    "socks5://127.0.0.1:10808",
			expected: "socks5h://127.0.0.1:10808",
		},
		{
			name:     "socks5h remains socks5h",
			input:    "socks5h://127.0.0.1:10808",
			expected: "socks5h://127.0.0.1:10808",
		},
		{
			name:     "http proxy preserved",
			input:    "http://127.0.0.1:7890",
			expected: "http://127.0.0.1:7890",
		},
		{
			name:     "https proxy preserved",
			input:    "https://proxy.example.com:8443",
			expected: "https://proxy.example.com:8443",
		},
	}

	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			actual := NormalizeProxyURL(tt.input)
			if actual != tt.expected {
				t.Errorf("NormalizeProxyURL(%q) = %q; want %q", tt.input, actual, tt.expected)
			}
		})
	}
}

func TestApplyProxyEnvironment(t *testing.T) {
	// 1. SOCKS5 proxy test
	ApplyProxyEnvironment("socks5://127.0.0.1:10808")
	if os.Getenv("ALL_PROXY") != "socks5h://127.0.0.1:10808" {
		t.Errorf("expected ALL_PROXY to be socks5h://127.0.0.1:10808, got %q", os.Getenv("ALL_PROXY"))
	}
	if os.Getenv("CONTROL_PLANE_HTTP_PROXY") != "" {
		t.Errorf("expected CONTROL_PLANE_HTTP_PROXY to be empty for socks5, got %q", os.Getenv("CONTROL_PLANE_HTTP_PROXY"))
	}

	// 2. HTTP proxy test
	ApplyProxyEnvironment("http://127.0.0.1:7890")
	if os.Getenv("ALL_PROXY") != "http://127.0.0.1:7890" {
		t.Errorf("expected ALL_PROXY to be http://127.0.0.1:7890, got %q", os.Getenv("ALL_PROXY"))
	}
	if os.Getenv("CONTROL_PLANE_HTTP_PROXY") != "http://127.0.0.1:7890" {
		t.Errorf("expected CONTROL_PLANE_HTTP_PROXY to be http://127.0.0.1:7890, got %q", os.Getenv("CONTROL_PLANE_HTTP_PROXY"))
	}
	if os.Getenv("TUNNEL_CLIENT_HTTP_PROXY") != "http://127.0.0.1:7890" {
		t.Errorf("expected TUNNEL_CLIENT_HTTP_PROXY to be http://127.0.0.1:7890, got %q", os.Getenv("TUNNEL_CLIENT_HTTP_PROXY"))
	}

	// 3. Cleanup test
	ApplyProxyEnvironment("")
	if os.Getenv("ALL_PROXY") != "" || os.Getenv("HTTPS_PROXY") != "" || os.Getenv("CONTROL_PLANE_HTTP_PROXY") != "" {
		t.Errorf("expected all proxy env variables to be cleared")
	}
}
