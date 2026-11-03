package main

import (
	"fmt"
	"net"
	"path"
	"regexp"
	"strings"
	"sync/atomic"
)

// Predefined ACL Presets
const (
	PresetLan      = "lan"
	PresetLoopback = "loopback"
	PresetAll      = "all"
)

var defaultLanSubnets = []string{
	"10.0.0.0/8",
	"172.16.0.0/12",
	"192.168.0.0/16",
	"127.0.0.0/8",
	"::1/128",
	"fc00::/7",
	"fe80::/10",
}

var defaultLanDomains = []string{
	"*.local",
	"*.internal",
	"*.lan",
	"*.home.arpa",
	"localhost",
}

// SubnetRule matches an IP against either a CIDR or a convenient prefix (e.g. "10.", "192.168.").
type SubnetRule struct {
	raw    string
	cidr   *net.IPNet
	prefix string
}

func (r SubnetRule) Match(ip net.IP) bool {
	if ip == nil {
		return false
	}
	if ip4 := ip.To4(); ip4 != nil {
		ip = ip4
	}
	if r.cidr != nil {
		return r.cidr.Contains(ip)
	}
	if r.prefix != "" {
		s := ip.String()
		return strings.HasPrefix(s, r.prefix+".") || s == r.prefix
	}
	return false
}

func (r SubnetRule) String() string {
	if r.cidr != nil {
		return r.cidr.String()
	}
	return r.raw
}

// DomainMatcher provides contract for matching hostnames.
type DomainMatcher interface {
	Match(domain string) bool
	String() string
}

// ExactDomainRule matches exact domain case-insensitively.
type ExactDomainRule string

func (r ExactDomainRule) Match(d string) bool {
	return strings.EqualFold(string(r), d)
}

func (r ExactDomainRule) String() string {
	return string(r)
}

// WildcardDomainRule supports *.domain (fast suffix path) and general glob patterns (e.g. node-*.lan).
type WildcardDomainRule struct {
	pattern string
	suffix  string // if pattern starts with "*.", suffix is ".domain"
}

func (r WildcardDomainRule) Match(d string) bool {
	domain := strings.ToLower(d)
	if r.suffix != "" {
		return strings.HasSuffix(domain, r.suffix) || domain == r.suffix[1:]
	}
	matched, _ := path.Match(r.pattern, domain)
	return matched
}

func (r WildcardDomainRule) String() string {
	return r.pattern
}

// RegexDomainRule provides linear-time regular expression matching using Go RE2.
type RegexDomainRule struct {
	raw string
	re  *regexp.Regexp
}

func (r RegexDomainRule) Match(d string) bool {
	return r.re.MatchString(d)
}

func (r RegexDomainRule) String() string {
	return r.raw
}

// CompiledAcl holds the immutable compiled ruleset for lock-free atomic evaluation.
type CompiledAcl struct {
	Preset         string
	AllowAllTarget bool
	AllowAllClient bool
	RawSubnets     string
	RawDomains     string
	RawClients     string
	SubnetRules    []SubnetRule
	DomainRules    []DomainMatcher
	ClientRules    []SubnetRule
}

// AclStats records runtime evaluation metrics.
type AclStats struct {
	Preset         string   `json:"preset"`
	AllowedTargets uint64   `json:"allowedTargets"`
	BlockedTargets uint64   `json:"blockedTargets"`
	BlockedClients uint64   `json:"blockedClients"`
	AuthFailures   uint64   `json:"authFailures"`
	SubnetRules    []string `json:"subnetRules"`
	DomainRules    []string `json:"domainRules"`
	ClientRules    []string `json:"clientRules"`
	AllowAllTarget bool     `json:"allowAllTarget"`
	AllowAllClient bool     `json:"allowAllClient"`
}

// AclEngine orchestrates multi-strategy access control evaluation with RCU atomic reloading.
type AclEngine struct {
	current        atomic.Pointer[CompiledAcl]
	allowedTargets atomic.Uint64
	blockedTargets atomic.Uint64
	blockedClients atomic.Uint64
	authFailures   atomic.Uint64
}

func NewAclEngine(preset, subnets, domains, clients string) (*AclEngine, error) {
	engine := &AclEngine{}
	if err := engine.Reload(preset, subnets, domains, clients); err != nil {
		return nil, err
	}
	return engine, nil
}

func (e *AclEngine) Reload(preset, subnets, domains, clients string) error {
	compiled, err := compileAclRules(preset, subnets, domains, clients)
	if err != nil {
		return err
	}
	e.current.Store(compiled)
	return nil
}

func (e *AclEngine) RecordAuthFailure() {
	e.authFailures.Add(1)
}

func (e *AclEngine) RecordClientBlocked() {
	e.blockedClients.Add(1)
}

// normalizeSubnetCidr normalizes prefixes like "10.", "192.168.", "172.16.", "127." and single IPs to canonical CIDR format.
func normalizeSubnetCidr(raw string) (cidrStr string, prefixStr string) {
	trimmed := strings.TrimSpace(raw)
	if strings.Contains(trimmed, "/") {
		return trimmed, ""
	}

	if ip := net.ParseIP(trimmed); ip != nil {
		if ip.To4() != nil {
			return trimmed + "/32", trimmed
		}
		return trimmed + "/128", trimmed
	}

	cleanPrefix := strings.TrimRight(trimmed, ".")
	switch cleanPrefix {
	case "10":
		return "10.0.0.0/8", "10"
	case "172.16":
		return "172.16.0.0/12", "172.16"
	case "192.168":
		return "192.168.0.0/16", "192.168"
	case "127":
		return "127.0.0.0/8", "127"
	case "169.254":
		return "169.254.0.0/16", "169.254"
	}

	parts := strings.Split(cleanPrefix, ".")
	if len(parts) == 1 {
		return cleanPrefix + ".0.0.0/8", cleanPrefix
	} else if len(parts) == 2 {
		return cleanPrefix + ".0.0/16", cleanPrefix
	} else if len(parts) == 3 {
		return cleanPrefix + ".0/24", cleanPrefix
	}

	return trimmed, cleanPrefix
}

func compileAclRules(preset, rawSubnets, rawDomains, rawClients string) (*CompiledAcl, error) {
	preset = strings.ToLower(strings.TrimSpace(preset))
	if preset == "" {
		preset = PresetLan
	}

	acl := &CompiledAcl{
		Preset:     preset,
		RawSubnets: rawSubnets,
		RawDomains: rawDomains,
		RawClients: rawClients,
	}

	// 1. Preset baseline
	var subnetInputs []string
	var domainInputs []string

	switch preset {
	case PresetAll:
		acl.AllowAllTarget = true
	case PresetLoopback:
		subnetInputs = append(subnetInputs, "127.0.0.0/8", "::1/128")
		domainInputs = append(domainInputs, "localhost")
	case PresetLan:
		fallthrough
	default:
		subnetInputs = append(subnetInputs, defaultLanSubnets...)
		domainInputs = append(domainInputs, defaultLanDomains...)
	}

	// 2. Append explicit subnet rules (e.g. "10.", "192.168.", "10.0.0.0/8")
	for _, part := range splitRules(rawSubnets) {
		if part == "*" || part == "0.0.0.0/0" || strings.EqualFold(part, "all") {
			acl.AllowAllTarget = true
			continue
		}
		rule, err := parseSubnetRule(part)
		if err != nil {
			return nil, fmt.Errorf("invalid subnet rule '%s': %w", part, err)
		}
		acl.SubnetRules = append(acl.SubnetRules, rule)
	}

	// Add preset subnets if not already allowAll
	if !acl.AllowAllTarget {
		for _, s := range subnetInputs {
			rule, _ := parseSubnetRule(s)
			acl.SubnetRules = append(acl.SubnetRules, rule)
		}
	}

	// 3. Append explicit domain rules (wildcard *.internal, exact, regex:...)
	for _, part := range splitRules(rawDomains) {
		if part == "*" || strings.EqualFold(part, "all") {
			acl.AllowAllTarget = true
			continue
		}
		matcher, err := parseDomainMatcher(part)
		if err != nil {
			return nil, fmt.Errorf("invalid domain rule '%s': %w", part, err)
		}
		acl.DomainRules = append(acl.DomainRules, matcher)
	}

	for _, d := range domainInputs {
		matcher, _ := parseDomainMatcher(d)
		acl.DomainRules = append(acl.DomainRules, matcher)
	}

	// 4. Parse client whitelist if specified
	clientParts := splitRules(rawClients)
	if len(clientParts) == 0 {
		switch preset {
		case PresetLoopback:
			for _, cp := range []string{"127.0.0.1/32", "::1/128"} {
				rule, _ := parseSubnetRule(cp)
				acl.ClientRules = append(acl.ClientRules, rule)
			}
		case PresetLan:
			fallthrough
		case PresetAll:
			// 出站目标即使全网开放 (PresetAll)，入站客户端来源默认仍为局域网私网 (LAN) 保护，严防公网未授权探针
			fallthrough
		default:
			for _, cp := range defaultLanSubnets {
				rule, _ := parseSubnetRule(cp)
				acl.ClientRules = append(acl.ClientRules, rule)
			}
		}
	} else if len(clientParts) == 1 && (clientParts[0] == "*" || clientParts[0] == "0.0.0.0/0" || strings.EqualFold(clientParts[0], "all")) {
		acl.AllowAllClient = true
	} else {
		for _, cp := range clientParts {
			if cp == "*" || cp == "0.0.0.0/0" || strings.EqualFold(cp, "all") {
				acl.AllowAllClient = true
				continue
			}
			rule, err := parseSubnetRule(cp)
			if err != nil {
				return nil, fmt.Errorf("invalid client rule '%s': %w", cp, err)
			}
			acl.ClientRules = append(acl.ClientRules, rule)
		}
	}

	return acl, nil
}

func splitRules(input string) []string {
	if strings.TrimSpace(input) == "" {
		return nil
	}
	var res []string
	// Support comma, semicolon, and newline separation
	normalized := strings.ReplaceAll(input, "\r\n", "\n")
	normalized = strings.ReplaceAll(normalized, ";", ",")
	normalized = strings.ReplaceAll(normalized, "\n", ",")
	for _, item := range strings.Split(normalized, ",") {
		trimmed := strings.TrimSpace(item)
		if trimmed != "" {
			res = append(res, trimmed)
		}
	}
	return res
}

func parseSubnetRule(raw string) (SubnetRule, error) {
	rule := SubnetRule{raw: raw}
	cidrStr, prefixStr := normalizeSubnetCidr(raw)
	rule.prefix = prefixStr

	// Try standard CIDR parsing first
	if strings.Contains(cidrStr, "/") {
		_, ipnet, err := net.ParseCIDR(cidrStr)
		if err == nil {
			rule.cidr = ipnet
			return rule, nil
		}
	}

	// Check if single IP
	if ip := net.ParseIP(raw); ip != nil {
		bits := 32
		if ip.To4() == nil {
			bits = 128
		}
		mask := net.CIDRMask(bits, bits)
		rule.cidr = &net.IPNet{IP: ip, Mask: mask}
		return rule, nil
	}

	// Convenient prefix syntax (e.g. "10.", "192.168.", "127.")
	prefix := strings.TrimRight(raw, ".")
	if prefix == "" {
		return rule, fmt.Errorf("empty prefix")
	}
	rule.prefix = prefix
	return rule, nil
}

func parseDomainMatcher(raw string) (DomainMatcher, error) {
	lower := strings.ToLower(raw)
	// Check regex: prefix or /.../ wrapper
	if strings.HasPrefix(lower, "regex:") {
		expr := strings.TrimPrefix(raw, raw[:6])
		re, err := regexp.Compile(expr)
		if err != nil {
			return nil, err
		}
		return RegexDomainRule{raw: raw, re: re}, nil
	}
	if strings.HasPrefix(raw, "/") && strings.HasSuffix(raw, "/") && len(raw) > 2 {
		expr := raw[1 : len(raw)-1]
		re, err := regexp.Compile(expr)
		if err != nil {
			return nil, err
		}
		return RegexDomainRule{raw: raw, re: re}, nil
	}

	// Check wildcard (*.domain or node-*.lan)
	if strings.Contains(raw, "*") || strings.Contains(raw, "?") {
		m := WildcardDomainRule{pattern: lower}
		if strings.HasPrefix(lower, "*.") {
			m.suffix = lower[1:] // ".internal"
		}
		return m, nil
	}

	// Exact domain match
	return ExactDomainRule(lower), nil
}

// EvaluateTarget evaluates whether the given target (IP or domain) is allowed.
// Returns (allowed, needDnsCheck):
// - If target is IP, returns (IsIpAllowed(ip), false)
// - If target is domain:
//   - If it matches domain whitelist, returns (true, true) [requires Anti-DNS-Rebinding IP verification before dial]
//   - If it does not match domain whitelist, returns (false, true) [may check if DNS resolves to allowed subnet]
func (e *AclEngine) EvaluateTarget(targetHost string) (allowed bool, needDnsCheck bool) {
	acl := e.current.Load()
	if acl.AllowAllTarget {
		return true, false
	}

	// 1. If target is pure IP
	if ip := net.ParseIP(targetHost); ip != nil {
		ok := e.IsIpAllowed(ip)
		if ok {
			e.allowedTargets.Add(1)
		} else {
			e.blockedTargets.Add(1)
		}
		return ok, false
	}

	// 2. Target is domain: check against domain whitelist
	for _, matcher := range acl.DomainRules {
		if matcher.Match(targetHost) {
			// Matched domain rule, but still requires DNS check for anti-rebinding defense!
			return true, true
		}
	}

	// 3. Domain didn't match domain rules directly, but might resolve to an allowed internal IP
	return false, true
}

// IsIpAllowed evaluates if target IP is allowed by subnet rules.
func (e *AclEngine) IsIpAllowed(ip net.IP) bool {
	acl := e.current.Load()
	if acl.AllowAllTarget {
		return true
	}
	for _, rule := range acl.SubnetRules {
		if rule.Match(ip) {
			return true
		}
	}
	return false
}

// RecordTargetResult updates metrics for target evaluation.
func (e *AclEngine) RecordTargetResult(allowed bool) {
	if allowed {
		e.allowedTargets.Add(1)
	} else {
		e.blockedTargets.Add(1)
	}
}

// IsClientAllowed checks if the incoming client connection source IP is permitted.
func (e *AclEngine) IsClientAllowed(remoteAddr string) bool {
	acl := e.current.Load()
	if acl.AllowAllClient {
		return true
	}

	host, _, err := net.SplitHostPort(remoteAddr)
	if err != nil {
		host = remoteAddr
	}
	ip := net.ParseIP(host)
	if ip == nil {
		e.blockedClients.Add(1)
		return false
	}

	for _, rule := range acl.ClientRules {
		if rule.Match(ip) {
			return true
		}
	}

	e.blockedClients.Add(1)
	return false
}

// GetStats returns snapshot of current ACL rules and metrics.
func (e *AclEngine) GetStats() AclStats {
	acl := e.current.Load()
	var subnets []string
	for _, s := range acl.SubnetRules {
		subnets = append(subnets, s.String())
	}
	var domains []string
	for _, d := range acl.DomainRules {
		domains = append(domains, d.String())
	}
	var clients []string
	for _, c := range acl.ClientRules {
		clients = append(clients, c.String())
	}

	return AclStats{
		Preset:         acl.Preset,
		AllowedTargets: e.allowedTargets.Load(),
		BlockedTargets: e.blockedTargets.Load(),
		BlockedClients: e.blockedClients.Load(),
		AuthFailures:   e.authFailures.Load(),
		SubnetRules:    subnets,
		DomainRules:    domains,
		ClientRules:    clients,
		AllowAllTarget: acl.AllowAllTarget,
		AllowAllClient: acl.AllowAllClient,
	}
}
