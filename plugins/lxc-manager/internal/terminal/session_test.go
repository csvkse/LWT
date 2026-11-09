package terminal

import (
	"context"
	"strings"
	"testing"
)

func TestSessionManager_ExecuteCommand(t *testing.T) {
	sm := NewSessionManager()
	ctx := context.Background()

	res := sm.ExecuteCommand(ctx, "ct-test", "uname -a")
	if !strings.Contains(res.Output, "Linux ct-test") {
		t.Errorf("expected uname output to contain 'Linux ct-test', got %s", res.Output)
	}
	if res.ExitCode != 0 {
		t.Errorf("expected exit code 0, got %d", res.ExitCode)
	}

	res2 := sm.ExecuteCommand(ctx, "ct-test", "whoami")
	if strings.TrimSpace(res2.Output) != "root" {
		t.Errorf("expected whoami to be 'root', got %s", res2.Output)
	}

	hist := sm.GetHistory("ct-test")
	if len(hist) != 2 {
		t.Errorf("expected history length 2, got %d", len(hist))
	}
}
