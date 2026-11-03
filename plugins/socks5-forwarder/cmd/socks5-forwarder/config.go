package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// UpstreamNode 远端上游服务节点（例如运行 socks5-proxy 的 LWT 宿主节点）
type UpstreamNode struct {
	ID            string        `json:"id"`
	Name          string        `json:"name"`
	URL           string        `json:"url"` // ws:// 或 wss:// 地址
	Token         string        `json:"token"`
	SkipTLSVerify bool          `json:"skipTlsVerify"`
	SNI           string        `json:"sni,omitempty"`
	Status        string        `json:"status"`    // healthy, degraded, dead, unknown
	LatencyMs     int64         `json:"latencyMs"` // 最近测速延迟 (毫秒)
	LastChecked   time.Time     `json:"lastChecked"`
	LastError     string        `json:"lastError,omitempty"`
}

// InboundProfile 本地入站中继端口配置与策略
type InboundProfile struct {
	ID               string   `json:"id"`
	Name             string   `json:"name"`
	Enabled          bool     `json:"enabled"`
	ListenPort       int      `json:"listenPort"`
	Protocol         string   `json:"protocol"` // mixed (自适应), socks5, http
	AllowedCIDR      []string `json:"allowedCidr"`
	UpstreamStrategy string   `json:"upstreamStrategy"` // single (固定单节点), best_latency (最低延迟), round_robin (轮询)
	UpstreamNodes    []string `json:"upstreamNodes"`    // 关联的 UpstreamNode ID 列表
	ActiveConns      int64    `json:"activeConns"`
	TotalBytesTx     int64    `json:"totalBytesTx"`
	TotalBytesRx     int64    `json:"totalBytesRx"`
	Status           string   `json:"status"` // running, stopped, error
	LastError        string   `json:"lastError,omitempty"`
}

// Config 全局持久化配置
type Config struct {
	mu         sync.RWMutex
	filePath   string
	Upstreams  []UpstreamNode   `json:"upstreams"`
	Inbounds   []InboundProfile `json:"inbounds"`
}

// LoadConfig 从指定路径加载配置，若文件不存在则初始化默认骨架
func LoadConfig(filePath string) (*Config, error) {
	cfg := &Config{
		filePath:  filePath,
		Upstreams: make([]UpstreamNode, 0),
		Inbounds:  make([]InboundProfile, 0),
	}

	if data, err := os.ReadFile(filePath); err == nil {
		if err := json.Unmarshal(data, cfg); err == nil {
			return cfg, nil
		}
	}

	// 初始化默认演示配置 (自适应容器内直通端口 5270 与 socks5-proxy 真实 Ingress Token)
	cfg.Upstreams = []UpstreamNode{
		{
			ID:            "default-lwt-node",
			Name:          "远程 LWT 默认宿主节点",
			URL:           "ws://127.0.0.1:5270/telemetry/metrics",
			Token:         "lwt_live_stream_token_8f3a9e",
			SkipTLSVerify: true,
			Status:        "unknown",
		},
	}
	cfg.Inbounds = []InboundProfile{
		{
			ID:               "inbound-lan-default",
			Name:             "局域网默认混合中继",
			Enabled:          true,
			ListenPort:       10801,
			Protocol:         "mixed",
			AllowedCIDR:      []string{"10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "::1/128"},
			UpstreamStrategy: "single",
			UpstreamNodes:    []string{"default-lwt-node"},
			Status:           "stopped",
		},
	}

	_ = cfg.Save()
	return cfg, nil
}

// Save 持久化保存配置至磁盘
func (c *Config) Save() error {
	c.mu.Lock()
	defer c.mu.Unlock()

	dir := filepath.Dir(c.filePath)
	if err := os.MkdirAll(dir, 0755); err != nil {
		return fmt.Errorf("创建配置目录失败: %w", err)
	}

	data, err := json.MarshalIndent(c, "", "  ")
	if err != nil {
		return fmt.Errorf("序列化配置失败: %w", err)
	}

	// 原子写入临时文件再 rename
	tmpFile := c.filePath + ".tmp"
	if err := os.WriteFile(tmpFile, data, 0644); err != nil {
		return fmt.Errorf("写入临时配置文件失败: %w", err)
	}

	return os.Rename(tmpFile, c.filePath)
}

// Clone 返回当前配置的安全深拷贝快照
func (c *Config) Clone() *Config {
	c.mu.RLock()
	defer c.mu.RUnlock()

	data, _ := json.Marshal(c)
	var clone Config
	_ = json.Unmarshal(data, &clone)
	clone.filePath = c.filePath
	return &clone
}
