using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Tunnel;

/// <summary>
/// FRP 穿透独立运行实例
/// 负责单个线路的 WebSocket 长连接、HTTP/SOCKS 上游代理、心跳探活与 302 内网代拉流
/// </summary>
public sealed class FrpTunnelInstance(
    FrpTunnelLineEntity config,
    ILogger logger)
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
        "te", "trailer", "trailers", "transfer-encoding", "upgrade", "proxy-connection"
    };

    // 局域网转发与代拉流专用客户端：强制 UseProxy = false，绝不走上游出口代理
    private readonly HttpClient _localHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Proxy = null,
        EnableMultipleHttp2Connections = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    });

    private readonly ConcurrentQueue<FrpTunnelLogItem> _logs = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _instanceCts;
    private Task? _runTask;
    private bool _isStopped = true;

    public string LineId => Config.Id;
    public FrpTunnelLineEntity Config { get; private set; } = config;
    public string State { get; private set; } = "Disconnected";
    public string? PublicUrl { get; private set; }
    public DateTime? ConnectedAt { get; private set; }
    public long SentBytes { get; private set; }
    public long ReceivedBytes { get; private set; }
    public string? LastError { get; private set; }
    public long UptimeSeconds => ConnectedAt.HasValue && State == "Connected"
        ? (long)(DateTime.UtcNow - ConnectedAt.Value).TotalSeconds
        : 0;

    public IReadOnlyList<FrpTunnelLogItem> GetRecentLogs() => _logs.ToArray();

    public void UpdateConfig(FrpTunnelLineEntity updated)
    {
        Config = updated;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStopped) return Task.CompletedTask;
        _isStopped = false;
        _instanceCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunLoopAsync(_instanceCts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _isStopped = true;
        State = "Stopped";
        _instanceCts?.Cancel();
        if (_ws != null)
        {
            try
            {
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Line stopped", CancellationToken.None);
            }
            catch { }
            _ws.Dispose();
            _ws = null;
        }
    }

    private void AddLog(string level, string message)
    {
        _logs.Enqueue(new FrpTunnelLogItem(DateTime.UtcNow, level, message));
        while (_logs.Count > 100) _logs.TryDequeue(out _);
        if (level == "ERR") logger.LogError("[FRP Line {Name}] {Message}", Config.Name, message);
        else if (level == "WRN") logger.LogWarning("[FRP Line {Name}] {Message}", Config.Name, message);
        else logger.LogInformation("[FRP Line {Name}] {Message}", Config.Name, message);
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        var backoffSeconds = 1;
        while (!token.IsCancellationRequested && !_isStopped)
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            try
            {
                State = "Connecting";
                AddLog("INFO", $"正在连接边缘网关: {Config.ServerUrl} (Host: {Config.TunnelHost})...");

                var (ws, invoker) = CreateConfiguredWebSocket();
                _ws = ws;
                using (invoker)
                {
                    var wsUri = BuildWebSocketUri(Config.ServerUrl, Config.TunnelHost, Config.ApiKey);
                    connectCts.CancelAfter(TimeSpan.FromSeconds(25));
                    await _ws.ConnectAsync(wsUri, invoker, connectCts.Token);

                    State = "Connected";
                    ConnectedAt = DateTime.UtcNow;
                    backoffSeconds = 1;
                    LastError = null;
                    PublicUrl = $"{ResolveHttpOrigin(Config.ServerUrl)}/tunnel/{Config.TunnelHost}/";
                    AddLog("INFO", $"✓ 已成功连接至边缘网关 (Host: {Config.TunnelHost})");

                    using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    var heartbeatTask = RunHeartbeatAsync(_ws, heartbeatCts.Token);

                    await ProcessWebSocketMessagesAsync(_ws, token);

                    heartbeatCts.Cancel();
                    try { await heartbeatTask; } catch { }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || _isStopped)
            {
                break;
            }
            catch (OperationCanceledException) when (connectCts.IsCancellationRequested)
            {
                LastError = "连接网关超时 (25 秒无响应)，可能受境内网络路由劣化影响，建议在配置中指定 SOCKS5 / HTTP 代理";
                AddLog("ERR", LastError);
            }
            catch (WebSocketException ex) when (ex.WebSocketErrorCode == WebSocketError.NotAWebSocket)
            {
                LastError = "网关非有效 WebSocket 服务端: " + ex.Message;
                AddLog("ERR", LastError);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                AddLog("WRN", $"连接异常中断: {ex.Message}");
            }
            finally
            {
                if (State != "Displaced") State = _isStopped ? "Stopped" : "Reconnecting";
                ConnectedAt = null;
                if (_ws != null)
                {
                    try { _ws.Dispose(); } catch { }
                    _ws = null;
                }
            }

            if (_isStopped || State == "Displaced") break;

            AddLog("INFO", $"{backoffSeconds} 秒后尝试重新连接...");
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), token);
            }
            catch (OperationCanceledException) { break; }

            backoffSeconds = Math.Min(backoffSeconds * 2, 30);
        }

        if (_isStopped) State = "Stopped";
    }

    private (ClientWebSocket ws, HttpMessageInvoker invoker) CreateConfiguredWebSocket()
    {
        var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(Math.Max(5, Config.HeartbeatIntervalSeconds));
        if (!string.IsNullOrWhiteSpace(Config.ApiKey))
        {
            ws.Options.SetRequestHeader("x-pyw-token", Config.ApiKey.Trim());
        }

        var handler = new SocketsHttpHandler
        {
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        };

        var proxyUrl = ResolveProxyUrl();
        if (!string.IsNullOrWhiteSpace(proxyUrl))
        {
            var proxyUri = new Uri(proxyUrl);
            var webProxy = new WebProxy(proxyUri);

            if (!string.IsNullOrEmpty(proxyUri.UserInfo))
            {
                var parts = proxyUri.UserInfo.Split(':', 2);
                webProxy.Credentials = new NetworkCredential(
                    Uri.UnescapeDataString(parts[0]),
                    parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
            }

            webProxy.BypassProxyOnLocal = true;
            webProxy.BypassList = new[]
            {
                "localhost", "127.0.0.1", "[::1]",
                "192.168.*", "10.*", "172.16.*", "172.17.*", "172.18.*", "172.19.*",
                "172.2*.*", "172.30.*", "172.31.*", "*.local", "*.lan"
            };

            handler.Proxy = webProxy;
            handler.UseProxy = true;
            AddLog("INFO", $"🌐 启用上游出口代理: {proxyUri.Scheme}://{proxyUri.Host}:{proxyUri.Port}");
        }
        else
        {
            handler.UseProxy = false;
            // 直连模式：优先连接 IPv4，彻底规避因 Cloudflare IPv6 路由黑洞导致的 21 秒假死超时
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                var entry = await Dns.GetHostEntryAsync(context.DnsEndPoint.Host, cancellationToken);
                var addresses = entry.AddressList
                    .OrderBy(ip => ip.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToArray();

                Socket? socket = null;
                foreach (var ip in addresses)
                {
                    socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    socket.NoDelay = true;
                    try
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        linkedCts.CancelAfter(TimeSpan.FromSeconds(5));
                        await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), linkedCts.Token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        socket = null;
                    }
                }

                throw new SocketException((int)SocketError.HostUnreachable);
            };
        }

        var invoker = new HttpMessageInvoker(handler);
        return (ws, invoker);
    }

    private string? ResolveProxyUrl()
    {
        if (Config.ProxyType is "Http" or "Socks5" or "Custom" && !string.IsNullOrWhiteSpace(Config.ProxyUrl))
        {
            return Config.ProxyUrl.Trim();
        }
        if (Config.ProxyType == "System")
        {
            return Environment.GetEnvironmentVariable("ALL_PROXY")
                ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")
                ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
        }
        return null;
    }

    public static Uri BuildWebSocketUri(string serverUrl, string tunnelHost, string? apiKey)
    {
        var raw = (serverUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(raw))
            throw new ArgumentException("Server URL cannot be empty", nameof(serverUrl));

        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }

        var uri = new Uri(raw);
        var scheme = (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase))
            ? "ws"
            : "wss";

        var builder = new UriBuilder(uri)
        {
            Scheme = scheme
        };

        var path = builder.Path.TrimEnd('/');
        if (!path.EndsWith("/tunnel/connect", StringComparison.OrdinalIgnoreCase))
        {
            builder.Path = $"{path}/tunnel/connect";
        }

        var query = new StringBuilder();
        query.Append($"host={Uri.EscapeDataString(tunnelHost)}");
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            var escKey = Uri.EscapeDataString(apiKey.Trim());
            query.Append($"&key={escKey}&token={escKey}");
        }
        builder.Query = query.ToString();

        return builder.Uri;
    }

    public static string ResolveHttpOrigin(string serverUrl)
    {
        var raw = (serverUrl ?? string.Empty).Trim();
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }

        var uri = new Uri(raw);
        var scheme = (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase))
            ? "http"
            : "https";

        var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{scheme}://{uri.Host}{portPart}";
    }

    private async Task RunHeartbeatAsync(ClientWebSocket ws, CancellationToken token)
    {
        var pingBytes = Encoding.UTF8.GetBytes("{\"type\":\"ping\"}");
        var interval = TimeSpan.FromSeconds(Math.Max(5, Config.HeartbeatIntervalSeconds));
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                await Task.Delay(interval, token);
                if (ws.State != WebSocketState.Open) break;
                await ws.SendAsync(pingBytes, WebSocketMessageType.Text, true, token);
                SentBytes += pingBytes.Length;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                AddLog("WRN", $"心跳发送失败: {ex.Message}");
                break;
            }
        }
    }

    private async Task ProcessWebSocketMessagesAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        var ms = new MemoryStream();

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (ws.CloseStatus == (WebSocketCloseStatus)4002)
                {
                    State = "Displaced";
                    LastError = "同名隧道已在其他客户端上线，当前实例已被顶替下线 (4002 Displaced)";
                    AddLog("WRN", LastError);
                    break;
                }
                AddLog("INFO", $"边缘网关断开连接: {ws.CloseStatusDescription}");
                break;
            }

            ReceivedBytes += result.Count;
            ms.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                var payloadBytes = ms.ToArray();
                ms.SetLength(0);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    HandleJsonMessage(ws, payloadBytes, token);
                }
            }
        }
    }

    private void HandleJsonMessage(ClientWebSocket ws, byte[] payloadBytes, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(payloadBytes);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeProp)) return;

        var type = typeProp.GetString();
        switch (type)
        {
            case "TUNNEL_CONNECTED" or "ready":
            {
                var host = root.TryGetProperty("host", out var hP) ? hP.GetString() : Config.TunnelHost;
                var pathModeUrl = root.TryGetProperty("pathModeUrl", out var pP) ? pP.GetString() : $"/tunnel/{host}/";
                PublicUrl = $"{ResolveHttpOrigin(Config.ServerUrl)}{pathModeUrl}";
                AddLog("INFO", $"🚀 公网映射挂载成功: {PublicUrl}");
                break;
            }
            case "pong" or "PONG":
                break;
            case "HTTP_REQUEST":
            {
                _ = Task.Run(() => ForwardHttpRequestAsync(ws, root.Clone(), token), token);
                break;
            }
        }
    }

    internal async Task ForwardHttpRequestAsync(WebSocket ws, JsonElement reqFrame, CancellationToken token)
    {
        var requestId = reqFrame.GetProperty("requestId").GetString()!;
        var method = reqFrame.GetProperty("method").GetString() ?? "GET";
        var path = reqFrame.TryGetProperty("path", out var pathP) ? pathP.GetString() : "/";
        var bodyB64 = reqFrame.TryGetProperty("body", out var bP) ? bP.GetString() : null;

        var targetBase = Config.LocalTargetUrl.TrimEnd('/');
        var targetUri = new Uri($"{targetBase}{path}");

        using var httpRequest = new HttpRequestMessage(new HttpMethod(method), targetUri);

        if (reqFrame.TryGetProperty("headers", out var headersObj))
        {
            foreach (var prop in headersObj.EnumerateObject())
            {
                var k = prop.Name;
                if (HopByHopHeaders.Contains(k)) continue;
                if (k.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.Equals("accept-encoding", StringComparison.OrdinalIgnoreCase)) continue;

                httpRequest.Headers.TryAddWithoutValidation(k, prop.Value.GetString());
            }
        }

        httpRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        httpRequest.Headers.TryAddWithoutValidation("X-Forwarded-Host", targetUri.Authority);

        if (!string.IsNullOrEmpty(bodyB64) && method != "GET" && method != "HEAD")
        {
            var bodyBytes = Convert.FromBase64String(bodyB64);
            httpRequest.Content = new ByteArrayContent(bodyBytes);
        }

        try
        {
            var currentUrl = targetUri.ToString();
            var currentMethod = method;
            byte[]? currentBodyBytes = !string.IsNullOrEmpty(bodyB64) && method != "GET" && method != "HEAD"
                ? Convert.FromBase64String(bodyB64)
                : null;

            var (httpResponse, _, _) = await ExecuteLocalHttpWith302Async(httpRequest, currentUrl, currentMethod, currentBodyBytes, token);

            using (httpResponse)
            {
                var statusCode = (int)httpResponse.StatusCode;
                var respHeaders = new Dictionary<string, string>();
                foreach (var (k, v) in httpResponse.Headers)
                {
                    if (!HopByHopHeaders.Contains(k)) respHeaders[k] = string.Join(", ", v);
                }
                foreach (var (k, v) in httpResponse.Content.Headers)
                {
                    if (!HopByHopHeaders.Contains(k)) respHeaders[k] = string.Join(", ", v);
                }

                var contentLength = httpResponse.Content.Headers.ContentLength;
                var isSmall = contentLength.HasValue && contentLength.Value <= 1024 * 1024;
                var isNoBody = statusCode == 204 || statusCode == 304 || method == "HEAD";

                if (isNoBody || isSmall)
                {
                    var bodyBytes = isNoBody ? Array.Empty<byte>() : await httpResponse.Content.ReadAsByteArrayAsync(token);
                    var respPayload = new
                    {
                        type = "HTTP_RESPONSE",
                        requestId,
                        status = statusCode,
                        headers = respHeaders,
                        body = Convert.ToBase64String(bodyBytes)
                    };

                    var frameBytes = JsonSerializer.SerializeToUtf8Bytes(respPayload);
                    await ws.SendAsync(frameBytes, WebSocketMessageType.Text, true, token);
                    SentBytes += frameBytes.Length;
                }
                else
                {
                    var headPayload = new
                    {
                        type = "HTTP_RESPONSE_START",
                        requestId,
                        status = statusCode,
                        headers = respHeaders
                    };
                    var headBytes = JsonSerializer.SerializeToUtf8Bytes(headPayload);
                    await ws.SendAsync(headBytes, WebSocketMessageType.Text, true, token);
                    SentBytes += headBytes.Length;

                    using var stream = await httpResponse.Content.ReadAsStreamAsync(token);
                    var streamBuf = new byte[32 * 1024];
                    var reqIdBytes = Encoding.UTF8.GetBytes(requestId);
                    var idLen = (byte)reqIdBytes.Length;

                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(streamBuf, token)) > 0)
                    {
                        // Cloudflare DO 零拷贝流式分片格式: [1字节 0x01][1字节 idLen][idLen 字节 reqId][原始二进制分片]
                        var chunkMsg = new byte[2 + idLen + bytesRead];
                        chunkMsg[0] = 0x01;
                        chunkMsg[1] = idLen;
                        Buffer.BlockCopy(reqIdBytes, 0, chunkMsg, 2, idLen);
                        Buffer.BlockCopy(streamBuf, 0, chunkMsg, 2 + idLen, bytesRead);

                        await ws.SendAsync(chunkMsg, WebSocketMessageType.Binary, true, token);
                        SentBytes += chunkMsg.Length;
                    }

                    var endPayload = new { type = "HTTP_RESPONSE_END", requestId };
                    var endBytes = JsonSerializer.SerializeToUtf8Bytes(endPayload);
                    await ws.SendAsync(endBytes, WebSocketMessageType.Text, true, token);
                    SentBytes += endBytes.Length;
                }
            }
        }
        catch (Exception ex)
        {
            AddLog("ERR", $"本地代理转发失败: {ex.Message}");
            var errPayload = new
            {
                type = "HTTP_RESPONSE",
                requestId,
                status = 502,
                headers = new Dictionary<string, string> { ["content-type"] = "text/plain; charset=utf-8" },
                body = Convert.ToBase64String(Encoding.UTF8.GetBytes("Bad Gateway: " + ex.Message))
            };
            var errBytes = JsonSerializer.SerializeToUtf8Bytes(errPayload);
            try
            {
                await ws.SendAsync(errBytes, WebSocketMessageType.Text, true, token);
                SentBytes += errBytes.Length;
            }
            catch { }
        }
    }

    internal async Task<(HttpResponseMessage Response, bool FollowedLanRedirect, string FinalUrl)> ExecuteLocalHttpWith302Async(
        HttpRequestMessage httpRequest,
        string initialUrl,
        string method,
        byte[]? bodyBytes,
        CancellationToken token)
    {
        var currentUrl = initialUrl;
        var currentMethod = method;
        byte[]? currentBodyBytes = bodyBytes;
        var followedLanRedirect = false;

        var httpResponse = await _localHttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token);

        // === 302 自动代理与内网私网智能代拉处理 ===
        if (Config.EnableLan302Proxy)
        {
            var redirectCount = 0;
            var visitedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { currentUrl };

            while ((int)httpResponse.StatusCode is 301 or 302 or 303 or 307 or 308 && redirectCount < 5)
            {
                redirectCount++;
                var location = httpResponse.Headers.Location?.ToString();
                if (string.IsNullOrWhiteSpace(location)) break;

                var nextUri = new Uri(new Uri(currentUrl), location);
                var nextUrl = nextUri.ToString();

                if (!visitedUrls.Add(nextUrl))
                {
                    AddLog("WRN", $"检测到内网重定向循环，终止跟随: {nextUrl}");
                    break;
                }

                if (NetworkAddressClassifier.IsPrivateNetworkUrl(nextUrl))
                {
                    AddLog("INFO", $">> [STRM/私网重定向] 本地服务重定向至私有地址，客户端在局域网内代为拉流: {nextUrl}");

                    // RFC 9110：301/302/303 非 GET/HEAD 降级为 GET 并清除 Body
                    if ((int)httpResponse.StatusCode is 301 or 302 or 303 && currentMethod != "GET" && currentMethod != "HEAD")
                    {
                        currentMethod = "GET";
                        currentBodyBytes = null;
                    }

                    httpResponse.Dispose();

                    using var nextReq = new HttpRequestMessage(new HttpMethod(currentMethod), nextUri);
                    nextReq.Headers.TryAddWithoutValidation("Host", nextUri.Authority);
                    nextReq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
                    if (currentBodyBytes != null && currentMethod != "GET" && currentMethod != "HEAD")
                    {
                        nextReq.Content = new ByteArrayContent(currentBodyBytes);
                    }

                    try
                    {
                        httpResponse = await _localHttpClient.SendAsync(nextReq, HttpCompletionOption.ResponseHeadersRead, token);
                        currentUrl = nextUrl;
                        followedLanRedirect = true;
                    }
                    catch (Exception redEx)
                    {
                        AddLog("WRN", $">> 跟进局域网重定向失败: {redEx.Message}");
                        break;
                    }
                }
                else
                {
                    // 目标为公网 CDN 直链（如阿里云盘/115/R2/COS），智能放行 302，卸载家庭上行带宽
                    AddLog("INFO", $">> [CDN/直链放行] 目标为公网 CDN，放行 302 让播放器直连下载: {nextUrl}");
                    break;
                }
            }
        }

        return (httpResponse, followedLanRedirect, currentUrl);
    }
}
