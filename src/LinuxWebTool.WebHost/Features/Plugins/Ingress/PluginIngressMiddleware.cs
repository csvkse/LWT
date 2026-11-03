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
/// 提供路由前缀/请求头匹配、CIDR 客户端白名单守卫、统一鉴权、主动探测伪装回落（Web Fallback）、
/// 以及全双工流式对拷（WebSocket / HTTP Upgrade Raw Duplex Stream）能力。
/// </summary>
public sealed class PluginIngressMiddleware(
    RequestDelegate next,
    ILogger<PluginIngressMiddleware> logger)
{
    public async Task InvokeAsync(
        HttpContext context,
        IPluginManager pluginManager,
        IDuplexStreamForwarder forwarder,
        IHttpClientFactory httpClientFactory)
    {
        var requestPath = context.Request.Path.Value ?? "/";

        // 快速短路系统级核心静态路径（提升静态资源命中性能）
        if (requestPath.StartsWith("/app/", StringComparison.OrdinalIgnoreCase) ||
            requestPath.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) ||
            requestPath.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var ct = context.RequestAborted;
        var plugins = await pluginManager.GetAllAsync(profileFilter: null, ct);

        foreach (var plugin in plugins)
        {
            if (plugin.State != PluginState.Running)
                continue;

            var ingress = plugin.Manifest.Ingress;
            if (ingress == null || !ingress.Enabled || ingress.Routes.Count == 0)
                continue;

            foreach (var route in ingress.Routes)
            {
                if (!IsRouteMatch(context, route.Match, requestPath))
                    continue;

                // 1. 执行通用安全与条件守卫校验（IP CIDR 白名单 + 统一鉴权）
                var (isAllowed, reason) = EvaluateSecurity(context, plugin, route.Security);
                if (!isAllowed)
                {
                    logger.LogWarning("[PluginIngress] 路由 '{RouteName}' 鉴权未通过: {Reason} (Path: {Path})",
                        route.Name, reason, requestPath);

                    if (string.Equals(route.Fallback.OnUnauthorized, "passthrough_to_default_spa", StringComparison.OrdinalIgnoreCase))
                    {
                        // 核心伪装回落机制：将未授权的主动探测流量静默流转至宿主默认 SPA 前端管线，完全隐匿服务特征
                        await ServeSpaFallbackAsync(context, next);
                        return;
                    }

                    if (string.Equals(route.Fallback.OnUnauthorized, "status_code_404", StringComparison.OrdinalIgnoreCase))
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

                // 2. 验证微服务目标后端端口
                var port = plugin.AllocatedPort ?? 0;
                if (port <= 0)
                {
                    if (string.Equals(route.Fallback.OnFailure, "passthrough_to_default_spa", StringComparison.OrdinalIgnoreCase))
                    {
                        await ServeSpaFallbackAsync(context, next);
                        return;
                    }

                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsJsonAsync(
                        new MessageResponse($"微服务 '{plugin.Id}' 端口未就绪"),
                        AppJsonSerializerContext.Default.MessageResponse);
                    return;
                }

                var (host, targetPort) = ResolveDestination(route.Target, port);

                // 3. 根据 Transport 模式进行流式劫持或 HTTP 代理转发
                var mode = route.Transport.Mode.ToLowerInvariant();
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
                                    p.Equals("stream.v1", StringComparison.OrdinalIgnoreCase))
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
                    if (string.Equals(route.Fallback.OnUnauthorized, "passthrough_to_default_spa", StringComparison.OrdinalIgnoreCase))
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
                    await ForwardHttpRequestAsync(context, route, host, targetPort, httpClientFactory);
                    return;
                }
            }
        }

        // 未匹配任何插件 Ingress 路由，继续主宿主管线
        await next(context);
    }

    private static bool IsRouteMatch(HttpContext context, IngressMatchConfig match, string requestPath)
    {
        bool pathMatch = false;
        if (!string.IsNullOrEmpty(match.Path) && IsPathMatch(match.Path, match.PathType, requestPath))
        {
            pathMatch = true;
        }
        else if (match.Aliases != null && match.Aliases.Count > 0)
        {
            foreach (var alias in match.Aliases)
            {
                if (!string.IsNullOrEmpty(alias) && IsPathMatch(alias, match.PathType, requestPath))
                {
                    pathMatch = true;
                    break;
                }
            }
        }

        if (!pathMatch) return false;

        // 请求头匹配
        if (match.Headers.Count > 0)
        {
            foreach (var (headerKey, expectedVal) in match.Headers)
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
        string host,
        int port,
        IHttpClientFactory httpClientFactory)
    {
        var client = httpClientFactory.CreateClient();
        var path = context.Request.Path.Value ?? "/";
        string matchedPrefix = route.Match.Path;
        if (!path.StartsWith(matchedPrefix, StringComparison.OrdinalIgnoreCase) && route.Match.Aliases.Count > 0)
        {
            foreach (var alias in route.Match.Aliases)
            {
                if (path.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
                {
                    matchedPrefix = alias;
                    break;
                }
            }
        }

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
