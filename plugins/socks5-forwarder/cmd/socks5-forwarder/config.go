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
	AdditionalPorts  []int    `json:"additionalPorts,omitempty"` // 附加监听端口列表（支持多端口入站映射同一策略）
	Protocol         string   `json:"protocol"`                 // mixed (自适应), socks5, http
	AllowedCIDR      []string `json:"allowedCidr"`
	UpstreamStrategy string   `json:"upstreamStrategy"`         // single (固定单节点), best_latency (最低延迟), round_robin (轮询)
	UpstreamNodes    []string `json:"upstreamNodes"`            // 关联的 UpstreamNode ID 列表
	ActiveConns      int64    `json:"activeConns"`
	TotalBytesTx     int64    `json:"totalBytesTx"`
	TotalBytesRx     int64    `json:"totalBytesRx"`
	Status           string   `json:"status"`                   // running, stopped, error
	LastError        string   `json:"lastError,omitempty"`
}

// GetEffectivePorts 返回当前规则生效的所有监听端口（主端口 + 附加端口，已去重过滤无效端口）
func (p *InboundProfile) GetEffectivePorts() []int {
	seen := make(map[int]bool)
	var ports []int
	if p.ListenPort > 0 && p.ListenPort <= 65535 {
		seen[p.ListenPort] = true
		ports = append(ports, p.ListenPort)
	}
	for _, port := range p.AdditionalPorts {
		if port > 0 && port <= 65535 && !seen[port] {
			seen[port] = true
			ports = append(ports, port)
		}
	}
	return ports
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

	hostInternalPort := os.Getenv("LWT_HOST_INTERNAL_PORT")
	if hostInternalPort == "" {
		hostInternalPort = "5270"
	}
	defaultUpstreamURL := fmt.Sprintf("ws://127.0.0.1:%s/telemetry/metrics", hostInternalPort)
	defaultToken := os.Getenv("LWT_PLUGIN_INGRESS_TOKEN")
	if defaultToken == "" {
		defaultToken = os.Getenv("LWT_INTERNAL_SECRET")
	}
	if defaultToken == "" {
		defaultToken = "lwt_live_stream_token_8f3a9e"
	}

	if data, err := os.ReadFile(filePath); err == nil {
		if err := json.Unmarshal(data, cfg); err == nil {
			// 历史配置自愈纠偏 (Self-healing for legacy config)
			healed := false
			for i := range cfg.Upstreams {
				if cfg.Upstreams[i].ID == "default-lwt-node" {
					if cfg.Upstreams[i].URL == "ws://127.0.0.1:5271/telemetry/metrics" || cfg.Upstreams[i].URL == "" {
						cfg.Upstreams[i].URL = defaultUpstreamURL
						healed = true
					}
					if cfg.Upstreams[i].Token == "your-camouflage-token-here" || cfg.Upstreams[i].Token == "" {
						cfg.Upstreams[i].Token = defaultToken
						healed = true
					}
				}
			}
			if healed {
				_ = cfg.Save()
			}
			return cfg, nil
		}
	}

	// 初始化默认演示配置 (自适应宿主容器内部直通端口与真实 Ingress Token)
	cfg.Upstreams = []UpstreamNode{
		{
			ID:            "default-lwt-node",
			Name:          "远程 LWT 默认宿主节点",
			URL:           defaultUpstreamURL,
			Token:         defaultToken,
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
			AdditionalPorts:  []int{},
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
