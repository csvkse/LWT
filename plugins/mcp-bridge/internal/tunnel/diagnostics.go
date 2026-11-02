package tunnel

import (
	"context"
	"crypto/tls"
	"fmt"
	"net"
	"net/url"
	"strings"
	"time"

	"golang.org/x/net/proxy"
)

// DiagnosticResult encapsulates end-to-end network egress diagnostic information.
type DiagnosticResult struct {
	TargetHost     string   `json:"targetHost"`
	TargetPort     int      `json:"targetPort"`
	DnsResolvedIPs []string `json:"dnsResolvedIPs"`
	DnsDurationMs  int64    `json:"dnsDurationMs"`
	TcpConnected   bool     `json:"tcpConnected"`
	TcpDurationMs  int64    `json:"tcpDurationMs"`
	TlsHandshakeOk bool     `json:"tlsHandshakeOk"`
	TlsDurationMs  int64    `json:"tlsDurationMs"`
	TlsVersion     string   `json:"tlsVersion,omitempty"`
	EffectiveProxy string   `json:"effectiveProxy,omitempty"`
	Error          string   `json:"error,omitempty"`
	Suggestion     string   `json:"suggestion,omitempty"`
}

// DiagnoseControlPlane performs comprehensive pre-flight connectivity probing to OpenAI control plane.
func DiagnoseControlPlane(ctx context.Context, controlPlaneBaseURL string, proxyStr string) *DiagnosticResult {
	res := &DiagnosticResult{
		TargetHost: "api.openai.com",
		TargetPort: 443,
	}

	if controlPlaneBaseURL != "" {
		if parsed, err := url.Parse(controlPlaneBaseURL); err == nil && parsed.Hostname() != "" {
			res.TargetHost = parsed.Hostname()
			if parsed.Port() != "" {
				var p int
				if _, err := fmt.Sscanf(parsed.Port(), "%d", &p); err == nil && p > 0 {
					res.TargetPort = p
				}
			}
		}
	}

	res.EffectiveProxy = strings.TrimSpace(proxyStr)

	// Step 1: DNS Resolution
	dnsStart := time.Now()
	resolver := &net.Resolver{}
	ips, err := resolver.LookupIP(ctx, "ip", res.TargetHost)
	res.DnsDurationMs = time.Since(dnsStart).Milliseconds()
	if err != nil {
		res.Error = fmt.Sprintf("DNS 解析失败: %v", err)
		res.Suggestion = "无法解析 api.openai.com 域名，请检查服务器的 DNS 配置 (/etc/resolv.conf) 或确保系统出站网络畅通。"
		return res
	}

	for _, ip := range ips {
		res.DnsResolvedIPs = append(res.DnsResolvedIPs, ip.String())
	}

	targetAddr := fmt.Sprintf("%s:%d", res.TargetHost, res.TargetPort)

	// Step 2: TCP Connection (Direct or via Proxy)
	tcpStart := time.Now()
	var rawConn net.Conn

	dialer := &net.Dialer{Timeout: 6 * time.Second}

	if res.EffectiveProxy != "" {
		proxyURL, parseErr := url.Parse(res.EffectiveProxy)
		if parseErr != nil {
			res.Error = fmt.Sprintf("代理 URL 解析失败: %v", parseErr)
			res.Suggestion = "请检查填写的代理地址格式，支持 http://host:port 或 socks5://host:port。"
			return res
		}

		if strings.HasPrefix(proxyURL.Scheme, "socks5") {
			socksDialer, sErr := proxy.FromURL(proxyURL, dialer)
			if sErr != nil {
				res.Error = fmt.Sprintf("构造 SOCKS5 拨号器失败: %v", sErr)
				res.Suggestion = "请检查 SOCKS5 代理地址是否正确及本地网络是否可达。"
				return res
			}
			rawConn, err = socksDialer.Dial("tcp", targetAddr)
		} else {
			// HTTP / HTTPS Connect proxy
			rawConn, err = dialViaHTTPProxy(ctx, proxyURL, targetAddr, 6*time.Second)
		}
	} else {
		// Direct TCP dial
		rawConn, err = dialer.DialContext(ctx, "tcp", targetAddr)
	}

	res.TcpDurationMs = time.Since(tcpStart).Milliseconds()
	if err != nil {
		res.Error = fmt.Sprintf("TCP 握手连接失败: %v", err)
		if res.EffectiveProxy != "" {
			res.Suggestion = fmt.Sprintf("无法通过配置的代理 [%s] 连接到 %s，请检查代理服务是否正常运行并允许 CONNECT 443 出站。", res.EffectiveProxy, targetAddr)
		} else {
			res.Suggestion = fmt.Sprintf("连接目标 %s 超时或被拒绝。当前服务器所在网络无法直连访问 OpenAI，建议在控制台配置出站代理 (如 http://代理IP:端口 或 socks5://代理IP:端口)。", targetAddr)
		}
		return res
	}
	defer rawConn.Close()
	res.TcpConnected = true

	// Step 3: TLS Handshake
	tlsStart := time.Now()
	tlsConfig := &tls.Config{
		ServerName: res.TargetHost,
		MinVersion: tls.VersionTLS12,
	}

	tlsConn := tls.Client(rawConn, tlsConfig)
	handshakeCtx, cancel := context.WithTimeout(ctx, 6*time.Second)
	defer cancel()

	if err := tlsConn.HandshakeContext(handshakeCtx); err != nil {
		res.TlsDurationMs = time.Since(tlsStart).Milliseconds()
		res.Error = fmt.Sprintf("TLS 安全握手失败: %v", err)
		res.Suggestion = "TLS 握手被重置或证书不匹配，这通常意味着网络路径上有安全网关劫持或中间人代理截断了 TLS 流量。"
		return res
	}
	res.TlsDurationMs = time.Since(tlsStart).Milliseconds()
	res.TlsHandshakeOk = true

	state := tlsConn.ConnectionState()
	switch state.Version {
	case tls.VersionTLS13:
		res.TlsVersion = "TLS 1.3"
	case tls.VersionTLS12:
		res.TlsVersion = "TLS 1.2"
	default:
		res.TlsVersion = fmt.Sprintf("0x%04x", state.Version)
	}

	res.Suggestion = fmt.Sprintf("网络通路正常就绪！DNS 耗时 %dms，TCP 耗时 %dms，TLS (%s) 耗时 %dms。",
		res.DnsDurationMs, res.TcpDurationMs, res.TlsVersion, res.TlsDurationMs)
	return res
}

func dialViaHTTPProxy(ctx context.Context, proxyURL *url.URL, targetAddr string, timeout time.Duration) (net.Conn, error) {
	d := &net.Dialer{Timeout: timeout}
	pConn, err := d.DialContext(ctx, "tcp", proxyURL.Host)
	if err != nil {
		return nil, fmt.Errorf("connect to HTTP proxy %s: %w", proxyURL.Host, err)
	}

	req := fmt.Sprintf("CONNECT %s HTTP/1.1\r\nHost: %s\r\n", targetAddr, targetAddr)
	if proxyURL.User != nil {
		req += fmt.Sprintf("Proxy-Authorization: Basic %s\r\n", proxyURL.User.String())
	}
	req += "\r\n"

	if err := pConn.SetDeadline(time.Now().Add(timeout)); err != nil {
		_ = pConn.Close()
		return nil, err
	}
	if _, err := pConn.Write([]byte(req)); err != nil {
		_ = pConn.Close()
		return nil, fmt.Errorf("write CONNECT to proxy: %w", err)
	}

	buf := make([]byte, 1024)
	n, err := pConn.Read(buf)
	if err != nil {
		_ = pConn.Close()
		return nil, fmt.Errorf("read proxy CONNECT response: %w", err)
	}
	_ = pConn.SetDeadline(time.Time{})

	respStr := string(buf[:n])
	if !strings.HasPrefix(respStr, "HTTP/1.1 200") && !strings.HasPrefix(respStr, "HTTP/1.0 200") {
		_ = pConn.Close()
		return nil, fmt.Errorf("proxy rejected CONNECT request: %s", strings.TrimSpace(respStr))
	}

	return pConn, nil
}
