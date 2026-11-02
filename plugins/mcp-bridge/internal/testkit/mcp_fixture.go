package testkit

import (
	"context"
	"fmt"
	"sync/atomic"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

// MCPFixture creates an in-memory MCP server with standard test tools.
type MCPFixture struct {
	server       *mcp.Server
	EchoCount    atomic.Int64
	AddCount     atomic.Int64
	FailCount    atomic.Int64
}

// NewMCPFixture instantiates a test MCP server.
func NewMCPFixture() *MCPFixture {
	f := &MCPFixture{}

	f.server = mcp.NewServer(&mcp.Implementation{
		Name:    "testkit-fixture",
		Version: "1.0.0",
	}, nil)

	type EchoArgs struct {
		Message string `json:"message"`
	}
	mcp.AddTool(f.server, &mcp.Tool{
		Name:        "echo",
		Description: "Echoes the input message",
	}, func(ctx context.Context, req *mcp.CallToolRequest, args EchoArgs) (*mcp.CallToolResult, any, error) {
		f.EchoCount.Add(1)
		return &mcp.CallToolResult{
			Content: []mcp.Content{
				&mcp.TextContent{Text: "echo: " + args.Message},
			},
		}, nil, nil
	})

	type AddArgs struct {
		A int `json:"a"`
		B int `json:"b"`
	}
	type AddOut struct {
		Sum int `json:"sum"`
	}
	mcp.AddTool(f.server, &mcp.Tool{
		Name:        "add",
		Description: "Adds two numbers",
	}, func(ctx context.Context, req *mcp.CallToolRequest, args AddArgs) (*mcp.CallToolResult, AddOut, error) {
		f.AddCount.Add(1)
		return &mcp.CallToolResult{
			Content: []mcp.Content{
				&mcp.TextContent{Text: fmt.Sprintf("%d", args.A+args.B)},
			},
		}, AddOut{Sum: args.A + args.B}, nil
	})

	type FailArgs struct {
		Reason string `json:"reason"`
	}
	mcp.AddTool(f.server, &mcp.Tool{
		Name:        "fail",
		Description: "Simulates a tool failure",
	}, func(ctx context.Context, req *mcp.CallToolRequest, args FailArgs) (*mcp.CallToolResult, any, error) {
		f.FailCount.Add(1)
		return &mcp.CallToolResult{
			IsError: true,
			Content: []mcp.Content{
				&mcp.TextContent{Text: "tool failure: " + args.Reason},
			},
		}, nil, nil
	})

	return f
}

// Server returns the underlying mcp.Server.
func (f *MCPFixture) Server() *mcp.Server {
	return f.server
}

// CreateInMemoryPair starts the fixture server on one side and returns the client transport.
func (f *MCPFixture) CreateInMemoryPair(ctx context.Context) (mcp.Transport, error) {
	clientTransport, serverTransport := mcp.NewInMemoryTransports()
	go func() {
		_ = f.server.Run(ctx, serverTransport)
	}()
	return clientTransport, nil
}
