package backend

import (
	"context"
	"fmt"
	"net/http"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/config"
)

type headerTransport struct {
	base       http.RoundTripper
	headers    map[string]string
	authSecret string
}

func (t *headerTransport) RoundTrip(req *http.Request) (*http.Response, error) {
	clone := req.Clone(req.Context())
	for k, v := range t.headers {
		clone.Header.Set(k, v)
	}
	if t.authSecret != "" && clone.Header.Get("Authorization") == "" {
		clone.Header.Set("Authorization", "Bearer "+t.authSecret)
	}
	base := t.base
	if base == nil {
		base = http.DefaultTransport
	}
	return base.RoundTrip(clone)
}

func connectHTTP(ctx context.Context, spec *config.BackendSpec, authSecret string) (Session, error) {
	timeout := time.Duration(spec.TimeoutSeconds) * time.Second
	if timeout <= 0 {
		timeout = 30 * time.Second
	}

	httpClient := &http.Client{
		Transport: &headerTransport{
			base:       http.DefaultTransport,
			headers:    spec.Headers,
			authSecret: authSecret,
		},
		Timeout: timeout,
	}

	transport := &mcp.StreamableClientTransport{
		Endpoint:   spec.URL,
		HTTPClient: httpClient,
	}

	client := mcp.NewClient(&mcp.Implementation{
		Name:    "mcp-bridge-http-client",
		Version: "1.0.0",
	}, nil)

	cs, err := client.Connect(ctx, transport, nil)
	if err != nil {
		return nil, fmt.Errorf("failed to connect to streamable http backend %q: %w", spec.ID, err)
	}

	return &mcpSession{clientSession: cs}, nil
}
