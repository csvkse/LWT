package main

import (
	"net"
	"strings"
	"sync"
)

// SessionMetadata 会话元数据
type SessionMetadata struct {
	Host       string
	Port       int
	Network    string
	SourceAddr net.Addr
}

// 预定义私网/保留网段列表 (RFC 1918, RFC 3927, RFC 4193, RFC 4291, Loopback, Link-Local)
var privateCIDRs []*net.IPNet

func init() {
	rawCIDRs := []string{
		"10.0.0.0/8",     // RFC 1918 Class A
		"172.16.0.0/12",  // RFC 1918 Class B
		"192.168.0.0/16", // RFC 1918 Class C
		"127.0.0.0/8",    // IPv4 回环
		"169.254.0.0/16", // 链路本地
		"100.64.0.0/10",  // 运营商级 NAT (CGNAT)
		"::1/128",        // IPv6 回环
		"fc00::/7",       // IPv6 唯一本地地址 (ULA)
		"fe80::/10",      // IPv6 链路本地
	}

	for _, s := range rawCIDRs {
		_, ipNet, err := net.ParseCIDR(s)
		if err == nil {
			privateCIDRs = append(privateCIDRs, ipNet)
		}
	}
}

// IsPrivateNetwork 判定目标 Host 是否属于局域网或私有保留地址
func IsPrivateNetwork(host string) bool {
	cleanHost := strings.Trim(host, "[]")
	if cleanHost == "localhost" || strings.HasSuffix(cleanHost, ".local") || strings.HasSuffix(cleanHost, ".lan") {
		return true
	}

	ip := net.ParseIP(cleanHost)
	if ip == nil {
		return false
	}

	for _, cidr := range privateCIDRs {
		if cidr.Contains(ip) {
			return true
		}
	}
	return false
}

// DomainTrieNode 域名字典树节点
type DomainTrieNode struct {
	children map[string]*DomainTrieNode
	action   string
	target   string
	isEnd    bool
}

// DomainTrie 双向分段域名字典树 (高效后缀与完全匹配)
type DomainTrie struct {
	mu   sync.RWMutex
	root *DomainTrieNode
}

func NewDomainTrie() *DomainTrie {
	return &DomainTrie{
		root: &DomainTrieNode{children: make(map[string]*DomainTrieNode)},
	}
}

// Insert 添加域名规则（如 "*.cn", ".bilibili.com", "apple.com"）
func (t *DomainTrie) Insert(domain, action, target string) {
	t.mu.Lock()
	defer t.mu.Unlock()

	clean := strings.ToLower(strings.TrimSpace(domain))
	clean = strings.TrimPrefix(clean, "*.")
	clean = strings.TrimPrefix(clean, ".")

	parts := strings.Split(clean, ".")
	node := t.root

	// 反向插入分段 (com -> bilibili -> www)
	for i := len(parts) - 1; i >= 0; i-- {
		part := parts[i]
		if _, exists := node.children[part]; !exists {
			node.children[part] = &DomainTrieNode{children: make(map[string]*DomainTrieNode)}
		}
		node = node.children[part]
	}

	node.isEnd = true
	node.action = action
	node.target = target
}

// Match 匹配域名规则，返回 action 与 target
func (t *DomainTrie) Match(domain string) (action, target string, matched bool) {
	t.mu.RLock()
	defer t.mu.RUnlock()

	clean := strings.ToLower(strings.TrimSpace(domain))
	parts := strings.Split(clean, ".")

	node := t.root
	for i := len(parts) - 1; i >= 0; i-- {
		part := parts[i]
		child, exists := node.children[part]
		if !exists {
			break
		}
		node = child
		if node.isEnd {
			return node.action, node.target, true
		}
	}

	return "", "", false
}

// RuleRoutingDispatcher 责任链规则分流决策中枢
type RuleRoutingDispatcher struct {
	directPrivate bool
	domainTrie    *DomainTrie
	cidrRules     []cidrRuleEntry
	keywordRules  []keywordRuleEntry
	defaultAction string
	defaultTarget string
}

type cidrRuleEntry struct {
	ipNet  *net.IPNet
	action string
	target string
}

type keywordRuleEntry struct {
	keyword string
	action  string
	target  string
}

// NewRuleRoutingDispatcher 创建分流调度器
func NewRuleRoutingDispatcher(policy RoutePolicyConfig) *RuleRoutingDispatcher {
	d := &RuleRoutingDispatcher{
		directPrivate: policy.DirectPrivateNetwork,
		domainTrie:    NewDomainTrie(),
		cidrRules:     make([]cidrRuleEntry, 0),
		keywordRules:  make([]keywordRuleEntry, 0),
		defaultAction: policy.DefaultAction,
		defaultTarget: policy.DefaultUpstream,
	}

	if d.defaultAction == "" {
		d.defaultAction = "proxy"
	}

	for _, rule := range policy.Rules {
		act := rule.Action
		if act == "" {
			act = "direct"
		}

		switch strings.ToLower(rule.Type) {
		case "domain_suffix", "domain":
			d.domainTrie.Insert(rule.Value, act, rule.Target)
		case "domain_keyword":
			d.keywordRules = append(d.keywordRules, keywordRuleEntry{
				keyword: strings.ToLower(rule.Value),
				action:  act,
				target:  rule.Target,
			})
		case "ip_cidr":
			_, ipNet, err := net.ParseCIDR(rule.Value)
			if err == nil {
				d.cidrRules = append(d.cidrRules, cidrRuleEntry{
					ipNet:  ipNet,
					action: act,
					target: rule.Target,
				})
			}
		}
	}

	return d
}

// Route 评估会话元数据，决定流向
func (d *RuleRoutingDispatcher) Route(meta *SessionMetadata) (action, target string) {
	if meta == nil {
		return d.defaultAction, d.defaultTarget
	}

	// 1. 局域网私网直连短路
	if d.directPrivate && IsPrivateNetwork(meta.Host) {
		return "direct", ""
	}

	// 2. 域名规则匹配 (Domain Trie 后缀树)
	if act, tgt, matched := d.domainTrie.Match(meta.Host); matched {
		return act, tgt
	}

	// 3. 域名关键词匹配
	lowerHost := strings.ToLower(meta.Host)
	for _, kw := range d.keywordRules {
		if strings.Contains(lowerHost, kw.keyword) {
			return kw.action, kw.target
		}
	}

	// 4. IP-CIDR 规则匹配
	if ip := net.ParseIP(meta.Host); ip != nil {
		for _, cr := range d.cidrRules {
			if cr.ipNet.Contains(ip) {
				return cr.action, cr.target
			}
		}
	}

	// 5. 兜底默认行为
	return d.defaultAction, d.defaultTarget
}
