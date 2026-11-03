package main

import (
	"context"
	"fmt"
	"time"
)

// CheckUpstreamHealth 探测远端上游节点的健康状态与 RTT 延迟 (毫秒)
func CheckUpstreamHealth(ctx context.Context, node *UpstreamNode) (int64, error) {
	start := time.Now()
	timeoutCtx, cancel := context.WithTimeout(ctx, 4*time.Second)
	defer cancel()

	conn, err := DialWebSocket(timeoutCtx, node.URL, node.Token, "lwt-token.", node.SkipTLSVerify, node.SNI)
	if err != nil {
		node.Status = "dead"
		node.LastError = err.Error()
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
