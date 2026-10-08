using System.Net;
using LinuxWebTool.Infrastructure.Features.Tunnel.Adapters;

namespace LinuxWebTool.WebHost.Shared.Extensions;

public static class HttpContextExtensions
{
    /// <summary>
    /// 取客户端真实 IP。
    /// 仅当直接网络连接来源于本机环回 (Loopback) 或受信任私网反代 (RFC 1918/ULA) 时才信任 X-Real-IP 反代头。
    /// 若直面不可信公网客户端，强制使用 RemoteIpAddress，彻底杜绝 IP 伪造与针对性封锁 DoS。
    /// </summary>
    public static string GetClientIp(this HttpContext context)
    {
        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null || NetworkAddressClassifier.IsPrivateOrLoopbackAddress(remoteIp))
        {
            var forwarded = context.Request.Headers["X-Real-IP"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
            {
                return forwarded.Trim();
            }
        }
        return remoteIp?.ToString() ?? string.Empty;
    }
}
