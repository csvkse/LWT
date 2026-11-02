package backend

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/config"
)

// Session represents an initialized MCP client session with a third-party backend.
type Session interface {
	ListTools(ctx context.Context, cursor string) (*mcp.ListToolsResult, error)
	CallTool(ctx context.Context, name string, args json.RawMessage) (*mcp.CallToolResult, error)
	Close(ctx context.Context) error
}

type mcpSession struct {
	clientSession *mcp.ClientSession
}

func (s *mcpSession) ListTools(ctx context.Context, cursor string) (*mcp.ListToolsResult, error) {
	if s.clientSession == nil {
		return nil, errors.New("backend session is closed")
	}
	params := &mcp.ListToolsParams{
		Cursor: cursor,
	}
	return s.clientSession.ListTools(ctx, params)
}

func (s *mcpSession) CallTool(ctx context.Context, name string, args json.RawMessage) (*mcp.CallToolResult, error) {
	if s.clientSession == nil {
		return nil, errors.New("backend session is closed")
	}

	var arguments any
	if len(args) > 0 {
		var decoded any
		if err := json.Unmarshal(args, &decoded); err != nil {
			return nil, fmt.Errorf("invalid json arguments: %w", err)
		}
		arguments = decoded
	}

	params := &mcp.CallToolParams{
		Name:      name,
		Arguments: arguments,
	}
	return s.clientSession.CallTool(ctx, params)
}

func (s *mcpSession) Close(_ context.Context) error {
	if s.clientSession == nil {
		return nil
	}
	err := s.clientSession.Close()
	s.clientSession = nil
	return err
}

// ConnectBackend establishes a new MCP session to a backend based on its transport.
func ConnectBackend(ctx context.Context, spec *config.BackendSpec, authSecret string, proxyURL ...string) (Session, error) {
	if spec == nil {
		return nil, errors.New("spec is nil")
	}

	var p string
	if len(proxyURL) > 0 {
		p = proxyURL[0]
	}

	switch spec.Transport {
	case config.TransportStdio:
		return connectStdio(ctx, spec)
	case config.TransportStreamableHTTP:
		return connectHTTP(ctx, spec, authSecret, p)
	case config.TransportSSE:
		return connectSSE(ctx, spec, authSecret, p)
	default:
		return nil, fmt.Errorf("unsupported backend transport: %q", spec.Transport)
	}
}
