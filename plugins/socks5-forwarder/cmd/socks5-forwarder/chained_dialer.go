package main

import (
	"context"
	"errors"
	"fmt"
	"net"
	"sort"
	"strconv"
)

// ChainedCompositeOutbound 组合模式实现多跳链式代理（静态手动顺序模式）
type ChainedCompositeOutbound struct {
	id    string
	name  string
	hops  []IOutboundDialer
	nodes []UpstreamNode
}

// NewChainedCompositeOutbound 构造多跳链式出站适配器
func NewChainedCompositeOutbound(id, name string, hops []IOutboundDialer, nodes []UpstreamNode) *ChainedCompositeOutbound {
	return &ChainedCompositeOutbound{
		id:    id,
		name:  name,
		hops:  hops,
		nodes: nodes,
	}
}

func (c *ChainedCompositeOutbound) Protocol() string { return "chain" }

// DialContext 顺序穿透每一级跳板直至最终目标
func (c *ChainedCompositeOutbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	if len(c.hops) == 0 {
		return nil, errors.New("链式代理未配置任何跳板节点")
	}

	// 1. 若只有一跳，直接拨号
	if len(c.hops) == 1 {
		return c.hops[0].DialContext(ctx, network, targetAddr)
	}

	// 2. 第一跳：拨通第一跳物理节点，目标为第二跳的服务器地址
	hop1Addr := getHopTargetAddr(c.nodes[1])
	conn, err := c.hops[0].DialContext(ctx, network, hop1Addr)
	if err != nil {
		return nil, fmt.Errorf("链式代理第 1 跳 [%s] 失败: %w", c.nodes[0].Name, err)
	}

	// 3. 级联穿透后续跳板
	for i := 1; i < len(c.hops); i++ {
		var nextAddr string
		if i == len(c.hops)-1 {
			// 最后一跳目标为真实目标地址
			nextAddr = targetAddr
		} else {
			// 中间跳目标为下一跳的服务器地址
			nextAddr = getHopTargetAddr(c.nodes[i+1])
		}

		conn, err = c.hops[i].DialThrough(ctx, conn, nextAddr)
		if err != nil {
			_ = conn.Close()
			return nil, fmt.Errorf("链式代理第 %d 跳 [%s] 握手失败: %w", i+1, c.nodes[i].Name, err)
		}
	}

	return conn, nil
}

// DialThrough 支持将当前整条链作为跳板嵌套进更外层的链中
func (c *ChainedCompositeOutbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	conn := baseConn
	var err error
	for i := 0; i < len(c.hops); i++ {
		var nextAddr string
		if i == len(c.hops)-1 {
			nextAddr = targetAddr
		} else {
			nextAddr = getHopTargetAddr(c.nodes[i+1])
		}
		conn, err = c.hops[i].DialThrough(ctx, conn, nextAddr)
		if err != nil {
			_ = conn.Close()
			return nil, err
		}
	}
	return conn, nil
}

// AdaptiveChainedOutbound 自适应链式出站（支持动态中继寻优与可选固定落地出口）
type AdaptiveChainedOutbound struct {
	id             string
	name           string
	fixedExitNode  *UpstreamNode
	candidateHops  []UpstreamNode
	candidateExits []UpstreamNode
}

// NewAdaptiveChainedOutbound 构造自适应链式出站适配器
func NewAdaptiveChainedOutbound(id, name string, fixedExit *UpstreamNode, candidateHops, candidateExits []UpstreamNode) *AdaptiveChainedOutbound {
	return &AdaptiveChainedOutbound{
		id:             id,
		name:           name,
		fixedExitNode:  fixedExit,
		candidateHops:  candidateHops,
		candidateExits: candidateExits,
	}
}

func (a *AdaptiveChainedOutbound) Protocol() string { return "adaptive_chain" }

// sortNodesByPriority 按照健康状态（非 dead）与延迟优先排序
func sortNodesByPriority(nodes []UpstreamNode) []UpstreamNode {
	sorted := make([]UpstreamNode, len(nodes))
	copy(sorted, nodes)
	sort.SliceStable(sorted, func(i, j int) bool {
		// 1. 优先非 dead
		iDead := sorted[i].Status == "dead"
		jDead := sorted[j].Status == "dead"
		if iDead != jDead {
			return !iDead
		}
		// 2. 优先有测速延迟的
		iLat := sorted[i].LatencyMs
		jLat := sorted[j].LatencyMs
		if iLat <= 0 {
			iLat = 99999
		}
		if jLat <= 0 {
			jLat = 99999
		}
		return iLat < jLat
	})
	return sorted
}

// DialContext 自适应探测并在中间跳板发生故障时自动 Failover
func (a *AdaptiveChainedOutbound) DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error) {
	// 场景 A：固定落地出口模式 (Pinned Exit Mode)
	if a.fixedExitNode != nil {
		if len(a.candidateHops) == 0 {
			// 如果没有中间跳板，直接单节点连接固定落地出口
			exitDialer, err := GlobalOutboundRegistry.CreateDialer(*a.fixedExitNode)
			if err != nil {
				return nil, fmt.Errorf("创建固定落地节点 [%s] 拨号器失败: %w", a.fixedExitNode.Name, err)
			}
			return exitDialer.DialContext(ctx, network, targetAddr)
		}

		orderedHops := sortNodesByPriority(a.candidateHops)
		var lastErr error

		// 依次尝试排序后的候选前置中继，出口固定为 a.fixedExitNode
		for _, hopNode := range orderedHops {
			conn, err := tryDialChainTwoHops(ctx, network, hopNode, *a.fixedExitNode, targetAddr)
			if err == nil {
				return conn, nil
			}
			lastErr = err
		}
		return nil, fmt.Errorf("自适应链式代理 (固定出口 [%s]) 所有前置跳板均建连失败: %w", a.fixedExitNode.Name, lastErr)
	}

	// 场景 B：全动态候选网格优选模式 (Full-Mesh Dynamic Mode)
	orderedHops := sortNodesByPriority(a.candidateHops)
	orderedExits := sortNodesByPriority(a.candidateExits)

	if len(orderedExits) == 0 {
		return nil, errors.New("自适应链式代理未配置任何落地出口节点")
	}

	var lastErr error
	for _, exitNode := range orderedExits {
		if len(orderedHops) == 0 {
			// 直接单节点
			d, err := GlobalOutboundRegistry.CreateDialer(exitNode)
			if err == nil {
				conn, err := d.DialContext(ctx, network, targetAddr)
				if err == nil {
					return conn, nil
				}
				lastErr = err
			}
			continue
		}

		for _, hopNode := range orderedHops {
			conn, err := tryDialChainTwoHops(ctx, network, hopNode, exitNode, targetAddr)
			if err == nil {
				return conn, nil
			}
			lastErr = err
		}
	}

	return nil, fmt.Errorf("自适应全网格链式代理建连全部失败: %w", lastErr)
}

func (a *AdaptiveChainedOutbound) DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error) {
	return nil, errors.New("暂不支持将自适应复合链作为中间 DialThrough 跳板")
}

// tryDialChainTwoHops 辅助执行前置 hopNode -> 落地 exitNode -> targetAddr 的两跳穿透
func tryDialChainTwoHops(ctx context.Context, network string, hopNode, exitNode UpstreamNode, targetAddr string) (net.Conn, error) {
	hopDialer, err := GlobalOutboundRegistry.CreateDialer(hopNode)
	if err != nil {
		return nil, fmt.Errorf("创建前置跳板 [%s] 拨号器失败: %w", hopNode.Name, err)
	}
	exitDialer, err := GlobalOutboundRegistry.CreateDialer(exitNode)
	if err != nil {
		return nil, fmt.Errorf("创建落地出口 [%s] 拨号器失败: %w", exitNode.Name, err)
	}

	exitTargetAddr := getHopTargetAddr(exitNode)
	// 第一跳：向前置中继请求连接落地节点
	conn, err := hopDialer.DialContext(ctx, network, exitTargetAddr)
	if err != nil {
		return nil, fmt.Errorf("前置跳板 [%s] 连向落地节点失败: %w", hopNode.Name, err)
	}

	// 第二跳：在已有连接上穿透落地出口并连向最终目标
	finalConn, err := exitDialer.DialThrough(ctx, conn, targetAddr)
	if err != nil {
		_ = conn.Close()
		return nil, fmt.Errorf("落地节点 [%s] 握手目标端点失败: %w", exitNode.Name, err)
	}

	return finalConn, nil
}

func getHopTargetAddr(node UpstreamNode) string {
	if node.Server != "" && node.Port > 0 {
		return net.JoinHostPort(node.Server, strconv.Itoa(node.Port))
	}
	return node.URL
}
