package gateway

import (
	"context"
	"log/slog"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// NewFacadeServer creates the frontend in-memory MCP server with receiving middleware.
func NewFacadeServer(logger *slog.Logger) *mcp.Server {
	server := mcp.NewServer(&mcp.Implementation{
		Name:    "mcp-bridge-facade",
		Version: "1.0.0",
	}, nil)

	// Logging and error containment middleware
	server.AddReceivingMiddleware(func(next mcp.MethodHandler) mcp.MethodHandler {
		return func(ctx context.Context, method string, req mcp.Request) (mcp.Result, error) {
			if logger != nil {
				logger.Debug("mcp facade received request", "method", method)
			}
			res, err := next(ctx, method, req)
			if err != nil && logger != nil {
				logger.Warn("mcp facade method returned error", "method", method, "error", err)
			}
			return res, err
		}
	})

	return server
}
