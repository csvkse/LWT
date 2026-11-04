package main

import (
	"context"
	"fmt"
	"os"
	"strings"
	"time"
)

// CheckUpstreamHealth 探测远端上游节点的健康状态与 RTT 延迟 (毫秒)
func CheckUpstreamHealth(ctx context.Context, node *UpstreamNode) (int64, error) {
	start := time.Now()
	timeoutCtx, cancel := context.WithTimeout(ctx, 4*time.Second)
	defer cancel()

	proto := strings.ToLower(node.Type)

	// 1. 针对 LWT-WSS 隐匿隧道: 验证客户端到该端点的握手时延 (L1 物理链路 RTT)
	// 避免向服务端不存在的 80 端口盲发 SOCKS5 CONNECT 导致 0x05 Connection Refused 假死误报
	if proto == "lwt_wss" || (proto == "" && (strings.HasPrefix(node.URL, "ws://") || strings.HasPrefix(node.URL, "wss://"))) {
		conn, err := DialWebSocket(timeoutCtx, node.URL, node.Token, "lwt-tunnel-protocol-v1", node.SkipTLSVerify, node.SNI)
		if err != nil {
			node.Status = "dead"
			node.LastError = formatProbeError(err)
			node.LastChecked = time.Now()
			return 0, err
		}
		_ = conn.Close()

		latency := time.Since(start).Milliseconds()
		node.LatencyMs = latency
		node.LastChecked = time.Now()
		node.LastError = ""
		if latency > 800 {
			node.Status = "degraded"
		} else {
			node.Status = "healthy"
		}
		return latency, nil
	}

	// 2. 针对通用代理协议 (SOCKS5, HTTP, Trojan, VLESS, Shadowsocks):
	dialer, err := GlobalOutboundRegistry.CreateDialer(*node)
	if err != nil {
		node.Status = "dead"
		node.LastError = formatProbeError(err)
		node.LastChecked = time.Now()
		return 0, err
	}

	target := resolveSafeProbeTarget(node)
	conn, err := dialer.DialContext(timeoutCtx, "tcp", target)
	if err != nil {
		node.Status = "dead"
		node.LastError = formatProbeError(err)
		node.LastChecked = time.Now()
		return 0, err
	}
	defer conn.Close()

	latency := time.Since(start).Milliseconds()
	node.LatencyMs = latency
	node.LastChecked = time.Now()
	node.LastError = ""

	if latency > 800 {
		node.Status = "degraded"
	} else {
		node.Status = "healthy"
	}

	return latency, nil
}

// resolveSafeProbeTarget 解析安全的健康检查探针目标，避免盲拨 127.0.0.1:80 引发连接拒绝
func resolveSafeProbeTarget(node *UpstreamNode) string {
	hostInternalPort := os.Getenv("LWT_HOST_INTERNAL_PORT")
	if hostInternalPort == "" {
		hostInternalPort = "5270"
	}

	// 若节点本身指向本地回环，探测当前系统内置的 Web 端口
	if node.Server == "127.0.0.1" || node.Server == "localhost" {
		return fmt.Sprintf("127.0.0.1:%s", hostInternalPort)
	}

	// 公网/境外节点：默认探测高可用探针目标 (Cloudflare 204)
	return "cp.cloudflare.com:80"
}

// formatProbeError 将底层网络错误转译为人类友好的排障指引
func formatProbeError(err error) string {
	if err == nil {
		return ""
	}
	errStr := err.Error()
	if strings.Contains(errStr, "connection refused") || strings.Contains(errStr, "actively refused") {
		return "服务未启动或端口未监听 (Connection refused)"
	}
	if strings.Contains(errStr, "i/o timeout") || strings.Contains(errStr, "context deadline exceeded") {
		return "连接超时，目标主机无响应或防火墙阻断"
	}
	if strings.Contains(errStr, "0x05") {
		return "上游代理可达，但目标端点拒绝访问 (0x05 Connection Refused)"
	}
	if strings.Contains(errStr, "0x02") {
		return "被上游代理 ACL 白名单规则拦截 (0x02 Rule Violation)"
	}
	if strings.Contains(errStr, "401") || strings.Contains(errStr, "407") || strings.Contains(errStr, "凭据") {
		return "鉴权失败：用户名、密码或 Token 不匹配"
	}
	return errStr
}

// SelectUpstream 根据策略从候选节点列表中挑选最适节点
func SelectUpstream(strategy string, candidateIDs []string, nodeMap map[string]*UpstreamNode, counter *uint64) (*UpstreamNode, error) {
	if len(candidateIDs) == 0 {
		return nil, fmt.Errorf("未关联任何上游服务器节点")
	}

	validNodes := make([]*UpstreamNode, 0, len(candidateIDs))
	for _, id := range candidateIDs {
		if node, exists := nodeMap[id]; exists {
			validNodes = append(validNodes, node)
		}
	}

	if len(validNodes) == 0 {
		return nil, fmt.Errorf("所有关联的上游服务器节点均不可用")
	}

	switch strategy {
	case "best_latency":
		var bestNode *UpstreamNode
		for _, node := range validNodes {
			if node.Status == "dead" {
				continue
			}
			if bestNode == nil || (node.LatencyMs > 0 && node.LatencyMs < bestNode.LatencyMs) {
				bestNode = node
			}
		}
		if bestNode != nil {
			return bestNode, nil
		}
		// 若全挂则降级回首个节点尝试重连
		return validNodes[0], nil

	case "round_robin":
		if counter != nil {
			*counter++
			idx := int(*counter) % len(validNodes)
			return validNodes[idx], nil
		}
		return validNodes[0], nil

	case "single", "failover":
		fallthrough
	default:
		// 优先选择第一个健康节点，若故障尝试后继节点 (Failover)
		for _, node := range validNodes {
			if node.Status != "dead" {
				return node, nil
			}
		}
		return validNodes[0], nil
	}

	return validNodes[0], nil
}
