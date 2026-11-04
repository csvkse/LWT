package main

// RoutingTemplate 分流规则预设模板定义
type RoutingTemplate struct {
	ID                   string       `json:"id"`
	Name                 string       `json:"name"`
	Description          string       `json:"description"`
	DirectPrivateNetwork bool         `json:"directPrivateNetwork"`
	DefaultAction        string       `json:"defaultAction"` // proxy, direct, reject
	Rules                []RuleConfig `json:"rules"`
}

// GetBuiltinRoutingTemplates 获取内置的 5 大常用分流模板
func GetBuiltinRoutingTemplates() []RoutingTemplate {
	return []RoutingTemplate{
		{
			ID:                   "tpl_balanced",
			Name:                 "标准平衡分流 (推荐)",
			Description:          "局域网与国内主流服务直连，拦截广告隐私追踪，境外服务走代理",
			DirectPrivateNetwork: true,
			DefaultAction:        "proxy",
			Rules: []RuleConfig{
				{Type: "domain_keyword", Value: "adservice", Action: "reject"},
				{Type: "domain_keyword", Value: "telemetry", Action: "reject"},
				{Type: "domain_keyword", Value: "tracking", Action: "reject"},
				{Type: "domain_suffix", Value: ".cn", Action: "direct"},
				{Type: "domain_suffix", Value: "bilibili.com", Action: "direct"},
				{Type: "domain_suffix", Value: "qq.com", Action: "direct"},
				{Type: "domain_suffix", Value: "aliyun.com", Action: "direct"},
				{Type: "domain_suffix", Value: "baidu.com", Action: "direct"},
				{Type: "ip_cidr", Value: "223.5.5.0/24", Action: "direct"},
				{Type: "ip_cidr", Value: "119.29.29.29/32", Action: "direct"},
				{Type: "ip_cidr", Value: "114.114.114.114/32", Action: "direct"},
			},
		},
		{
			ID:                   "tpl_ai_developer",
			Name:                 "AI 研发与模型加速专线",
			Description:          "OpenAI、Claude、Copilot、HuggingFace 专属代理加速，大陆服务直连",
			DirectPrivateNetwork: true,
			DefaultAction:        "proxy",
			Rules: []RuleConfig{
				{Type: "domain_suffix", Value: "openai.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "chatgpt.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "oaistatic.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "oaiusercontent.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "anthropic.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "claude.ai", Action: "proxy"},
				{Type: "domain_suffix", Value: "githubcopilot.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "huggingface.co", Action: "proxy"},
				{Type: "domain_suffix", Value: ".cn", Action: "direct"},
			},
		},
		{
			ID:                   "tpl_adblock",
			Name:                 "强力广告与隐私拦截模式",
			Description:          "阻断各类埋点、遥测及分析脚本，其余常规分流代理",
			DirectPrivateNetwork: true,
			DefaultAction:        "proxy",
			Rules: []RuleConfig{
				{Type: "domain_keyword", Value: "adservice", Action: "reject"},
				{Type: "domain_keyword", Value: "google-analytics", Action: "reject"},
				{Type: "domain_keyword", Value: "telemetry", Action: "reject"},
				{Type: "domain_keyword", Value: "cnzz", Action: "reject"},
				{Type: "domain_keyword", Value: "umeng", Action: "reject"},
				{Type: "domain_keyword", Value: "log.byteoversea", Action: "reject"},
				{Type: "domain_suffix", Value: ".cn", Action: "direct"},
			},
		},
		{
			ID:                   "tpl_global_proxy",
			Name:                 "全量出海模式",
			Description:          "仅保留局域网私网直连，其余所有流量均通过代理网关转发",
			DirectPrivateNetwork: true,
			DefaultAction:        "proxy",
			Rules:                []RuleConfig{},
		},
		{
			ID:                   "tpl_minimal_whitelist",
			Name:                 "极简白名单模式",
			Description:          "仅指定的主流外网站点走代理，其余流量全部直连",
			DirectPrivateNetwork: true,
			DefaultAction:        "direct",
			Rules: []RuleConfig{
				{Type: "domain_suffix", Value: "google.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "youtube.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "github.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "twitter.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "x.com", Action: "proxy"},
				{Type: "domain_suffix", Value: "wikipedia.org", Action: "proxy"},
			},
		},
	}
}

// GetRoutingTemplateByID 根据 ID 查询模板
func GetRoutingTemplateByID(id string) *RoutingTemplate {
	for _, tpl := range GetBuiltinRoutingTemplates() {
		if tpl.ID == id {
			return &tpl
		}
	}
	return nil
}
