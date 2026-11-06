using LinuxWebTool.Application.Features.Plugins.Auth;
using LinuxWebTool.Application.Features.Security;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.WebHost.Features.Plugins.Auth;

/// <summary>
/// 扩展中心 HTTP 网关层鉴权决断适配器。
/// 负责从 HttpContext 提取认证信息（管理员 JWT、Cookie 会话、API Key），
/// 并委托领域层策略决断器 PluginAuthRuleEvaluator 进行策略裁决。
/// </summary>
public sealed class PluginAuthGatewayEvaluator(
    ApiKeyService apiKeyService,
    ILogger<PluginAuthGatewayEvaluator> logger)
{
    public async ValueTask<PluginAuthDecision> EvaluateAsync(
        HttpContext context,
        PluginManifest manifest)
    {
        var fullPath = context.Request.Path.Value ?? string.Empty;
        var subPath = context.Request.RouteValues["catch-all"]?.ToString() ?? string.Empty;

        // 1. 检查管理员认证态（Bearer JWT 或已由 Cookie/中间件认证的用户）
        var isAdmin = context.User?.Identity?.IsAuthenticated == true &&
                      context.User.FindFirst("TokenType")?.Value != "ApiKey";

        string? adminName = isAdmin ? context.User?.Identity?.Name ?? "admin" : null;

        // Cookie 和 query token 由 JWT 认证处理器验证；原始字符串不能证明身份。

        // 2. 检查 API Key 认证态（优先读取中间件注入的 CurrentApiKey）
        ApiKeyEntity? apiKey = null;
        if (context.Items.TryGetValue("CurrentApiKey", out var keyObj) && keyObj is ApiKeyEntity cachedKey)
        {
            apiKey = cachedKey;
        }
        else
        {
            // 尝试提取请求中的 API Key 进行即席校验
            var rawKey = ExtractKey(context);
            if (!string.IsNullOrWhiteSpace(rawKey))
            {
                apiKey = await apiKeyService.ValidateAsync(rawKey);
            }
        }

        var hasValidKey = apiKey != null;
        var keyName = apiKey?.Name;
        var allowPlugins = apiKey?.AllowPlugins ?? false;
        var allowedPaths = apiKey?.AllowedPaths;

        var decision = PluginAuthRuleEvaluator.Evaluate(
            manifest,
            subPath,
            fullPath,
            isAdmin,
            adminName,
            hasValidKey,
            keyName,
            allowPlugins,
            allowedPaths);

        if (!decision.IsAllowed)
        {
            logger.LogWarning("[PluginGateway] 拒绝访问微服务 '{PluginId}' 端点: {Path}，原因: {Reason}",
                manifest.Id, fullPath, decision.FailureReason);
        }

        return decision;
    }

    private static string? ExtractKey(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var headerKey) && !string.IsNullOrWhiteSpace(headerKey))
        {
            return headerKey.ToString().Trim();
        }

        var authHeader = context.Request.Headers.Authorization.ToString();
        if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authHeader["Bearer ".Length..].Trim();
            if (token.StartsWith("lwt_", StringComparison.OrdinalIgnoreCase))
            {
                return token;
            }
        }

        if (context.Request.Query.TryGetValue("apiKey", out var queryKey) && !string.IsNullOrWhiteSpace(queryKey))
        {
            return queryKey.ToString().Trim();
        }

        if (context.Request.Query.TryGetValue("key", out var fallbackKey) && !string.IsNullOrWhiteSpace(fallbackKey))
        {
            return fallbackKey.ToString().Trim();
        }

        return null;
    }
}
