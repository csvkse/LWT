package main

import (
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"
)

// UpstreamNode 远端上游服务节点（例如运行 socks5-proxy 的 LWT 宿主节点或外部多协议节点）
type UpstreamNode struct {
	ID            string    `json:"id"`
	Name          string    `json:"name"`
	Type          string    `json:"type,omitempty"` // lwt_wss, socks5, http, shadowsocks, trojan, vless, direct
	URL           string    `json:"url,omitempty"`  // ws:// 或 wss:// 地址
	Server        string    `json:"server,omitempty"`
	Port          int       `json:"port,omitempty"`
	Username      string    `json:"username,omitempty"`
	Password      string    `json:"password,omitempty"`
	UUID          string    `json:"uuid,omitempty"`
	Flow          string    `json:"flow,omitempty"`        // xtls-rprx-vision 等流控
	Fingerprint   string    `json:"fingerprint,omitempty"` // chrome, firefox 等指纹
	Transport     string    `json:"transport,omitempty"`   // tcp, ws, etc.
	Method        string    `json:"method,omitempty"`
	Token         string    `json:"token,omitempty"`
	SkipTLSVerify bool      `json:"skipTlsVerify,omitempty"`
	SNI           string    `json:"sni,omitempty"`
	Status        string    `json:"status"`    // healthy, degraded, dead, unknown
	LatencyMs     int64     `json:"latencyMs"` // 最近测速延迟 (毫秒)
	LastChecked   time.Time `json:"lastChecked"`
	LastError     string    `json:"lastError,omitempty"`
}

// RuleConfig 单条分流路由规则
type RuleConfig struct {
	Type   string `json:"type"`   // domain_suffix, domain_keyword, ip_cidr
	Value  string `json:"value"`  // 规则匹配值 (如 ".cn", "google", "192.168.1.0/24")
	Action string `json:"action"` // direct, reject, proxy, chain
	Target string `json:"target,omitempty"`
}

// RoutingPipeline 独立线路编排流水线 (解耦分流策略与物理接入)
type RoutingPipeline struct {
	ID                   string       `json:"id"`
	Name                 string       `json:"name"`
	Description          string       `json:"description,omitempty"`
	DirectPrivateNetwork bool         `json:"directPrivateNetwork"`
	DefaultAction        string       `json:"defaultAction"`            // proxy, direct, reject
	DefaultOutbound      string       `json:"defaultOutbound,omitempty"` // 默认关联的 Upstream ID 或 Chain ID
	Rules                []RuleConfig `json:"rules,omitempty"`
}

// RoutePolicyConfig 入站规则分流策略 (向后兼容历史配置)
type RoutePolicyConfig struct {
	DirectPrivateNetwork bool         `json:"directPrivateNetwork"`
	Rules                []RuleConfig `json:"rules,omitempty"`
	DefaultAction        string       `json:"defaultAction,omitempty"`   // proxy, direct, reject
	DefaultUpstream      string       `json:"defaultUpstream,omitempty"` // 默认关联的 Upstream ID 或 Chain ID
}

// ProxyChainConfig 多跳链式代理配置
type ProxyChainConfig struct {
	ID                   string   `json:"id"`
	Name                 string   `json:"name"`
	Strategy             string   `json:"strategy"`                       // manual (静态拓扑), auto (自适应寻优)
	Hops                 []string `json:"hops"`                           // 关联的 UpstreamNode ID 列表（手动模式顺序）
	FixedExitNodeID      string   `json:"fixedExitNodeId,omitempty"`      // 自动模式：可选固定落地出口（锁定出口 IP）
	CandidateHopNodeIDs  []string `json:"candidateHopNodeIds,omitempty"`  // 自动模式：前置候选跳板池（自适应中继寻优）
	CandidateExitNodeIDs []string `json:"candidateExitNodeIds,omitempty"` // 自动模式：落地候选池（当 FixedExitNodeID 为空时）
	Mux                  bool     `json:"mux,omitempty"`
}

// InboundProfile 本地入站接入配置 (三阶解耦模型：统一支持端口与 TUN 虚拟网卡)
type InboundProfile struct {
	ID               string            `json:"id"`
	Name             string            `json:"name"`
	Type             string            `json:"type,omitempty"`            // port (物理端口), tun (虚拟网卡透明代理)
	Enabled          bool              `json:"enabled"`
	ListenPort       int               `json:"listenPort"`
	AdditionalPorts  []int             `json:"additionalPorts,omitempty"` // 附加监听端口列表（支持多端口映射）
	Protocol         string            `json:"protocol"`                 // mixed (自适应), socks5, http
	AllowedCIDR      []string          `json:"allowedCidr"`
	PipelineID       string            `json:"pipelineId,omitempty"`      // 关联绑定的 RoutingPipeline ID
	UpstreamStrategy string            `json:"upstreamStrategy,omitempty"`// 兼容字段
	UpstreamNodes    []string          `json:"upstreamNodes,omitempty"`   // 兼容字段
	RoutePolicy      RoutePolicyConfig `json:"routePolicy,omitempty"`    // 兼容历史字段
	TunInterface     string            `json:"tunInterface,omitempty"`    // TUN 虚拟网卡名 (lwt-tun0)
	TunCidr          string            `json:"tunCidr,omitempty"`         // TUN 虚拟网段 (198.18.0.1/16)
	DnsHijack        bool              `json:"dnsHijack,omitempty"`       // 53 端口 DNS 自动劫持
	BypassCidr       []string          `json:"bypassCidr,omitempty"`      // 透明代理旁路网段
	ActiveConns      int64             `json:"activeConns"`
	TotalBytesTx     int64             `json:"totalBytesTx"`
	TotalBytesRx     int64             `json:"totalBytesRx"`
	Status           string            `json:"status"`                    // running, stopped, error
	LastError        string            `json:"lastError,omitempty"`
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
	mu        sync.RWMutex
	filePath  string
	Upstreams []UpstreamNode     `json:"upstreams"`
	Inbounds  []InboundProfile   `json:"inbounds"`
	Pipelines []RoutingPipeline  `json:"pipelines,omitempty"`
	Chains    []ProxyChainConfig `json:"chains,omitempty"`
}

// LoadConfig 从指定路径加载配置，若文件不存在则初始化默认骨架
func LoadConfig(filePath string) (*Config, error) {
	cfg := &Config{
		filePath:  filePath,
		Upstreams: make([]UpstreamNode, 0),
		Inbounds:  make([]InboundProfile, 0),
		Pipelines: make([]RoutingPipeline, 0),
		Chains:    make([]ProxyChainConfig, 0),
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

	defaultPipelines := []RoutingPipeline{
		{
			ID:                   "pipe-default",
			Name:                 "全局标准分流线路 (默认流水线)",
			Description:          "局域网与国内主流服务直连，拦截广告隐私追踪，境外服务走代理",
			DirectPrivateNetwork: true,
			DefaultAction:        "proxy",
			DefaultOutbound:      "default-lwt-node",
			Rules: []RuleConfig{
				{Type: "domain_keyword", Value: "adservice", Action: "reject"},
				{Type: "domain_keyword", Value: "telemetry", Action: "reject"},
				{Type: "domain_suffix", Value: ".cn", Action: "direct"},
				{Type: "domain_suffix", Value: "bilibili.com", Action: "direct"},
				{Type: "ip_cidr", Value: "223.5.5.0/24", Action: "direct"},
			},
		},
		{
			ID:                   "pipe-ai-dedicated",
			Name:                 "AI 研发专线流水线",
			Description:          "OpenAI、Claude、Copilot 模型加速专线，大陆服务直连",
			DirectPrivateNetwork: true,
			DefaultAction:        "proxy",
			DefaultOutbound:      "default-lwt-node",
			Rules: []RuleConfig{
				{Type: "domain_suffix", Value: "openai.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "chatgpt.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "anthropic.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "claude.ai", Action: "proxy"},
				{Type: "domain_suffix", Value: ".cn", Action: "direct"},
			},
		},
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

			// 自动迁移并补全分流流水线
			if len(cfg.Pipelines) == 0 {
				cfg.Pipelines = defaultPipelines
				healed = true
			}

			// 自动适配三阶分层字段 (PipelineID 与 Type)
			for i := range cfg.Inbounds {
				if cfg.Inbounds[i].Type == "" {
					cfg.Inbounds[i].Type = "port"
					healed = true
				}
				if cfg.Inbounds[i].PipelineID == "" {
					cfg.Inbounds[i].PipelineID = "pipe-default"
					healed = true
				}
			}

			if healed {
				_ = cfg.Save()
			}
			return cfg, nil
		}
	}

	// 初始化默认三阶解耦配置 (自适应宿主容器内部直通端口与真实 Ingress Token)
	cfg.Pipelines = defaultPipelines
	cfg.Upstreams = []UpstreamNode{
		{
			ID:            "default-lwt-node",
			Name:          "远程 LWT 默认宿主节点",
			Type:          "lwt_wss",
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
			Type:             "port",
			Enabled:          true,
			ListenPort:       10801,
			AdditionalPorts:  []int{},
			Protocol:         "mixed",
			AllowedCIDR:      []string{"10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8", "::1/128"},
			PipelineID:       "pipe-default",
			UpstreamStrategy: "single",
			UpstreamNodes:    []string{"default-lwt-node"},
			RoutePolicy: RoutePolicyConfig{
				DirectPrivateNetwork: true,
				Rules: []RuleConfig{
					{Type: "domain_suffix", Value: ".cn", Action: "direct"},
					{Type: "domain_suffix", Value: ".bilibili.com", Action: "direct"},
				},
				DefaultAction:   "proxy",
				DefaultUpstream: "default-lwt-node",
			},
			Status: "stopped",
		},
	}

	_ = cfg.Save()
	return cfg, nil
}

// Save 持久化保存配置至磁盘
func (c *Config) Save() error {
	c.mu.Lock()
	defer c.mu.Unlock()

	if c.filePath == "" {
		return nil
	}

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
