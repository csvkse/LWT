namespace LinuxWebTool.WebHost.Features.Plugins.Ingress;

/// <summary>
/// 宿主核心系统保留路由守卫（Host Reserved Route Guard）。
/// 绝对保护宿主控制面（Control Plane）核心管理 API、前端 SPA 路由、健康探测与终端通信管道。
/// 严禁任何插件微服务通过 Ingress 声明直接覆盖或劫持保留路径。
/// </summary>
public static class HostReservedRoutes
{
    private static readonly string[] ReservedExact =
    [
        "/health",
        "/favicon.ico",
        "/robots.txt"
    ];

    private static readonly string[] ReservedPrefixes =
    [
        "/api/",
        "/app/",
        "/assets/",
        "/terminal"
    ];

    /// <summary>
    /// 检查指定请求路径是否命中了宿主系统保留路由。
    /// </summary>
    public static bool IsReserved(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Trim();

        foreach (var exact in ReservedExact)
        {
            if (string.Equals(normalized, exact, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (var prefix in ReservedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (prefix.EndsWith('/') || normalized.Length == prefix.Length || normalized[prefix.Length] == '/')
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 准入检查：检验插件声明的匹配路径是否侵犯了宿主核心系统保留路由。
    /// </summary>
    public static (bool IsAllowed, string? ViolationReason) ValidateDeclaredPath(string declaredPath)
    {
        if (string.IsNullOrWhiteSpace(declaredPath))
            return (false, "Ingress 路径不能为空");

        var normalized = declaredPath.Trim();

        if (string.Equals(normalized, "/", StringComparison.Ordinal))
            return (false, "严禁将根路径 '/' 声明为全局 Ingress 路径，避免全站流量劫持");

        foreach (var exact in ReservedExact)
        {
            if (string.Equals(normalized, exact, StringComparison.OrdinalIgnoreCase))
                return (false, $"路径 '{declaredPath}' 为宿主系统绝对核心保留路由，严禁插件声明覆盖");
        }

        foreach (var prefix in ReservedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"路径 '{declaredPath}' 侵犯了宿主系统核心保留前缀 '{prefix}'，已被准入控制器硬阻断");
            }
        }

        return (true, null);
    }
}
