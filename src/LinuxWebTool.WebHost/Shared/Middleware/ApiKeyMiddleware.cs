using System.Security.Claims;
using LinuxWebTool.WebHost.Composition;
using Microsoft.Extensions.Logging;
namespace LinuxWebTool.WebHost.Shared.Middleware;

/// <summary>
/// API Key 认证与细粒度权限矩阵检查中间件
/// 支持与既有管理员 JWT 协同工作（双轨认证模型）
/// </summary>
public sealed class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ApiKeyService keyService)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // 1. 公共静态资源与非鉴权端点直接放行
        if (IsPublicRoute(path))
        {
            await next(context);
            return;
        }

        // 2. 若用户已通过管理员 JWT 登录，具备全量管理权限，直接放行
        if (context.User?.Identity?.IsAuthenticated == true
            && context.User.FindFirst("TokenType")?.Value != "ApiKey")
        {
            await next(context);
            return;
        }

        // 3. 提取 API Key
        var rawKey = ExtractKey(context);

        // 如果既非 API 也非 MCP 路由，放行给后续管线（如 SPA fallback）
        var isApi = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);
        var isMcp = path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase);
        if (!isApi && !isMcp)
        {
            await next(context);
            return;
        }

        // 4. API / MCP 请求缺少凭据处理
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            if (isMcp)
            {
                logger.LogDebug("MCP 请求未提供认证凭据，拒绝: {Path}", path);
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(
                    new MessageResponse("需要身份认证：请提供有效的 API Key 或登录管理员"),
                    AppJsonSerializerContext.Default.MessageResponse);
                return;
            }

            // 普通 API 请求未携带 API Key 时放行给后续标准管线（由 [Authorize] 或 Fallback 404 处理）
            await next(context);
            return;
        }

        // 5. 校验 Key 是否合法、启用且未过期
        var entity = await keyService.ValidateAsync(rawKey);
        if (entity is null)
        {
            logger.LogWarning("无效或过期的 API Key，拒绝请求: {Path}", path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(
                new MessageResponse("无效、已禁用或已过期的 API Key"),
                AppJsonSerializerContext.Default.MessageResponse);
            return;
        }

        // 6. 通道权限检查 (Channel Checking)
        if (isMcp && !entity.AllowMcp)
        {
            logger.LogWarning("API Key '{Name}' 未启用 MCP 通道访问: {Path}", entity.Name, path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(
                new MessageResponse("该 API Key 未被授予访问 MCP 协议通道权限"),
                AppJsonSerializerContext.Default.MessageResponse);
            return;
        }

        if (isApi && !entity.AllowApi)
        {
            logger.LogWarning("API Key '{Name}' 未启用 REST API 通道访问: {Path}", entity.Name, path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(
                new MessageResponse("该 API Key 未被授予访问 REST API 通道权限"),
                AppJsonSerializerContext.Default.MessageResponse);
            return;
        }

        // 7. 模块权限检查 (Module Scope Checking)
        if (isApi && !CheckModulePermission(path, entity))
        {
            logger.LogWarning("API Key '{Name}' 无权访问模块: {Path}", entity.Name, path);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(
                new MessageResponse("该 API Key 对所请求的业务模块无权访问"),
                AppJsonSerializerContext.Default.MessageResponse);
            return;
        }

        // 8. 注入身份上下文
        context.Items["CurrentApiKey"] = entity;
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, entity.Id),
            new Claim(ClaimTypes.Name, entity.Name),
            new Claim("TokenType", "ApiKey")
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "ApiKey"));

        await next(context);

        // 异步记录最后使用时间
        keyService.TouchLastUsed(entity.Id);
    }

    private static bool CheckModulePermission(string path, ApiKeyEntity entity)
    {
        // 管理类专属接口严禁普通 API Key 访问（需管理员会话）
        if (path.StartsWith("/api/Auth/ChangeCredential", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/ApiKeys", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/FrpTunnel", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (path.StartsWith("/api/Commands", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/Terminal", StringComparison.OrdinalIgnoreCase))
        {
            return entity.AllowTerminal;
        }

        if (path.StartsWith("/api/Schedules", StringComparison.OrdinalIgnoreCase))
        {
            return entity.AllowSchedules;
        }

        if (path.StartsWith("/api/Files", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/SmbMounts", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/WebDavMounts", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/RcloneMounts", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/api/MountTasks", StringComparison.OrdinalIgnoreCase))
        {
            return entity.AllowFiles;
        }

        if (path.StartsWith("/api/Transcode", StringComparison.OrdinalIgnoreCase))
        {
            return entity.AllowTranscode;
        }

        if (path.StartsWith("/api/Gateway", StringComparison.OrdinalIgnoreCase))
        {
            return entity.AllowGateway;
        }

        // 默认如 Overview、SystemStatus、History、Logs 等查询接口只要拥有 Api 权限即可
        return true;
    }

    private static string? ExtractKey(HttpContext context)
    {
        // 1. Header: X-Api-Key
        if (context.Request.Headers.TryGetValue("X-Api-Key", out var headerKey)
            && !string.IsNullOrWhiteSpace(headerKey))
        {
            return headerKey.ToString().Trim();
        }

        // 2. Header: Authorization: Bearer lwt_...
        if (context.Request.Headers.TryGetValue("Authorization", out var authHeader)
            && !string.IsNullOrWhiteSpace(authHeader))
        {
            var authStr = authHeader.ToString().Trim();
            if (authStr.StartsWith("Bearer lwt_", StringComparison.OrdinalIgnoreCase))
            {
                return authStr["Bearer ".Length..].Trim();
            }
        }

        // 3. Query: ?apiKey= 或 ?key=
        if (context.Request.Query.TryGetValue("apiKey", out var queryKey)
            && !string.IsNullOrWhiteSpace(queryKey))
        {
            return queryKey.ToString().Trim();
        }

        if (context.Request.Query.TryGetValue("key", out var fallbackQueryKey)
            && !string.IsNullOrWhiteSpace(fallbackQueryKey))
        {
            return fallbackQueryKey.ToString().Trim();
        }

        return null;
    }

    private static bool IsPublicRoute(string path)
    {
        if (path == "/" || path == "/health") return true;
        if (path.StartsWith("/app", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/api/Auth/Login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals("/api/Auth/Check", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // API 与 MCP 路由不受静态扩展名放行规则影响，必须经过严格认证判定
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 静态文件扩展名放行
        var staticExtensions = new[] { ".html", ".js", ".css", ".ico", ".svg", ".png", ".woff", ".woff2", ".map" };
        foreach (var ext in staticExtensions)
        {
            if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
