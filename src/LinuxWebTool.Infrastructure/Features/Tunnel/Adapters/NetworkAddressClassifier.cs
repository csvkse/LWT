using System.Net;
using System.Net.Sockets;
namespace LinuxWebTool.Infrastructure.Features.Tunnel.Adapters;

/// <summary>
/// 网络地址与私有网络边界判定分类器
/// 严格实现 RFC 1122、RFC 1918、RFC 6598、RFC 4193 等私网边界识别
/// </summary>
public static class NetworkAddressClassifier
{
    public static bool IsPrivateNetworkUrl(string urlString)
    {
        if (string.IsNullOrWhiteSpace(urlString)) return false;
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return false;

        var host = uri.DnsSafeHost.ToLowerInvariant();

        // 1. 本机环回地址 (RFC 1122 / RFC 4291)
        if (host is "localhost" or "127.0.0.1" or "::1") return true;

        // 2. 本地局域网单标签主机名或内网专用后缀 (Docker / mDNS / 家用路由)
        // 例如: http://alist:5211, http://synology.local, http://openwrt.lan, http://nas.home.arpa
        if (!host.Contains('.')) return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. IP 地址段检查
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                // RFC 1918 10.0.0.0/8
                if (bytes[0] == 10) return true;
                // RFC 1918 172.16.0.0/12 (172.16.0.0 ~ 172.31.255.255)
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                // RFC 1918 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                // RFC 6598 CGNAT / Tailscale 虚拟网段 (100.64.0.0/10)
                if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
                // RFC 3927 链路本地 (169.254.0.0/16)
                if (bytes[0] == 169 && bytes[1] == 254) return true;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // RFC 4193 唯一本地地址 ULA (fc00::/7 包括 fd00::/8)
                if (ip.IsIPv6UniqueLocal) return true;
                // RFC 4291 链路本地 (fe80::/10)
                if (ip.IsIPv6LinkLocal) return true;
            }
        }

        return false;
    }
}
