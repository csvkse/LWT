package backend

import (
	"context"
	"fmt"
	"net/http"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/config"
)

func connectSSE(ctx context.Context, spec *config.BackendSpec, authSecret string) (Session, error) {
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

	transport := &mcp.SSEClientTransport{
		Endpoint:   spec.URL,
		HTTPClient: httpClient,
	}

	client := mcp.NewClient(&mcp.Implementation{
		Name:    "mcp-bridge-sse-client",
		Version: "1.0.0",
	}, nil)

	cs, err := client.Connect(ctx, transport, nil)
	if err != nil {
		return nil, fmt.Errorf("failed to connect to sse backend %q: %w", spec.ID, err)
	}

	return &mcpSession{clientSession: cs}, nil
}
