package main

import (
	"context"
	"fmt"
	"log"
	"sync"
	"sync/atomic"
	"time"
)

// ProfileManager 管理所有入站监听器与上游节点的生命周期
type ProfileManager struct {
	mu        sync.RWMutex
	cfg       *Config
	listeners map[string]*InboundListener
	ctx       context.Context
	cancel    context.CancelFunc
}

// NewProfileManager 创建 Profile 管理器实例
func NewProfileManager(cfg *Config) *ProfileManager {
	ctx, cancel := context.WithCancel(context.Background())
	return &ProfileManager{
		cfg:       cfg,
		listeners: make(map[string]*InboundListener),
		ctx:       ctx,
		cancel:    cancel,
	}
}

// StartAll 启动所有处于启用状态的入站 Profile 并启动后台健康探针
func (m *ProfileManager) StartAll() {
	m.mu.Lock()
	defer m.mu.Unlock()

	for i := range m.cfg.Inbounds {
		p := &m.cfg.Inbounds[i]
		if p.Enabled {
			l := NewInboundListener(*p, m)
			if err := l.Start(); err != nil {
				p.Status = "error"
				p.LastError = err.Error()
				log.Printf("[ProfileManager] 启动 Profile [%s] 失败: %v", p.Name, err)
			} else {
				p.Status = "running"
				p.LastError = ""
				m.listeners[p.ID] = l
			}
		} else {
			p.Status = "stopped"
		}
	}

	go m.backgroundHealthLoop()
}

// StopAll 优雅停止所有入站中继
func (m *ProfileManager) StopAll() {
	m.mu.Lock()
	defer m.mu.Unlock()

	m.cancel()
	for id, l := range m.listeners {
		l.Stop()
		delete(m.listeners, id)
	}
}

// ToggleInbound 启停单个入站规则
func (m *ProfileManager) ToggleInbound(id string) (*InboundProfile, error) {
	m.mu.Lock()
	defer m.mu.Unlock()

	var target *InboundProfile
	for i := range m.cfg.Inbounds {
		if m.cfg.Inbounds[i].ID == id {
			target = &m.cfg.Inbounds[i]
			break
		}
	}
	if target == nil {
		return nil, fmt.Errorf("找不到 ID 为 '%s' 的入站规则", id)
	}

	target.Enabled = !target.Enabled

	if target.Enabled {
		if l, exists := m.listeners[id]; exists {
			l.Stop()
			delete(m.listeners, id)
		}
		newListener := NewInboundListener(*target, m)
		if err := newListener.Start(); err != nil {
			target.Status = "error"
			target.LastError = err.Error()
			_ = m.cfg.Save()
			return target, err
		}
		target.Status = "running"
		target.LastError = ""
		m.listeners[id] = newListener
	} else {
		if l, exists := m.listeners[id]; exists {
			l.Stop()
			delete(m.listeners, id)
		}
		target.Status = "stopped"
	}

	_ = m.cfg.Save()
	return target, nil
}

// UpsertInbound 创建或更新入站规则
func (m *ProfileManager) UpsertInbound(p InboundProfile) (*InboundProfile, error) {
	m.mu.Lock()
	defer m.mu.Unlock()

	if p.ID == "" {
		p.ID = fmt.Sprintf("inbound-%d", time.Now().UnixNano()%100000)
	}

	if p.ListenPort <= 0 || p.ListenPort > 65535 {
		return nil, fmt.Errorf("主监听端口 :%d 无效，端口范围必须在 1-65535", p.ListenPort)
	}

	// 端口校验：确保自身端口列表无重复，且没有被其他规则占用
	portSet := make(map[int]bool)
	portSet[p.ListenPort] = true
	for _, addPort := range p.AdditionalPorts {
		if addPort <= 0 || addPort > 65535 {
			return nil, fmt.Errorf("附加监听端口 :%d 无效，端口范围必须在 1-65535", addPort)
		}
		if portSet[addPort] {
			return nil, fmt.Errorf("规则自身包含重复端口 :%d", addPort)
		}
		portSet[addPort] = true
	}

	for _, existing := range m.cfg.Inbounds {
		if existing.ID != p.ID {
			for _, existPort := range existing.GetEffectivePorts() {
				if portSet[existPort] {
					return nil, fmt.Errorf("端口 :%d 已被规则 '%s' 占用", existPort, existing.Name)
				}
			}
		}
	}

	idx := -1
	for i := range m.cfg.Inbounds {
		if m.cfg.Inbounds[i].ID == p.ID {
			idx = i
			break
		}
	}

	if idx >= 0 {
		// 更新已有规则，若正在运行则平滑重启
		if l, exists := m.listeners[p.ID]; exists {
			l.Stop()
			delete(m.listeners, p.ID)
		}
		m.cfg.Inbounds[idx] = p
	} else {
		m.cfg.Inbounds = append(m.cfg.Inbounds, p)
		idx = len(m.cfg.Inbounds) - 1
	}

	target := &m.cfg.Inbounds[idx]
	if target.Enabled {
		newListener := NewInboundListener(*target, m)
		if err := newListener.Start(); err != nil {
			target.Status = "error"
			target.LastError = err.Error()
		} else {
			target.Status = "running"
			target.LastError = ""
			m.listeners[target.ID] = newListener
		}
	} else {
		target.Status = "stopped"
	}

	_ = m.cfg.Save()
	return target, nil
}

// DeleteInbound 删除入站规则
func (m *ProfileManager) DeleteInbound(id string) error {
	m.mu.Lock()
	defer m.mu.Unlock()

	if l, exists := m.listeners[id]; exists {
		l.Stop()
		delete(m.listeners, id)
	}

	newInbounds := make([]InboundProfile, 0, len(m.cfg.Inbounds))
	for _, p := range m.cfg.Inbounds {
		if p.ID != id {
			newInbounds = append(newInbounds, p)
		}
	}
	m.cfg.Inbounds = newInbounds
	return m.cfg.Save()
}

// UpsertUpstream 创建或更新上游节点
func (m *ProfileManager) UpsertUpstream(node UpstreamNode) (*UpstreamNode, error) {
	m.mu.Lock()
	defer m.mu.Unlock()

	if node.ID == "" {
		node.ID = fmt.Sprintf("node-%d", time.Now().UnixNano()%100000)
	}
	if node.Status == "" {
		node.Status = "unknown"
	}

	idx := -1
	for i := range m.cfg.Upstreams {
		if m.cfg.Upstreams[i].ID == node.ID {
			idx = i
			break
		}
	}

	if idx >= 0 {
		m.cfg.Upstreams[idx] = node
	} else {
		m.cfg.Upstreams = append(m.cfg.Upstreams, node)
		idx = len(m.cfg.Upstreams) - 1
	}

	_ = m.cfg.Save()
	return &m.cfg.Upstreams[idx], nil
}

// DeleteUpstream 删除上游节点
func (m *ProfileManager) DeleteUpstream(id string) error {
	m.mu.Lock()
	defer m.mu.Unlock()

	newUpstreams := make([]UpstreamNode, 0, len(m.cfg.Upstreams))
	for _, n := range m.cfg.Upstreams {
		if n.ID != id {
			newUpstreams = append(newUpstreams, n)
		}
	}
	m.cfg.Upstreams = newUpstreams
	return m.cfg.Save()
}

// TestUpstreamNode 测试上游节点延迟
func (m *ProfileManager) TestUpstreamNode(id string) (int64, error) {
	m.mu.Lock()
	var target *UpstreamNode
	for i := range m.cfg.Upstreams {
		if m.cfg.Upstreams[i].ID == id {
			target = &m.cfg.Upstreams[i]
			break
		}
	}
	m.mu.Unlock()

	if target == nil {
		return 0, fmt.Errorf("未找到上游节点 '%s'", id)
	}

	latency, err := CheckUpstreamHealth(context.Background(), target)
	_ = m.cfg.Save()
	return latency, err
}

// SelectUpstreamForProfile 为指定 Profile 挑选最优可用上游节点
func (m *ProfileManager) SelectUpstreamForProfile(p InboundProfile, rrCounter *uint64) (*UpstreamNode, error) {
	m.mu.RLock()
	defer m.mu.RUnlock()

	nodeMap := make(map[string]*UpstreamNode, len(m.cfg.Upstreams))
	for i := range m.cfg.Upstreams {
		nodeMap[m.cfg.Upstreams[i].ID] = &m.cfg.Upstreams[i]
	}

	return SelectUpstream(p.UpstreamStrategy, p.UpstreamNodes, nodeMap, rrCounter)
}

// GetSnapshot 获取当前所有 Inbounds 与 Upstreams 的最新实时状态快照
func (m *ProfileManager) GetSnapshot() ([]UpstreamNode, []InboundProfile) {
	m.mu.RLock()
	defer m.mu.RUnlock()

	upstreams := make([]UpstreamNode, len(m.cfg.Upstreams))
	copy(upstreams, m.cfg.Upstreams)

	inbounds := make([]InboundProfile, len(m.cfg.Inbounds))
	for i, p := range m.cfg.Inbounds {
		if l, exists := m.listeners[p.ID]; exists {
			p.ActiveConns = atomic.LoadInt64(&l.profile.ActiveConns)
			p.TotalBytesTx = atomic.LoadInt64(&l.profile.TotalBytesTx)
			p.TotalBytesRx = atomic.LoadInt64(&l.profile.TotalBytesRx)
			p.Status = "running"
		}
		inbounds[i] = p
	}

	return upstreams, inbounds
}

func (m *ProfileManager) backgroundHealthLoop() {
	ticker := time.NewTicker(60 * time.Second)
	defer ticker.Stop()

	for {
		select {
		case <-m.ctx.Done():
			return
		case <-ticker.C:
			m.mu.Lock()
			nodes := make([]*UpstreamNode, len(m.cfg.Upstreams))
			for i := range m.cfg.Upstreams {
				nodes[i] = &m.cfg.Upstreams[i]
			}
			m.mu.Unlock()

			for _, n := range nodes {
				_, _ = CheckUpstreamHealth(m.ctx, n)
			}
			_ = m.cfg.Save()
		}
	}
}
