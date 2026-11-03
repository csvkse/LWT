using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.WebHost.Composition;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.WebHost.Features.Plugins.Ingress;

/// <summary>
/// 平台级通用声明式 Ingress 中间件。
/// 业务中立，由各插件微服务清单中的 Ingress 规则动态驱动。
/// 提供核心系统保留路由守卫 (HostReservedRoutes)、双轨制命名空间 (Standard Scoped vs Stealth Camouflage)、
/// 多维消歧仲裁策略 (Precedence Strategy)、CIDR 客户端白名单守卫、统一鉴权、主动探测伪装回落 (Web Fallback)、
/// 以及全双工流式对拷 (WebSocket / HTTP Upgrade Raw Duplex Stream) 能力。
/// </summary>
public sealed class PluginIngressMiddleware(
    RequestDelegate next,
    ILogger<PluginIngressMiddleware> logger)
{
    private sealed record IngressCandidate(
        PluginInstanceDto Plugin,
        PluginIngressRoute Route,
        string MatchedPath,
        int Score);

    public async Task InvokeAsync(
        HttpContext context,
        IPluginManager pluginManager,
        IDuplexStreamForwarder forwarder,
        IHttpClientFactory httpClientFactory)
    {
        var requestPath = context.Request.Path.Value ?? "/";

        // 1. 核心系统保留路由快速短路（绝对保护控制面 REST API、前端 SPA 静态资源与终端）
        if (HostReservedRoutes.IsReserved(requestPath))
        {
            await next(context);
            return;
        }

        var ct = context.RequestAborted;
        var plugins = await pluginManager.GetAllAsync(profileFilter: null, ct);

        // 2. 收集所有匹配当前请求的有效候选路由并执行多维消歧仲裁评分
        var candidates = new List<IngressCandidate>();

        foreach (var plugin in plugins)
        {
            if (plugin.State != PluginState.Running)
                continue;

            var ingress = plugin.Manifest.Ingress;
            if (ingress == null || !ingress.Enabled || ingress.Routes.Count == 0)
                continue;

            var isStealth = string.Equals(ingress.Scope, "stealth", StringComparison.OrdinalIgnoreCase);

            foreach (var route in ingress.Routes)
            {
                var effectivePaths = ResolveEffectivePaths(plugin.Id, route.Match, isStealth, logger);
                if (effectivePaths.Count == 0) continue;

                foreach (var (effectivePath, isExact) in effectivePaths)
                {
                    if (!IsPathMatch(effectivePath, isExact ? "Exact" : "Prefix", requestPath))
                        continue;

                    if (!IsHeaderMatch(context, route.Match.Headers))
                        continue;

                    // 计算多维消歧评分 (Precedence Score)
                    int score = CalculatePrecedenceScore(context, route, effectivePath, isExact);
                    candidates.Add(new IngressCandidate(plugin, route, effectivePath, score));
                }
            }
        }

        if (candidates.Count == 0)
        {
            // 未匹配任何插件 Ingress 路由，继续主宿主管线
            await next(context);
            return;
        }

        // 3. 策略仲裁：选取最高评分的胜出路由（确定性消除竞态）
        var winner = candidates.OrderByDescending(c => c.Score)
                               .ThenBy(c => c.Plugin.Id, StringComparer.Ordinal)
                               .First();

        var winningPlugin = winner.Plugin;
        var winningRoute = winner.Route;

        // 4. 执行通用安全与条件守卫校验（IP CIDR 白名单 + 统一鉴权）
        var (isAllowed, reason) = EvaluateSecurity(context, winningPlugin, winningRoute.Security);
        if (!isAllowed)
        {
            logger.LogWarning("[PluginIngress] 路由 '{RouteName}' 鉴权未通过: {Reason} (Path: {Path})",
                winningRoute.Name, reason, requestPath);

            if (string.Equals(winningRoute.Fallback.OnUnauthorized, "passthrough_to_default_spa", StringComparison.OrdinalIgnoreCase))
            {
                // 核心伪装回落机制：将未授权的主动探测流量静默流转至宿主默认 SPA 前端管线，完全隐匿服务特征
                await ServeSpaFallbackAsync(context, next);
                return;
            }

            if (string.Equals(winningRoute.Fallback.OnUnauthorized, "status_code_404", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            // 默认 JSON 401 拒绝
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(
                new MessageResponse($"微服务 Ingress 访问拒绝: {reason}"),
                AppJsonSerializerContext.Default.MessageResponse);
            return;
        }

        // 5. 验证微服务目标后端端口
        var port = winningPlugin.AllocatedPort ?? 0;
        if (port <= 0)
        {
            if (string.Equals(winningRoute.Fallback.OnFailure, "passthrough_to_default_spa", StringComparison.OrdinalIgnoreCase))
            {
                await ServeSpaFallbackAsync(context, next);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(
                new MessageResponse($"微服务 '{winningPlugin.Id}' 端口未就绪"),
                AppJsonSerializerContext.Default.MessageResponse);
            return;
        }

        var (host, targetPort) = ResolveDestination(winningRoute.Target, port);

        // 6. 根据 Transport 模式进行全双工流劫持或 HTTP 代理转发
        var mode = winningRoute.Transport.Mode.ToLowerInvariant();
        if (mode == "duplex_stream")
        {
            if (context.WebSockets.IsWebSocketRequest)
            {
                string? negotiatedSubProtocol = null;
                if (context.Request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var clientProtocols))
                {
                    var requested = clientProtocols.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    foreach (var p in requested)
                    {
                        if (p.Equals("lwt.v1", StringComparison.OrdinalIgnoreCase) ||
                            p.Equals("chat.v1", StringComparison.OrdinalIgnoreCase) ||
                            p.Equals("stream.v1", StringComparison.OrdinalIgnoreCase) ||
                            (!string.IsNullOrEmpty(winningRoute.Security.TokenSubprotocolPrefix) &&
                             p.StartsWith(winningRoute.Security.TokenSubprotocolPrefix, StringComparison.OrdinalIgnoreCase)))
                        {
                            negotiatedSubProtocol = p;
                            break;
                        }
                    }
                }

                using var webSocket = negotiatedSubProtocol != null
                    ? await context.WebSockets.AcceptWebSocketAsync(negotiatedSubProtocol)
                    : await context.WebSockets.AcceptWebSocketAsync();

                await forwarder.ForwardWebSocketToTcpAsync(webSocket, host, targetPort, ct);
                return;
            }

            var upgradeFeature = context.Features.Get<IHttpUpgradeFeature>();
            if (upgradeFeature != null && upgradeFeature.IsUpgradableRequest)
            {
                using var rawStream = await upgradeFeature.UpgradeAsync();
                await forwarder.ForwardStreamToTcpAsync(rawStream, host, targetPort, ct);
                return;
            }

            // 客户端发起的是普通 HTTP 请求而非双工升级握手
            if (string.Equals(winningRoute.Fallback.OnUnauthorized, "passthrough_to_default_spa", StringComparison.OrdinalIgnoreCase))
            {
                // 普通 HTTP 探测直接回落至默认 SPA
                await ServeSpaFallbackAsync(context, next);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("Duplex stream upgrade required (WebSocket or HTTP Upgrade)", ct);
            return;
        }
        else
        {
            // http_reverse_proxy 或 sse
            await ForwardHttpRequestAsync(context, winningRoute, winner.MatchedPath, host, targetPort, httpClientFactory);
            return;
        }
    }

    /// <summary>
    /// 解析生效路径：区分标准隔离轨 (Standard) 与特权伪装轨 (Stealth)。
    /// 严禁任何模式侵犯宿主系统保留路径。
    /// </summary>
    private static List<(string Path, bool IsExact)> ResolveEffectivePaths(
        string pluginId,
        IngressMatchConfig match,
        bool isStealth,
        ILogger logger)
    {
        var result = new List<(string Path, bool IsExact)>();
        bool isExact = string.Equals(match.PathType, "Exact", StringComparison.OrdinalIgnoreCase);

        var rawPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(match.Path))
        {
            rawPaths.Add(match.Path.Trim());
        }
        if (match.Aliases != null && match.Aliases.Count > 0)
        {
            foreach (var alias in match.Aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias))
                    rawPaths.Add(alias.Trim());
            }
        }

        foreach (var raw in rawPaths)
        {
            if (isStealth)
            {
                // 特权伪装轨：需通过保留路由准入验证
                var (isAllowed, reason) = HostReservedRoutes.ValidateDeclaredPath(raw);
                if (!isAllowed)
                {
                    logger.LogWarning("[PluginIngress] 插件 '{PluginId}' 声明的伪装路径 '{Path}' 被准入守卫拦截: {Reason}",
                        pluginId, raw, reason);
                    continue;
                }
                result.Add((raw, isExact));
            }
            else
            {
                // 标准隔离轨：自动挂载统一插件前缀 /plugins/{pluginId}/...
                var cleanPath = raw.TrimStart('/');
                var scopedPath = cleanPath.Length == 0 ? $"/plugins/{pluginId}" : $"/plugins/{pluginId}/{cleanPath}";
                result.Add((scopedPath, isExact));
            }
        }

        return result;
    }

    /// <summary>
    /// 多维消歧优先级评分矩阵 (Precedence Strategy)
    /// </summary>
    private static int CalculatePrecedenceScore(
        HttpContext context,
        PluginIngressRoute route,
        string matchedPath,
        bool isExact)
    {
        int score = 0;

        // 1. 特权子协议消歧 (最高权重 +1000)
        if (!string.IsNullOrEmpty(route.Security.TokenSubprotocolPrefix) &&
            context.Request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var subprotocols))
        {
            var prefix = route.Security.TokenSubprotocolPrefix;
            foreach (var sp in subprotocols.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (sp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    score += 1000;
                    break;
                }
            }
        }

        // 2. 请求头精确匹配消歧 (权重 +500)
        if (route.Match.Headers.Count > 0)
        {
            score += 500;
        }

        // 3. 路径精确度消歧 (Exact 比 Prefix 高 +200)
        if (isExact)
        {
            score += 200;
        }

        // 4. 最长前缀消歧 (长路径优先 +每字符1分)
        score += Math.Min(matchedPath.Length, 100);

        return score;
    }

    private static bool IsHeaderMatch(HttpContext context, IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count == 0) return true;

        foreach (var (headerKey, expectedVal) in headers)
        {
            if (headerKey.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
            {
                var isWs = context.WebSockets.IsWebSocketRequest;
                var upgradeVal = context.Request.Headers.Upgrade.ToString();
                if (!isWs && !upgradeVal.Contains(expectedVal, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            else
            {
                if (!context.Request.Headers.TryGetValue(headerKey, out var actualVal) ||
                    !actualVal.ToString().Contains(expectedVal, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsPathMatch(string pattern, string pathType, string requestPath)
    {
        if (string.Equals(pathType, "Exact", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(requestPath, pattern, StringComparison.OrdinalIgnoreCase);
        }

        // Prefix
        if (string.Equals(pattern, "/", StringComparison.Ordinal))
        {
            return true;
        }

        return requestPath.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) &&
               (requestPath.Length == pattern.Length ||
                pattern.EndsWith('/') ||
                requestPath[pattern.Length] == '/');
    }

    private static async Task ServeSpaFallbackAsync(HttpContext context, RequestDelegate next)
    {
        var webRoot = context.RequestServices.GetService<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>()?.WebRootPath
                      ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var indexHtml = Path.Combine(webRoot, "index.html");
        if (File.Exists(indexHtml))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.SendFileAsync(indexHtml);
            return;
        }

        context.SetEndpoint(null);
        await next(context);
    }

    private static (bool IsAllowed, string? Reason) EvaluateSecurity(
        HttpContext context,
        PluginInstanceDto plugin,
        IngressSecurityConfig security)
    {
        // 1. IP CIDR 范围校验
        if (!string.IsNullOrWhiteSpace(security.AllowedSubnets) && security.AllowedSubnets != "*")
        {
            var remoteIp = context.Connection.RemoteIpAddress;
            if (remoteIp == null)
            {
                return (false, "无法获取客户端来源 IP");
            }

            if (remoteIp.IsIPv4MappedToIPv6)
            {
                remoteIp = remoteIp.MapToIPv4();
            }

            if (!IsIpInSubnets(remoteIp, security.AllowedSubnets))
            {
                return (false, $"客户端 IP '{remoteIp}' 不在 Ingress 允许网段内");
            }
        }

        // 2. 鉴权模式裁决
        var authMode = security.Auth.ToLowerInvariant();
        switch (authMode)
        {
            case "none":
                return (true, null);

            case "secret_token":
                var headerName = string.IsNullOrWhiteSpace(security.TokenHeader) ? "X-LWT-Token" : security.TokenHeader;
                string? providedToken = null;

                if (context.Request.Headers.TryGetValue(headerName, out var hVal))
                {
                    providedToken = hVal.ToString().Trim();
                }
                else if (!string.IsNullOrWhiteSpace(security.TokenQuery) &&
                         context.Request.Query.TryGetValue(security.TokenQuery, out var qVal))
                {
                    providedToken = qVal.ToString().Trim();
                }
                else if (context.Request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var subprotocols))
                {
                    // 支持标准 WebSocket 子协议无痕鉴权 (RFC 6455)
                    var prefix = security.TokenSubprotocolPrefix ?? "lwt-token.";
                    foreach (var sp in subprotocols.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (!string.IsNullOrEmpty(prefix) && sp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            providedToken = sp[prefix.Length..];
                            break;
                        }
                        if (!string.IsNullOrEmpty(security.SecretValue) && string.Equals(sp, security.SecretValue, StringComparison.Ordinal))
                        {
                            providedToken = sp;
                            break;
                        }
                        if (!string.IsNullOrEmpty(plugin.InternalSecret) && string.Equals(sp, plugin.InternalSecret, StringComparison.Ordinal))
                        {
                            providedToken = sp;
                            break;
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(providedToken))
                {
                    return (false, "缺少必需的 Ingress 访问令牌");
                }

                bool tokenValid = false;
                if (!string.IsNullOrEmpty(security.SecretValue) && string.Equals(providedToken, security.SecretValue, StringComparison.Ordinal))
                {
                    tokenValid = true;
                }
                else if (!string.IsNullOrEmpty(plugin.InternalSecret) && string.Equals(providedToken, plugin.InternalSecret, StringComparison.Ordinal))
                {
                    tokenValid = true;
                }

                if (!tokenValid)
                {
                    return (false, "Ingress 访问令牌不匹配");
                }

                return (true, null);

            case "platform_session":
                var isAuth = context.User?.Identity?.IsAuthenticated == true ||
                             context.Request.Cookies.ContainsKey("lwt_token") ||
                             context.Request.Headers.ContainsKey("Authorization");
                if (!isAuth)
                {
                    return (false, "需要平台管理员登录会话");
                }
                return (true, null);

            case "inherit":
            default:
                var hasSession = context.User?.Identity?.IsAuthenticated == true ||
                                 context.Request.Cookies.ContainsKey("lwt_token");
                if (hasSession) return (true, null);
                return (false, "未通过平台默认认证");
        }
    }

    private static bool IsIpInSubnets(IPAddress remoteIp, string subnetsStr)
    {
        var subnets = subnetsStr.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var subnet in subnets)
        {
            if (subnet == "*") return true;

            if (IPNetwork.TryParse(subnet, out var network))
            {
                if (network.Contains(remoteIp)) return true;
            }
            else if (IPAddress.TryParse(subnet, out var singleIp))
            {
                if (remoteIp.Equals(singleIp)) return true;
            }
        }
        return false;
    }

    private static (string Host, int Port) ResolveDestination(IngressTargetConfig target, int allocatedPort)
    {
        var dest = target.Destination;
        if (string.IsNullOrWhiteSpace(dest))
        {
            dest = "127.0.0.1:{port}";
        }

        dest = dest.Replace("{port}", allocatedPort.ToString());
        var host = "127.0.0.1";
        var port = allocatedPort;

        if (dest.Contains(':'))
        {
            var parts = dest.Split(':');
            host = parts[0];
            if (int.TryParse(parts[1], out var parsedPort))
            {
                port = parsedPort;
            }
        }

        return (host, port);
    }

    private static async Task ForwardHttpRequestAsync(
        HttpContext context,
        PluginIngressRoute route,
        string matchedPrefix,
        string host,
        int port,
        IHttpClientFactory httpClientFactory)
    {
        var client = httpClientFactory.CreateClient();
        var path = context.Request.Path.Value ?? "/";

        if (route.Target.StripPrefix && path.StartsWith(matchedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            path = path[matchedPrefix.Length..];
            if (!path.StartsWith('/')) path = "/" + path;
        }

        var queryString = context.Request.QueryString.Value ?? "";
        var targetUri = $"http://{host}:{port}{path}{queryString}";

        using var reqMsg = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

        foreach (var h in context.Request.Headers)
        {
            if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            if (h.Key.StartsWith("X-LWT-Internal-", StringComparison.OrdinalIgnoreCase)) continue;
            reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
        }

        reqMsg.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1");
        reqMsg.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
        reqMsg.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", matchedPrefix);

        if (HttpMethods.IsPost(context.Request.Method) ||
            HttpMethods.IsPut(context.Request.Method) ||
            HttpMethods.IsPatch(context.Request.Method))
        {
            reqMsg.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType != null)
            {
                reqMsg.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
        }

        try
        {
            using var respMsg = await client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
            context.Response.StatusCode = (int)respMsg.StatusCode;
            foreach (var h in respMsg.Headers)
            {
                if (IsHopByHopHeader(h.Key)) continue;
                context.Response.Headers[h.Key] = h.Value.ToArray();
            }
            foreach (var h in respMsg.Content.Headers)
            {
                if (IsHopByHopHeader(h.Key)) continue;
                context.Response.Headers[h.Key] = h.Value.ToArray();
            }
            await respMsg.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(
                new MessageResponse($"微服务代理访问失败: {ex.Message}"),
                AppJsonSerializerContext.Default.MessageResponse);
        }
    }

    private static bool IsHopByHopHeader(string header)
    {
        return header.Equals("Connection", StringComparison.OrdinalIgnoreCase)
            || header.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
            || header.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
            || header.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
    }
}
