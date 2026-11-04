package main

import (
	"context"
	"errors"
	"fmt"
	"net"
	"strconv"
)

// ChainedCompositeOutbound 组合模式实现多跳链式代理
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

func getHopTargetAddr(node UpstreamNode) string {
	if node.Server != "" && node.Port > 0 {
		return net.JoinHostPort(node.Server, strconv.Itoa(node.Port))
	}
	return node.URL
}
