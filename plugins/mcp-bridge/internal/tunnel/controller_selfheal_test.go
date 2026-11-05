package tunnel_test

import (
	"log/slog"
	"os"
	"testing"
	"time"

	"linuxwebtool.local/mcp-bridge/internal/tunnel"
)

func TestController_ErrorAutoClearOnSuccess(t *testing.T) {
	logger := slog.Default()
	ctrl := tunnel.NewController(nil, nil, logger)

	// 1. 模拟注入长轮询超时错误日志
	ctrl.ProcessLogEntryForTest(tunnel.ChannelLogEntry{
		Level:     "ERROR",
		Message:   "controlplane client: poll transport failed (timeout)",
		Timestamp: time.Now(),
		Attrs: map[string]any{
			"error": "controlplane client: poll transport failed (timeout)",
		},
	})

	status := ctrl.Status()
	if status.LastPollError != "controlplane client: poll transport failed (timeout)" {
		t.Fatalf("expected LastPollError to be recorded, got: %q", status.LastPollError)
	}

	// 2. 模拟渠道就绪态 (ReadyForChatGPT = true)
	ctrl.SetReadyForChatGPTForTest(true)

	// 3. 模拟接收到正常的轮询成功回执日志 (Status 204 No Content)
	ctrl.ProcessLogEntryForTest(tunnel.ChannelLogEntry{
		Level:     "INFO",
		Message:   "controlplane poller: completed empty poll",
		Timestamp: time.Now(),
		Attrs: map[string]any{
			"status": 204,
		},
	})

	// 4. 核心断言：成功的轮询必须自动清空瞬态 LastPollError
	statusAfter := ctrl.Status()
	if statusAfter.LastPollError != "" {
		t.Fatalf("expected LastPollError to be cleared after successful poll, got: %q", statusAfter.LastPollError)
	}
}

func TestResolvePollTimeout(t *testing.T) {
	// 1. 默认超时预算: 20s 轮询等待，10s 死线护栏 (避免 30s 代理空闲掐断)
	os.Unsetenv("MCP_BRIDGE_POLL_TIMEOUT_SEC")
	timeout, guardrail := tunnel.ResolvePollTimeout()
	if timeout != 20*time.Second {
		t.Errorf("expected default timeout 20s, got %v", timeout)
	}
	if guardrail != 10*time.Second {
		t.Errorf("expected default guardrail 10s, got %v", guardrail)
	}

	// 2. 环境变量调谐
	os.Setenv("MCP_BRIDGE_POLL_TIMEOUT_SEC", "15")
	defer os.Unsetenv("MCP_BRIDGE_POLL_TIMEOUT_SEC")
	timeout15, guardrail15 := tunnel.ResolvePollTimeout()
	if timeout15 != 15*time.Second {
		t.Errorf("expected 15s timeout from env, got %v", timeout15)
	}
	if guardrail15 < 5*time.Second {
		t.Errorf("guardrail should be at least 5s, got %v", guardrail15)
	}
}
