package main

import (
	"net"
	"strings"
)

// ACLManager 负责客户端入站 IP 与网段白名单判定
type ACLManager struct {
	allowedSubnets []*net.IPNet
	allowedIPs     []net.IP
	allowAll       bool
}

// NewACLManager 根据允许的 CIDR 列表构造 ACL 检查器
func NewACLManager(subnets []string) *ACLManager {
	mgr := &ACLManager{
		allowedSubnets: make([]*net.IPNet, 0),
		allowedIPs:     make([]net.IP, 0),
	}

	for _, s := range subnets {
		s = strings.TrimSpace(s)
		if s == "" {
			continue
		}
		if s == "*" {
			mgr.allowAll = true
			return mgr
		}

		if strings.Contains(s, "/") {
			_, ipNet, err := net.ParseCIDR(s)
			if err == nil && ipNet != nil {
				mgr.allowedSubnets = append(mgr.allowedSubnets, ipNet)
			}
		} else {
			ip := net.ParseIP(s)
			if ip != nil {
				mgr.allowedIPs = append(mgr.allowedIPs, ip)
			}
		}
	}

	return mgr
}

// IsAllowed 检查给定的远程网络套接字地址是否放行
func (m *ACLManager) IsAllowed(remoteAddr net.Addr) bool {
	if m.allowAll {
		return true
	}
	if len(m.allowedSubnets) == 0 && len(m.allowedIPs) == 0 {
		return false
	}

	ip := ExtractIP(remoteAddr)
	if ip == nil {
		return false
	}

	// 兼容 IPv4-mapped IPv6 格式转换
	if ip4 := ip.To4(); ip4 != nil {
		ip = ip4
	}

	for _, single := range m.allowedIPs {
		if ip.Equal(single) {
			return true
		}
	}

	for _, subnet := range m.allowedSubnets {
		if subnet.Contains(ip) {
			return true
		}
	}

	return false
}

// ExtractIP 从 net.Addr 中解析纯净的 net.IP
func ExtractIP(addr net.Addr) net.IP {
	if addr == nil {
		return nil
	}
	host, _, err := net.SplitHostPort(addr.String())
	if err != nil {
		host = addr.String()
	}
	return net.ParseIP(host)
}
