package backend

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"runtime"
	"strings"
	"time"

	"github.com/modelcontextprotocol/go-sdk/mcp"
	"linuxwebtool.local/mcp-bridge/internal/config"
)

func buildMinimalEnv(userEnv map[string]string) []string {
	var env []string

	// Allowed system base env variables
	var allowedBase []string
	if runtime.GOOS == "windows" {
		allowedBase = []string{"PATH", "SYSTEMROOT", "COMSPEC", "TEMP", "TMP", "PATHEXT", "USERPROFILE"}
	} else {
		allowedBase = []string{"PATH", "HOME", "USER", "TMPDIR", "LANG", "LD_LIBRARY_PATH"}
	}

	for _, key := range allowedBase {
		if val, ok := os.LookupEnv(key); ok {
			env = append(env, key+"="+val)
		}
	}

	// Add approved user-defined env vars
	for k, v := range userEnv {
		if strings.TrimSpace(k) != "" {
			env = append(env, k+"="+v)
		}
	}

	return env
}

func connectStdio(ctx context.Context, spec *config.BackendSpec) (Session, error) {
	cmd := exec.Command(spec.Command, spec.Args...)
	cmd.Env = buildMinimalEnv(spec.Env)
	cmd.Stderr = os.Stderr
	if spec.Cwd != "" {
		cmd.Dir = spec.Cwd
	}

	transport := &mcp.CommandTransport{
		Command:           cmd,
		TerminateDuration: 5 * time.Second,
	}

	client := mcp.NewClient(&mcp.Implementation{
		Name:    "mcp-bridge-stdio-client",
		Version: "1.0.0",
	}, nil)

	cs, err := client.Connect(ctx, transport, nil)
	if err != nil {
		return nil, fmt.Errorf("failed to connect to stdio backend %q: %w", spec.ID, err)
	}

	return &mcpSession{clientSession: cs}, nil
}
