package main

import (
	"encoding/base64"
	"strings"
	"testing"
)

func TestUriParser_Vless(t *testing.T) {
	link := "vless://01234567-89ab-cdef-0123-456789abcdef@node.example.com:8443?security=tls&sni=node.example.com#Tokyo-01"
	node, err := ParseUpstreamLink(link)
	if err != nil {
		t.Fatalf("ParseUpstreamLink failed: %v", err)
	}

	if node.Type != "vless" {
		t.Errorf("expected type vless, got %s", node.Type)
	}
	if node.Server != "node.example.com" {
		t.Errorf("expected server node.example.com, got %s", node.Server)
	}
	if node.Port != 8443 {
		t.Errorf("expected port 8443, got %d", node.Port)
	}
	if node.UUID != "01234567-89ab-cdef-0123-456789abcdef" {
		t.Errorf("expected UUID matching, got %s", node.UUID)
	}
	if node.SNI != "node.example.com" {
		t.Errorf("expected SNI node.example.com, got %s", node.SNI)
	}
	if node.Name != "Tokyo-01" {
		t.Errorf("expected name Tokyo-01, got %s", node.Name)
	}
}

func TestUriParser_Vless_Vision(t *testing.T) {
	link := "vless://00000000-0000-0000-0000-000000000000@node.example.com:11443?encryption=none&flow=xtls-rprx-vision&security=tls&sni=node.example.com&fp=chrome&type=tcp&headerType=none&host=node.example.com#Vision-Node"
	node, err := ParseUpstreamLink(link)
	if err != nil {
		t.Fatalf("ParseUpstreamLink failed: %v", err)
	}

	if node.Flow != "xtls-rprx-vision" {
		t.Errorf("expected flow xtls-rprx-vision, got %s", node.Flow)
	}
	if node.Fingerprint != "chrome" {
		t.Errorf("expected fp chrome, got %s", node.Fingerprint)
	}
	if node.Transport != "tcp" {
		t.Errorf("expected transport tcp, got %s", node.Transport)
	}
	if node.Server != "node.example.com" {
		t.Errorf("expected server node.example.com, got %s", node.Server)
	}
	if node.SNI != "node.example.com" {
		t.Errorf("expected sni node.example.com, got %s", node.SNI)
	}
}

func TestUriParser_Trojan(t *testing.T) {
	link := "trojan://test-trojan-pwd-123@trojan.example.com:443?sni=trojan.example.com&allowInsecure=1#HK-Trojan"
	node, err := ParseUpstreamLink(link)
	if err != nil {
		t.Fatalf("ParseUpstreamLink failed: %v", err)
	}

	if node.Type != "trojan" {
		t.Errorf("expected type trojan, got %s", node.Type)
	}
	if node.Server != "trojan.example.com" {
		t.Errorf("expected server trojan.example.com, got %s", node.Server)
	}
	if node.Port != 443 {
		t.Errorf("expected port 443, got %d", node.Port)
	}
	if node.Password != "test-trojan-pwd-123" {
		t.Errorf("expected password matching, got %s", node.Password)
	}
	if !node.SkipTLSVerify {
		t.Errorf("expected SkipTLSVerify true because allowInsecure=1")
	}
	if node.Name != "HK-Trojan" {
		t.Errorf("expected name HK-Trojan, got %s", node.Name)
	}
}

func TestUriParser_Shadowsocks(t *testing.T) {
	// SIP002 Format 1: method:password base64
	userBase64 := base64.StdEncoding.EncodeToString([]byte("aes-256-gcm:my-ss-secret-pwd"))
	link1 := "ss://" + userBase64 + "@ss.example.com:8388#US-SS-01"
	node1, err := ParseUpstreamLink(link1)
	if err != nil {
		t.Fatalf("ParseUpstreamLink link1 failed: %v", err)
	}
	if node1.Type != "shadowsocks" || node1.Method != "aes-256-gcm" || node1.Password != "my-ss-secret-pwd" {
		t.Errorf("unexpected node1: %+v", node1)
	}
	if node1.Server != "ss.example.com" || node1.Port != 8388 || node1.Name != "US-SS-01" {
		t.Errorf("unexpected node1 host/port: %+v", node1)
	}

	// SIP002 Format 2: full base64
	fullBase64 := base64.StdEncoding.EncodeToString([]byte("chacha20-ietf-poly1305:pwd123@1.2.3.4:9000"))
	link2 := "ss://" + fullBase64 + "#JP-SS-02"
	node2, err := ParseUpstreamLink(link2)
	if err != nil {
		t.Fatalf("ParseUpstreamLink link2 failed: %v", err)
	}
	if node2.Method != "chacha20-ietf-poly1305" || node2.Password != "pwd123" || node2.Server != "1.2.3.4" || node2.Port != 9000 {
		t.Errorf("unexpected node2: %+v", node2)
	}
}

func TestUriParser_Socks5_Http(t *testing.T) {
	socksLink := "socks5://user1:pass1@192.168.1.100:1080#Internal-Socks5"
	nodeSocks, err := ParseUpstreamLink(socksLink)
	if err != nil {
		t.Fatalf("Parse socks failed: %v", err)
	}
	if nodeSocks.Type != "socks5" || nodeSocks.Username != "user1" || nodeSocks.Password != "pass1" || nodeSocks.Port != 1080 {
		t.Errorf("unexpected nodeSocks: %+v", nodeSocks)
	}

	httpLink := "http://admin:admin888@proxy.company.internal:8080#Corp-Http"
	nodeHttp, err := ParseUpstreamLink(httpLink)
	if err != nil {
		t.Fatalf("Parse http failed: %v", err)
	}
	if nodeHttp.Type != "http" || nodeHttp.Username != "admin" || nodeHttp.Password != "admin888" || nodeHttp.Port != 8080 {
		t.Errorf("unexpected nodeHttp: %+v", nodeHttp)
	}
}

func TestUriParser_Vmess(t *testing.T) {
	vmessJson := `{"v":"2","ps":"SG-Vmess","add":"sg.example.com","port":"443","id":"98765432-1234-abcd-ef01-234567890abc","aid":"0","net":"ws","type":"none","host":"sg.example.com","path":"/vmess","tls":"tls"}`
	vmessLink := "vmess://" + base64.StdEncoding.EncodeToString([]byte(vmessJson))

	node, err := ParseUpstreamLink(vmessLink)
	if err != nil {
		t.Fatalf("Parse vmess failed: %v", err)
	}
	if node.Type != "vmess" || node.Server != "sg.example.com" || node.Port != 443 || node.Name != "SG-Vmess" {
		t.Errorf("unexpected vmess node: %+v", node)
	}
	if node.UUID != "98765432-1234-abcd-ef01-234567890abc" {
		t.Errorf("unexpected uuid: %s", node.UUID)
	}
}

func TestUriParser_Batch(t *testing.T) {
	rawText := `
# 这是注释行
// 这是另一行注释
vless://01234567-89ab-cdef-0123-456789abcdef@node1.example.com:443?sni=node1.example.com#Node1

trojan://pwd1@node2.example.com:443#Node2
invalid://malformed-url
`
	nodes, errs := ParseUpstreamLinks(rawText)
	if len(nodes) != 2 {
		t.Errorf("expected 2 parsed nodes, got %d", len(nodes))
	}
	if len(errs) != 1 {
		t.Errorf("expected 1 error for invalid scheme, got %d", len(errs))
	}
	if !strings.Contains(errs[0], "invalid") {
		t.Errorf("expected invalid error, got %s", errs[0])
	}
}
