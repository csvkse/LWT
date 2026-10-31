package main

import (
	"context"
	"fmt"
	"os"

	"github.com/modelcontextprotocol/go-sdk/mcp"
)

func main() {
	server := mcp.NewServer(&mcp.Implementation{
		Name:    "stdio-fixture",
		Version: "1.0.0",
	}, nil)

	type EchoArgs struct {
		Message string `json:"message"`
	}
	mcp.AddTool(server, &mcp.Tool{
		Name:        "stdio-echo",
		Description: "Echoes input over stdio",
	}, func(ctx context.Context, req *mcp.CallToolRequest, args EchoArgs) (*mcp.CallToolResult, any, error) {
		return &mcp.CallToolResult{
			Content: []mcp.Content{
				&mcp.TextContent{Text: "stdio: " + args.Message},
			},
		}, nil, nil
	})

	type AddArgs struct {
		A int `json:"a"`
		B int `json:"b"`
	}
	type AddOut struct {
		Result int `json:"result"`
	}
	mcp.AddTool(server, &mcp.Tool{
		Name:        "stdio-add",
		Description: "Adds numbers over stdio",
	}, func(ctx context.Context, req *mcp.CallToolRequest, args AddArgs) (*mcp.CallToolResult, AddOut, error) {
		sum := args.A + args.B
		return &mcp.CallToolResult{
			Content: []mcp.Content{
				&mcp.TextContent{Text: fmt.Sprintf("%d", sum)},
			},
		}, AddOut{Result: sum}, nil
	})

	if err := server.Run(context.Background(), &mcp.StdioTransport{}); err != nil {
		fmt.Fprintf(os.Stderr, "stdio server failed: %v\n", err)
		os.Exit(1)
	}
}
