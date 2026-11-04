package main

import (
	"context"
	"errors"
	"os"
	"strings"
	"testing"
	"time"
)

func Test_FormatProbeError(t *testing.T) {
	testCases := []struct {
		errStr   string
		expected string
	}{
		{"dial tcp 127.0.0.1:8443: connect: connection refused", "服务未启动或端口未监听 (Connection refused)"},
		{"context deadline exceeded", "连接超时，目标主机无响应或防火墙阻断"},
		{"LWT WSS 隧道拒绝代理请求 (0x05)", "上游代理可达，但目标端点拒绝访问 (0x05 Connection Refused)"},
		{"SOCKS5 CONNECT 请求被拒绝，状态码: 0x02", "被上游代理 ACL 白名单规则拦截 (0x02 Rule Violation)"},
		{"SOCKS5 认证失败: 凭据不正确 (0x01)", "鉴权失败：用户名、密码或 Token 不匹配"},
	}

	for _, tc := range testCases {
		res := formatProbeError(errors.New(tc.errStr))
		if !strings.Contains(res, tc.expected) {
			t.Fatalf("formatProbeError 不匹配: 原文 '%s', 期望包含 '%s', 实际 '%s'",
				tc.errStr, tc.expected, res)
		}
	}
}

func Test_ResolveSafeProbeTarget(t *testing.T) {
	_ = os.Setenv("LWT_HOST_INTERNAL_PORT", "5270")
	defer os.Unsetenv("LWT_HOST_INTERNAL_PORT")

	localNode := &UpstreamNode{Server: "127.0.0.1"}
	target := resolveSafeProbeTarget(localNode)
	if target != "127.0.0.1:5270" {
		t.Fatalf("本地节点探针目标解析错误: 期望 127.0.0.1:5270, 实际 %s", target)
	}

	remoteNode := &UpstreamNode{Server: "1.2.3.4"}
	targetRemote := resolveSafeProbeTarget(remoteNode)
	if targetRemote != "cp.cloudflare.com:80" {
		t.Fatalf("远程节点探针目标解析错误: 期望 cp.cloudflare.com:80, 实际 %s", targetRemote)
	}
}

func Test_CheckUpstreamHealth_SafeProbe(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Second)
	defer cancel()

	deadNode := &UpstreamNode{
		ID:     "dead-node",
		Type:   "socks5",
		Server: "127.0.0.1",
		Port:   64999, // 确保未监听端口
	}

	_, err := CheckUpstreamHealth(ctx, deadNode)
	if err == nil {
		t.Fatalf("未监听端口预期应报错，但未报错")
	}
	if !strings.Contains(deadNode.LastError, "服务未启动或端口未监听") {
		t.Fatalf("错误未被友好转译: %s", deadNode.LastError)
	}
}
