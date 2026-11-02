using LinuxWebTool.Application.Features.Security;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Application.Features.Plugins.Auth;

/// <summary>
/// 扩展中心长时 HTTP 微服务鉴权决策器（纯领域层，AOT 兼容，零 ASP.NET 依赖）。
/// 支持四大策略：
/// 1. "inherit"（默认）：要求管理员登录态或具有 AllowPlugins=true 的有效 API Key；
/// 2. "hybrid"：白名单路径 (PublicPaths) 放行匿名，其余端点强制要求凭据；
/// 3. "apikey"：专供第三方系统或脚本集成，仅允许有效 API Key 访问；
/// 4. "anonymous"：插件显式开启的完全公开端点（如全公开轻量数据抓取）。
/// </summary>
public static class PluginAuthRuleEvaluator
{
    public static PluginAuthDecision Evaluate(
        PluginManifest manifest,
        string requestSubPath,
        string fullRequestPath,
        bool isAdminAuthenticated,
        string? adminUserName,
        bool hasValidApiKey,
        string? apiKeyName,
        bool apiKeyAllowPlugins,
        string? apiKeyAllowedPaths)
    {
        var network = manifest.Network;
        var mode = string.IsNullOrWhiteSpace(network?.AuthMode) ? "inherit" : network.AuthMode.Trim().ToLowerInvariant();

        // 策略 1: 完全匿名策略 (anonymous)
        if (mode == "anonymous")
        {
            return PluginAuthDecision.Allowed("anonymous", "Anonymous");
        }

        // 策略 2: 混合路径白名单策略 (hybrid) 或配置了 PublicPaths
        if ((mode == "hybrid" || (network?.PublicPaths != null && network.PublicPaths.Count > 0)) &&
            IsPublicPath(network?.PublicPaths, requestSubPath))
        {
            return PluginAuthDecision.Allowed("anonymous", "PublicPath");
        }

        // 策略 3: API Key 访问核验
        if (hasValidApiKey)
        {
            if (!apiKeyAllowPlugins)
            {
                return PluginAuthDecision.Denied(403, "该 API Key 未被授予访问扩展中心微服务的权限");
            }

            if (!string.IsNullOrWhiteSpace(apiKeyAllowedPaths) &&
                !ApiKeyPermissionMatrix.IsPathPermitted(apiKeyAllowedPaths, fullRequestPath))
            {
                return PluginAuthDecision.Denied(403, "API Key 路径作用域越界");
            }

            return PluginAuthDecision.Allowed(apiKeyName ?? "ApiKeyUser", "ApiKey");
        }

        // 策略 4: 管理员会话核验
        if (isAdminAuthenticated)
        {
            return PluginAuthDecision.Allowed(adminUserName ?? "admin", "AdminSession");
        }

        // 凭证缺失：拒绝访问并下发认证质询
        var challenge = mode == "apikey"
            ? "ApiKey realm=\"LWT Plugin Gateway\""
            : "Bearer realm=\"LWT Plugin Gateway\"";

        var reason = mode == "apikey"
            ? "该微服务被配置为仅允许 API Key 访问，请提供有效的 X-Api-Key 请求头或 query 参数"
            : "微服务访问受限：需要身份认证，请登录管理员账号或提供已授权的 API Key";

        return PluginAuthDecision.Denied(401, reason, challenge);
    }

    /// <summary>
    /// 匹配微服务公开子路径白名单（支持精确匹配与前缀通配）
    /// </summary>
    public static bool IsPublicPath(IReadOnlyList<string>? publicPaths, string subPath)
    {
        if (publicPaths == null || publicPaths.Count == 0) return false;

        var normalizedSub = ("/" + subPath.Trim().Trim('/')).TrimEnd('/');
        if (string.IsNullOrEmpty(normalizedSub)) normalizedSub = "/";

        foreach (var raw in publicPaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var path = ("/" + raw.Trim().Trim('/')).TrimEnd('/');
            if (string.IsNullOrEmpty(path)) path = "/";

            if (path == "*" || string.Equals(path, normalizedSub, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (path.EndsWith("/*", StringComparison.Ordinal))
            {
                var prefix = path[..^2].TrimEnd('/');
                if (normalizedSub.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (normalizedSub.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
