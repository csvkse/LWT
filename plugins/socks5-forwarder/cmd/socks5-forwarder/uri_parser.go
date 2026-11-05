package main

import (
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"net/url"
	"strconv"
	"strings"
)

// IProtocolLinkParser 链接解析策略接口
type IProtocolLinkParser interface {
	Scheme() string
	CanParse(rawLink string) bool
	Parse(rawLink string) (UpstreamNode, error)
}

// LinkParserRegistry 链接解析器注册表
type LinkParserRegistry struct {
	parsers map[string]IProtocolLinkParser
}

// GlobalLinkParser 全局注册表单例
var GlobalLinkParser = NewLinkParserRegistry()

// NewLinkParserRegistry 构造注册表并预注册核心解析器
func NewLinkParserRegistry() *LinkParserRegistry {
	r := &LinkParserRegistry{
		parsers: make(map[string]IProtocolLinkParser),
	}
	r.Register(&VlessLinkParser{})
	r.Register(&TrojanLinkParser{})
	r.Register(&ShadowsocksLinkParser{})
	r.Register(&Socks5LinkParser{})
	r.Register(&HttpLinkParser{})
	r.Register(&VmessLinkParser{})
	return r
}

// Register 注册解析器
func (r *LinkParserRegistry) Register(p IProtocolLinkParser) {
	r.parsers[strings.ToLower(p.Scheme())] = p
}

// Parse 解析单个链接
func (r *LinkParserRegistry) Parse(rawLink string) (UpstreamNode, error) {
	trimmed := strings.TrimSpace(rawLink)
	if trimmed == "" {
		return UpstreamNode{}, errors.New("链接内容为空")
	}

	colonIdx := strings.Index(trimmed, "://")
	if colonIdx == -1 {
		return UpstreamNode{}, fmt.Errorf("无效的协议链接格式: 缺少 scheme (://)")
	}
	scheme := strings.ToLower(trimmed[:colonIdx])

	parser, exists := r.parsers[scheme]
	if !exists {
		// 别名兼容
		if scheme == "socks" {
			parser = r.parsers["socks5"]
		} else if scheme == "https" {
			parser = r.parsers["http"]
		}
	}

	if parser == nil {
		return UpstreamNode{}, fmt.Errorf("暂不支持该协议链接: %s", scheme)
	}

	return parser.Parse(trimmed)
}

// ParseUpstreamLink 全局便捷解析函数
func ParseUpstreamLink(rawLink string) (UpstreamNode, error) {
	return GlobalLinkParser.Parse(rawLink)
}

// ParseUpstreamLinks 批量解析多行文本中的节点链接（自动清洗注释与空行）
func ParseUpstreamLinks(rawText string) ([]UpstreamNode, []string) {
	lines := strings.Split(rawText, "\n")
	var nodes []UpstreamNode
	var errs []string

	for idx, line := range lines {
		trimmed := strings.TrimSpace(line)
		// 忽略空行和行注释
		if trimmed == "" || strings.HasPrefix(trimmed, "#") || strings.HasPrefix(trimmed, "//") {
			continue
		}

		node, err := ParseUpstreamLink(trimmed)
		if err != nil {
			errs = append(errs, fmt.Sprintf("第 %d 行解析失败: %v", idx+1, err))
			continue
		}
		// 生成唯一 ID 若缺失
		if node.ID == "" {
			node.ID = fmt.Sprintf("node-%s-%d", node.Type, idx+1)
		}
		node.Status = "unknown"
		nodes = append(nodes, node)
	}

	return nodes, errs
}

// decodeBase64Safe 自适应 Base64 解码器（兼容 URL-safe 与缺失 padding）
func decodeBase64Safe(s string) ([]byte, error) {
	s = strings.TrimSpace(s)
	// 补齐 padding
	if m := len(s) % 4; m != 0 {
		s += strings.Repeat("=", 4-m)
	}
	// 尝试 URL Safe 解码
	if data, err := base64.URLEncoding.DecodeString(s); err == nil {
		return data, nil
	}
	// 尝试标准解码
	return base64.StdEncoding.DecodeString(s)
}

// ======================== VLESS 解析器 ========================

type VlessLinkParser struct{}

func (p *VlessLinkParser) Scheme() string { return "vless" }
func (p *VlessLinkParser) CanParse(link string) bool {
	return strings.HasPrefix(strings.ToLower(link), "vless://")
}
func (p *VlessLinkParser) Parse(rawLink string) (UpstreamNode, error) {
	u, err := url.Parse(rawLink)
	if err != nil {
		return UpstreamNode{}, fmt.Errorf("解析 vless 链接失败: %w", err)
	}

	uuid := u.User.Username()
	if uuid == "" {
		return UpstreamNode{}, errors.New("vless 链接缺少 UUID")
	}

	host, portStr, err := net.SplitHostPort(u.Host)
	if err != nil {
		// 默认 443 端口
		host = u.Host
		portStr = "443"
	}
	port, _ := strconv.Atoi(portStr)
	if port <= 0 {
		port = 443
	}

	q := u.Query()
	sni := q.Get("sni")
	if sni == "" {
		sni = q.Get("peer")
	}
	if sni == "" {
		sni = q.Get("host")
	}
	security := strings.ToLower(q.Get("security"))
	skipVerify := q.Get("allowInsecure") == "1" || q.Get("skipTlsVerify") == "true" || q.Get("insecure") == "1"

	flow := strings.ToLower(q.Get("flow"))
	fp := strings.ToLower(q.Get("fp"))
	if fp == "" {
		fp = strings.ToLower(q.Get("fingerprint"))
	}
	transport := strings.ToLower(q.Get("type"))
	if transport == "" {
		transport = "tcp"
	}

	name := u.Fragment
	if name != "" {
		if unescaped, err := url.QueryUnescape(name); err == nil {
			name = unescaped
		}
	}
	if name == "" {
		name = fmt.Sprintf("VLESS-%s:%d", host, port)
	}

	return UpstreamNode{
		Type:          "vless",
		Name:          name,
		Server:        host,
		Port:          port,
		UUID:          uuid,
		Flow:          flow,
		Fingerprint:   fp,
		Transport:     transport,
		SNI:           sni,
		SkipTLSVerify: skipVerify || security == "none",
	}, nil
}

// ======================== TROJAN 解析器 ========================

type TrojanLinkParser struct{}

func (p *TrojanLinkParser) Scheme() string { return "trojan" }
func (p *TrojanLinkParser) CanParse(link string) bool {
	return strings.HasPrefix(strings.ToLower(link), "trojan://")
}
func (p *TrojanLinkParser) Parse(rawLink string) (UpstreamNode, error) {
	u, err := url.Parse(rawLink)
	if err != nil {
		return UpstreamNode{}, fmt.Errorf("解析 trojan 链接失败: %w", err)
	}

	pwd := u.User.Username()
	if pwd == "" {
		if p, ok := u.User.Password(); ok {
			pwd = p
		}
	}
	if pwd == "" {
		return UpstreamNode{}, errors.New("trojan 链接缺少密码")
	}

	host, portStr, err := net.SplitHostPort(u.Host)
	if err != nil {
		host = u.Host
		portStr = "443"
	}
	port, _ := strconv.Atoi(portStr)
	if port <= 0 {
		port = 443
	}

	q := u.Query()
	sni := q.Get("sni")
	if sni == "" {
		sni = q.Get("peer")
	}
	skipVerify := q.Get("allowInsecure") == "1" || q.Get("skipTlsVerify") == "true" || q.Get("insecure") == "1"

	name := u.Fragment
	if name != "" {
		if unescaped, err := url.QueryUnescape(name); err == nil {
			name = unescaped
		}
	}
	if name == "" {
		name = fmt.Sprintf("Trojan-%s:%d", host, port)
	}

	return UpstreamNode{
		Type:          "trojan",
		Name:          name,
		Server:        host,
		Port:          port,
		Password:      pwd,
		SNI:           sni,
		SkipTLSVerify: skipVerify,
	}, nil
}

// ======================== SHADOWSOCKS 解析器 ========================

type ShadowsocksLinkParser struct{}

func (p *ShadowsocksLinkParser) Scheme() string { return "ss" }
func (p *ShadowsocksLinkParser) CanParse(link string) bool {
	return strings.HasPrefix(strings.ToLower(link), "ss://")
}
func (p *ShadowsocksLinkParser) Parse(rawLink string) (UpstreamNode, error) {
	// ss://<base64>#tag or ss://<user>@<host>:<port>#tag
	trimmed := strings.TrimPrefix(rawLink, "ss://")
	var tag string
	if hashIdx := strings.Index(trimmed, "#"); hashIdx != -1 {
		tag = trimmed[hashIdx+1:]
		trimmed = trimmed[:hashIdx]
		if unescaped, err := url.QueryUnescape(tag); err == nil {
			tag = unescaped
		}
	}

	var method, password, host string
	var port int

	// SIP002 格式 1: user-info base64: method:password@host:port
	if atIdx := strings.Index(trimmed, "@"); atIdx != -1 {
		userInfoRaw := trimmed[:atIdx]
		hostPortRaw := trimmed[atIdx+1:]

		// 用户名可能是 base64 编码的 method:password
		userInfoDecoded, err := decodeBase64Safe(userInfoRaw)
		if err == nil && strings.Contains(string(userInfoDecoded), ":") {
			parts := strings.SplitN(string(userInfoDecoded), ":", 2)
			method = parts[0]
			password = parts[1]
		} else if strings.Contains(userInfoRaw, ":") {
			parts := strings.SplitN(userInfoRaw, ":", 2)
			method = parts[0]
			password = parts[1]
		}

		h, pStr, err := net.SplitHostPort(hostPortRaw)
		if err == nil {
			host = h
			port, _ = strconv.Atoi(pStr)
		}
	} else {
		// SIP002 格式 2: 全链接 Base64: base64(method:password@host:port)
		decoded, err := decodeBase64Safe(trimmed)
		if err != nil {
			return UpstreamNode{}, fmt.Errorf("无法解码 Shadowsocks Base64: %w", err)
		}
		decodedStr := string(decoded)
		atIdx := strings.Index(decodedStr, "@")
		if atIdx == -1 {
			return UpstreamNode{}, errors.New("Shadowsocks 解密后缺少 @ 分隔符")
		}
		userPart := decodedStr[:atIdx]
		hostPart := decodedStr[atIdx+1:]

		userParts := strings.SplitN(userPart, ":", 2)
		if len(userParts) == 2 {
			method = userParts[0]
			password = userParts[1]
		}

		h, pStr, err := net.SplitHostPort(hostPart)
		if err == nil {
			host = h
			port, _ = strconv.Atoi(pStr)
		}
	}

	if host == "" || port <= 0 {
		return UpstreamNode{}, errors.New("Shadowsocks 链接缺少有效的目标主机与端口")
	}

	if tag == "" {
		tag = fmt.Sprintf("SS-%s:%d", host, port)
	}

	return UpstreamNode{
		Type:     "shadowsocks",
		Name:     tag,
		Server:   host,
		Port:     port,
		Method:   method,
		Password: password,
	}, nil
}

// ======================== SOCKS5 解析器 ========================

type Socks5LinkParser struct{}

func (p *Socks5LinkParser) Scheme() string { return "socks5" }
func (p *Socks5LinkParser) CanParse(link string) bool {
	lower := strings.ToLower(link)
	return strings.HasPrefix(lower, "socks5://") || strings.HasPrefix(lower, "socks://")
}
func (p *Socks5LinkParser) Parse(rawLink string) (UpstreamNode, error) {
	u, err := url.Parse(rawLink)
	if err != nil {
		return UpstreamNode{}, fmt.Errorf("解析 socks5 链接失败: %w", err)
	}

	host, portStr, err := net.SplitHostPort(u.Host)
	if err != nil {
		host = u.Host
		portStr = "1080"
	}
	port, _ := strconv.Atoi(portStr)
	if port <= 0 {
		port = 1080
	}

	var username, password string
	if u.User != nil {
		username = u.User.Username()
		password, _ = u.User.Password()
	}

	name := u.Fragment
	if name != "" {
		if unescaped, err := url.QueryUnescape(name); err == nil {
			name = unescaped
		}
	}
	if name == "" {
		name = fmt.Sprintf("SOCKS5-%s:%d", host, port)
	}

	return UpstreamNode{
		Type:     "socks5",
		Name:     name,
		Server:   host,
		Port:     port,
		Username: username,
		Password: password,
	}, nil
}

// ======================== HTTP(S) 解析器 ========================

type HttpLinkParser struct{}

func (p *HttpLinkParser) Scheme() string { return "http" }
func (p *HttpLinkParser) CanParse(link string) bool {
	lower := strings.ToLower(link)
	return strings.HasPrefix(lower, "http://") || strings.HasPrefix(lower, "https://")
}
func (p *HttpLinkParser) Parse(rawLink string) (UpstreamNode, error) {
	u, err := url.Parse(rawLink)
	if err != nil {
		return UpstreamNode{}, fmt.Errorf("解析 http 链接失败: %w", err)
	}

	isHttps := strings.EqualFold(u.Scheme, "https")
	defaultPort := "80"
	if isHttps {
		defaultPort = "443"
	}

	host, portStr, err := net.SplitHostPort(u.Host)
	if err != nil {
		host = u.Host
		portStr = defaultPort
	}
	port, _ := strconv.Atoi(portStr)
	if port <= 0 {
		port = 80
		if isHttps {
			port = 443
		}
	}

	var username, password string
	if u.User != nil {
		username = u.User.Username()
		password, _ = u.User.Password()
	}

	name := u.Fragment
	if name != "" {
		if unescaped, err := url.QueryUnescape(name); err == nil {
			name = unescaped
		}
	}
	if name == "" {
		name = fmt.Sprintf("HTTP-%s:%d", host, port)
	}

	return UpstreamNode{
		Type:          "http",
		Name:          name,
		Server:        host,
		Port:          port,
		Username:      username,
		Password:      password,
		SkipTLSVerify: !isHttps,
	}, nil
}

// ======================== VMESS 解析器 ========================

type VmessLinkParser struct{}

func (p *VmessLinkParser) Scheme() string { return "vmess" }
func (p *VmessLinkParser) CanParse(link string) bool {
	return strings.HasPrefix(strings.ToLower(link), "vmess://")
}

type vmessJsonConfig struct {
	V    interface{} `json:"v"`
	Ps   string      `json:"ps"`
	Add  string      `json:"add"`
	Port interface{} `json:"port"`
	Id   string      `json:"id"`
	Aid  interface{} `json:"aid"`
	Net  string      `json:"net"`
	Type string      `json:"type"`
	Host string      `json:"host"`
	Path string      `json:"path"`
	Tls  string      `json:"tls"`
	Sni  string      `json:"sni"`
}

func (p *VmessLinkParser) Parse(rawLink string) (UpstreamNode, error) {
	trimmed := strings.TrimPrefix(rawLink, "vmess://")
	decoded, err := decodeBase64Safe(trimmed)
	if err != nil {
		return UpstreamNode{}, fmt.Errorf("解密 vmess base64 失败: %w", err)
	}

	var vj vmessJsonConfig
	if err := json.Unmarshal(decoded, &vj); err != nil {
		return UpstreamNode{}, fmt.Errorf("反序列化 vmess JSON 失败: %w", err)
	}

	port := 443
	switch val := vj.Port.(type) {
	case float64:
		port = int(val)
	case string:
		port, _ = strconv.Atoi(val)
	}

	name := vj.Ps
	if name == "" {
		name = fmt.Sprintf("VMess-%s:%d", vj.Add, port)
	}

	sni := vj.Sni
	if sni == "" {
		sni = vj.Host
	}

	return UpstreamNode{
		Type:          "vmess",
		Name:          name,
		Server:        vj.Add,
		Port:          port,
		UUID:          vj.Id,
		SNI:           sni,
		SkipTLSVerify: vj.Tls == "" || vj.Tls == "none",
	}, nil
}
