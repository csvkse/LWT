using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace LinuxWebTool.WebHost.Gateway;

/// <summary>
/// 网站代理：以 /{scheme}://{authority}/路径 或 /proxy/{authority}/路径 的形式访问已登记的网站。
/// 只有出现在白名单中的 authority 会被转发，其余一律拒绝，避免网关变成开放代理（SSRF）。
/// 借鉴于 ProxyYARP。
/// </summary>
public sealed partial class WebsiteProxyTransformProvider : ITransformProvider
{
    public const string MetadataKey = "WebsiteProxy";

    private readonly WebsiteAllowList _allow;
    private readonly ILogger<WebsiteProxyTransformProvider> _logger;

    public WebsiteProxyTransformProvider(WebsiteAllowList allow, ILogger<WebsiteProxyTransformProvider> logger)
    {
        _allow = allow;
        _logger = logger;
    }

    public void ValidateRoute(TransformRouteValidationContext context) { }
    public void ValidateCluster(TransformClusterValidationContext context) { }

    public void Apply(TransformBuilderContext context)
    {
        if (context.Route?.Metadata == null ||
            !context.Route.Metadata.TryGetValue(MetadataKey, out var flag) ||
            !string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var anyBodyRewrite = context.Route.Metadata.TryGetValue(MetadataKey + ".AnyBodyRewrite", out var br)
                              && string.Equals(br, "true", StringComparison.OrdinalIgnoreCase);
        var anyCookieRewrite = context.Route.Metadata.TryGetValue(MetadataKey + ".AnyCookieRewrite", out var cr)
                                && string.Equals(cr, "true", StringComparison.OrdinalIgnoreCase);

        context.AddRequestTransform(ctx => RewriteRequestAsync(ctx));

        if (anyBodyRewrite)
            context.AddResponseTransform(ctx => RewriteBodyAsync(ctx));

        if (anyCookieRewrite)
            context.AddResponseTransform(ctx => RewriteCookiesAsync(ctx));

        if (anyBodyRewrite || anyCookieRewrite)
            context.AddResponseTransform(RewriteLocationAsync);
    }

    private const string AllowEntryKey = "__WebsiteProxyEntry";
    private const string MatchedPrefixKey = "__WebsiteProxyMatchedPrefix";

    private ValueTask RewriteRequestAsync(RequestTransformContext ctx)
    {
        var http = ctx.HttpContext;
        var raw = http.Request.Path.Value ?? "";

        // 管理后台、API 与基础设施接口永远不被代理拦截
        if (raw.StartsWith("/app", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            raw.Equals("/health", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("/scalar", StringComparison.OrdinalIgnoreCase) ||
            raw.StartsWith("/openapi", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.CompletedTask;
        }

        var rest = raw.Length > 0 && raw[0] == '/' ? raw[1..] : raw;

        // 1. 匹配 /proxy/{key} (支持 /proxy/domain 和 /proxy/alias)
        if (rest.StartsWith("proxy/", StringComparison.OrdinalIgnoreCase) || string.Equals(rest, "proxy", StringComparison.OrdinalIgnoreCase))
        {
            var afterProxy = rest.Length > 6 ? rest[6..] : "";
            var slash = afterProxy.IndexOf('/');
            var key = slash < 0 ? afterProxy : afterProxy[..slash];
            var tail = slash < 0 ? "/" : afterProxy[slash..];

            if (string.IsNullOrWhiteSpace(key) || key.Contains('?') || key.Contains('#'))
            {
                _logger.LogDebug("[WebsiteProxy] 拒绝：/proxy/ 后目标非法 {Raw}", raw);
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return ValueTask.CompletedTask;
            }

            var authKey = NormalizeAuthority(key);
            if (_allow.TryGetByAuthority(authKey, out var entry))
            {
                if (!entry!.AllowsMode("Prefix"))
                {
                    _logger.LogWarning("[WebsiteProxy] 拒绝：站点 '{Authority}' 未启用 Prefix 代理模式", authKey);
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return ValueTask.CompletedTask;
                }
                ApplyProxyRoute(ctx, entry, tail, $"/proxy/{entry.Authority}");
                return ValueTask.CompletedTask;
            }

            if (_allow.TryGetByAlias(key, out entry))
            {
                if (!entry!.AllowsMode("Alias"))
                {
                    _logger.LogWarning("[WebsiteProxy] 拒绝：站点别名 '{Alias}' 未启用 Alias 代理模式", key);
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return ValueTask.CompletedTask;
                }
                ApplyProxyRoute(ctx, entry, tail, $"/proxy/{entry.Alias}");
                return ValueTask.CompletedTask;
            }

            _logger.LogWarning("[WebsiteProxy] 拒绝：'{Key}' 不在白名单中来自 {RemoteIp}", key, http.Connection.RemoteIpAddress);
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ValueTask.CompletedTask;
        }

        // 2. 匹配 /s/{alias} (短别名模式)
        if (rest.StartsWith("s/", StringComparison.OrdinalIgnoreCase) || string.Equals(rest, "s", StringComparison.OrdinalIgnoreCase))
        {
            var afterS = rest.Length > 2 ? rest[2..] : "";
            var slash = afterS.IndexOf('/');
            var alias = slash < 0 ? afterS : afterS[..slash];
            var tail = slash < 0 ? "/" : afterS[slash..];

            if (string.IsNullOrWhiteSpace(alias) || alias.Contains('?') || alias.Contains('#'))
            {
                _logger.LogDebug("[WebsiteProxy] 拒绝：/s/ 后别名非法 {Raw}", raw);
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return ValueTask.CompletedTask;
            }

            if (_allow.TryGetByAlias(alias, out var entry))
            {
                if (!entry!.AllowsMode("Alias"))
                {
                    _logger.LogWarning("[WebsiteProxy] 拒绝：站点别名 '{Alias}' 未启用 Alias 代理模式", alias);
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return ValueTask.CompletedTask;
                }
                ApplyProxyRoute(ctx, entry, tail, $"/s/{entry.Alias}");
                return ValueTask.CompletedTask;
            }

            _logger.LogWarning("[WebsiteProxy] 拒绝：别名 '{Alias}' 不在白名单中来自 {RemoteIp}", alias, http.Connection.RemoteIpAddress);
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ValueTask.CompletedTask;
        }

        // 3. 匹配 /{scheme}://{authority} 或 /{scheme}:/{authority} (协议内嵌模式)
        var sep = rest.IndexOf("://", StringComparison.Ordinal);
        var sepLen = 3;
        if (sep < 0)
        {
            sep = rest.IndexOf(":/", StringComparison.Ordinal);
            sepLen = 2;
        }

        if (sep > 0)
        {
            var schemeText = rest[..sep];
            if (!string.Equals(schemeText, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(schemeText, "https", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("[WebsiteProxy] 拒绝：不支持的协议 {Scheme}", schemeText);
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return ValueTask.CompletedTask;
            }

            var afterScheme = rest[(sep + sepLen)..];
            var slash = afterScheme.IndexOf('/');
            var authority = slash < 0 ? afterScheme : afterScheme[..slash];
            var tail = slash < 0 ? "/" : afterScheme[slash..];

            if (authority.Contains('?') || authority.Contains('#') || string.IsNullOrWhiteSpace(authority))
            {
                _logger.LogDebug("[WebsiteProxy] 拒绝：authority 非法 {Authority}", authority);
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                return ValueTask.CompletedTask;
            }

            var authorityKey = NormalizeAuthority(authority);
            if (_allow.TryGetByAuthority(authorityKey, out var entry))
            {
                if (!entry!.AllowsMode("Scheme"))
                {
                    _logger.LogWarning("[WebsiteProxy] 拒绝：站点 '{Authority}' 未启用 Scheme 代理模式", authorityKey);
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return ValueTask.CompletedTask;
                }
                var prefix = sepLen == 2
                    ? $"/{entry.UpstreamScheme}:/{entry.Authority}"
                    : $"/{entry.UpstreamScheme}://{entry.Authority}";
                ApplyProxyRoute(ctx, entry, tail, prefix);
                return ValueTask.CompletedTask;
            }

            _logger.LogWarning(
                "[WebsiteProxy] 拒绝：authority '{Authority}' 不在白名单中（疑似开放代理/SSRF 探测）来自 {RemoteIp}",
                authorityKey, http.Connection.RemoteIpAddress);
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return ValueTask.CompletedTask;
        }

        _logger.LogDebug("[WebsiteProxy] 拒绝：路径未匹配任何代理方案 {Raw}", raw);
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        return ValueTask.CompletedTask;
    }

    private static void ApplyProxyRoute(RequestTransformContext ctx, WebsiteEntry entry, string tail, string matchedPrefix)
    {
        var upstreamScheme = entry.UseHttps ? "https" : "http";
        ctx.DestinationPrefix = $"{upstreamScheme}://{entry.Authority}";
        ctx.Path = tail;

        ctx.ProxyRequest.Headers.Remove("Host");
        ctx.ProxyRequest.Headers.TryAddWithoutValidation("Host", entry.Authority);

        if (entry.RewriteBody)
        {
            // 剥离压缩请求头，促使上游返回未压缩明文响应，确保 HTML/CSS 绝对路径改写能可靠执行
            ctx.ProxyRequest.Headers.Remove("Accept-Encoding");
        }

        ctx.HttpContext.Items[AllowEntryKey] = entry;
        ctx.HttpContext.Items[MatchedPrefixKey] = matchedPrefix;
    }

    [GeneratedRegex("""(?<==["'])/(?!/)""")]
    public static partial Regex HtmlAttrRootRelativeRegex();

    [GeneratedRegex("""(?<=url\(["']?)/(?!/)""")]
    public static partial Regex CssUrlRootRelativeRegex();

    private ValueTask RewriteBodyAsync(ResponseTransformContext ctx)
    {
        var entry = ctx.HttpContext.Items[AllowEntryKey] as WebsiteEntry;
        if (entry is not { RewriteBody: true }) return ValueTask.CompletedTask;

        var contentEncoding = ctx.ProxyResponse?.Content.Headers.ContentEncoding.ToString();
        if (string.IsNullOrEmpty(contentEncoding))
        {
            contentEncoding = ctx.HttpContext.Response.Headers.ContentEncoding.ToString();
        }
        if (!string.IsNullOrEmpty(contentEncoding))
        {
            _logger.LogDebug("[WebsiteProxy] 跳过响应体改写：响应已被压缩 (Content-Encoding: {Encoding})", contentEncoding);
            return ValueTask.CompletedTask;
        }

        var media = ctx.ProxyResponse?.Content.Headers.ContentType?.MediaType;
        if (media is null) return ValueTask.CompletedTask;

        var isHtml = media.Contains("html", StringComparison.OrdinalIgnoreCase);
        var isCss = media.Contains("css", StringComparison.OrdinalIgnoreCase);
        if (!isHtml && !isCss) return ValueTask.CompletedTask;

        var cl = ctx.ProxyResponse?.Content.Headers.ContentLength;
        if (cl.HasValue && cl.Value > 2 * 1024 * 1024)
        {
            _logger.LogDebug("[WebsiteProxy] 响应体尺寸 ({Length} 字节) 超过改写上限 2MB，跳过正则改写直接透传", cl.Value);
            return ValueTask.CompletedTask;
        }

        var prefix = (ctx.HttpContext.Items[MatchedPrefixKey] as string)
                     ?? $"/{entry.UpstreamScheme}://{entry.Authority}";

        ctx.SuppressResponseBody = true;
        return RewriteBodyCoreAsync(ctx, prefix, isHtml);
    }

    private static async ValueTask RewriteBodyCoreAsync(ResponseTransformContext ctx, string prefix, bool isHtml)
    {
        var response = ctx.ProxyResponse!;
        var stream = await response.Content.ReadAsStreamAsync(ctx.CancellationToken);
        string body;
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            body = await reader.ReadToEndAsync(ctx.CancellationToken);
        }

        body = isHtml
            ? HtmlAttrRootRelativeRegex().Replace(body, prefix + "/")
            : CssUrlRootRelativeRegex().Replace(body, prefix + "/");

        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.HttpContext.Response.ContentLength = bytes.Length;
        await ctx.HttpContext.Response.Body.WriteAsync(bytes, ctx.CancellationToken);
    }

    private ValueTask RewriteLocationAsync(ResponseTransformContext ctx)
    {
        var entry = ctx.HttpContext.Items[AllowEntryKey] as WebsiteEntry;
        if (entry is null) return ValueTask.CompletedTask;

        var headers = ctx.HttpContext.Response.Headers;
        var loc = headers.Location.ToString();
        if (string.IsNullOrEmpty(loc)) return ValueTask.CompletedTask;

        var prefix = (ctx.HttpContext.Items[MatchedPrefixKey] as string)
                     ?? $"/{entry.UpstreamScheme}://{entry.Authority}";

        if (loc.StartsWith('/') && !loc.StartsWith("//", StringComparison.Ordinal))
        {
            headers.Location = $"{prefix}{loc}";
        }
        else if (Uri.TryCreate(loc, UriKind.Absolute, out var abs) &&
                 string.Equals(NormalizeAuthority(abs.Authority), entry.Authority, StringComparison.OrdinalIgnoreCase))
        {
            headers.Location = $"{prefix}{abs.PathAndQuery}";
        }
        return ValueTask.CompletedTask;
    }

    private ValueTask RewriteCookiesAsync(ResponseTransformContext ctx)
    {
        var entry = ctx.HttpContext.Items[AllowEntryKey] as WebsiteEntry;
        if (entry is not { RewriteCookies: true }) return ValueTask.CompletedTask;

        var headers = ctx.HttpContext.Response.Headers;
        if (!headers.TryGetValue("Set-Cookie", out var values)) return ValueTask.CompletedTask;

        var prefix = (ctx.HttpContext.Items[MatchedPrefixKey] as string)
                     ?? $"/{entry.UpstreamScheme}://{entry.Authority}";
        var pathPrefix = prefix + "/";
        var rewritten = new List<string>();

        foreach (var raw in values)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            rewritten.Add(CookiePathRegex().Replace(raw, "$1" + pathPrefix.Replace("$", "$$")));
        }

        headers.SetCookie = new Microsoft.Extensions.Primitives.StringValues(rewritten.ToArray());
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex("(?i)(;\\s*path=)/(?!/)")]
    public static partial Regex CookiePathRegex();

    public static string NormalizeAuthority(string authority)
    {
        if (string.IsNullOrWhiteSpace(authority)) return "";

        var host = authority;
        var portText = "";
        var colon = authority.LastIndexOf(':');
        var bracketEnd = authority.LastIndexOf(']');
        if (colon > bracketEnd)
        {
            host = authority[..colon];
            var rest = authority[(colon + 1)..];
            if (int.TryParse(rest, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p))
                portText = p.ToString(CultureInfo.InvariantCulture);
            else
                portText = rest;
        }

        host = host.ToLowerInvariant();

        if (portText is "80" or "443")
            return host;

        return string.IsNullOrEmpty(portText) ? host : $"{host}:{portText}";
    }
}

/// <summary>
/// 内存中的网站代理白名单快照
/// </summary>
public sealed class WebsiteAllowList
{
    private volatile Dictionary<string, WebsiteEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, WebsiteEntry> _aliasMap = new(StringComparer.OrdinalIgnoreCase);

    public void Replace(IEnumerable<GatewayWebsiteEntity> entities)
    {
        var map = new Dictionary<string, WebsiteEntry>(StringComparer.OrdinalIgnoreCase);
        var aliasMap = new Dictionary<string, WebsiteEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var e in entities.Where(x => x.IsEnabled))
        {
            var target = e.TargetUrl.Trim();
            if (string.IsNullOrWhiteSpace(target)) continue;
            var uri = Uri.TryCreate(target.Contains("://") ? target : "https://" + target, UriKind.Absolute, out var u) ? u : null;
            if (uri == null) continue;
            var authority = WebsiteProxyTransformProvider.NormalizeAuthority(uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}");
            if (string.IsNullOrEmpty(authority)) continue;
            if (map.ContainsKey(authority)) continue;

            var entry = new WebsiteEntry(
                authority,
                uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase),
                e.RewriteBody,
                e.RewriteCookie,
                "Scheme,Prefix,Alias",
                e.Name);

            map[authority] = entry;

            if (!string.IsNullOrWhiteSpace(e.Name))
            {
                var cleanAlias = e.Name.Trim().ToLowerInvariant();
                if (!aliasMap.ContainsKey(cleanAlias))
                    aliasMap[cleanAlias] = entry;
            }
        }

        _entries = map;
        _aliasMap = aliasMap;
    }

    public bool TryGet(string key, out WebsiteEntry? entry)
        => _entries.TryGetValue(key, out entry) || _aliasMap.TryGetValue(key, out entry);

    public bool TryGetByAuthority(string authorityKey, out WebsiteEntry? entry)
        => _entries.TryGetValue(authorityKey, out entry);

    public bool TryGetByAlias(string aliasKey, out WebsiteEntry? entry)
        => _aliasMap.TryGetValue(aliasKey, out entry);

    public int Count => _entries.Count;
}

public sealed record WebsiteEntry(
    string Authority,
    bool UseHttps,
    bool RewriteBody,
    bool RewriteCookies,
    string AllowedModes,
    string? Alias)
{
    public string UpstreamScheme => UseHttps ? "https" : "http";

    public bool AllowsMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(AllowedModes)) return false;
        return AllowedModes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase));
    }
}
