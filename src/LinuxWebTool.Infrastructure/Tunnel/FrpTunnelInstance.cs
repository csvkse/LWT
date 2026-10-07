using System.Buffers;
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

    // 局域网转发与代拉流专用客户端：强制 UseProxy = false，绝不走上游出口代理；支持局域网自签名 SSL 证书
    private readonly HttpClient _localHttpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        Proxy = null,
        EnableMultipleHttp2Connections = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        SslOptions = new System.Net.Security.SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
        }
    });

    private readonly ConcurrentDictionary<string, TunnelRequestSession> _sessions = new();
    private readonly MediaStreamCoordinator _mediaCoordinator = new();

    private readonly ConcurrentQueue<FrpTunnelLogItem> _logs = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _instanceCts;
    private Task? _runTask;
    private bool _isStopped = true;

    public string LineId => Config.Id;
    public FrpTunnelLineEntity Config { get; private set; } = config;
    public string State { get; private set; } = "Disconnected";
    public string? PublicUrl { get; private set; }
    private readonly SemaphoreSlim _wsSendLock = new(1, 1);
    private long _sentBytes;
    private long _receivedBytes;

    public string? SubdomainUrl { get; private set; }
    public DateTime? ConnectedAt { get; private set; }
    public long SentBytes => Volatile.Read(ref _sentBytes);
    public long ReceivedBytes => Volatile.Read(ref _receivedBytes);
    public string? LastError { get; private set; }
    public long UptimeSeconds => ConnectedAt.HasValue && State == "Connected"
        ? (long)(DateTime.UtcNow - ConnectedAt.Value).TotalSeconds
        : 0;

    private async Task<bool> SafeSendWebSocketAsync(
        WebSocket ws,
        ReadOnlyMemory<byte> buffer,
        WebSocketMessageType messageType,
        bool endOfMessage,
        CancellationToken token)
    {
        if (ws.State != WebSocketState.Open) return false;

        try
        {
            await _wsSendLock.WaitAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            if (ws.State != WebSocketState.Open) return false;
            await ws.SendAsync(buffer, messageType, endOfMessage, token);
            Interlocked.Add(ref _sentBytes, buffer.Length);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebSocketException ex)
        {
            AddLog("WRN", $"WebSocket 发送失败 ({ex.WebSocketErrorCode}): {ex.Message}");
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        finally
        {
            _wsSendLock.Release();
        }
    }

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
        foreach (var s in _sessions.Values)
        {
            s.Abort();
            s.Dispose();
        }
        _sessions.Clear();

        var ws = Interlocked.Exchange(ref _ws, null);
        if (ws != null)
        {
            try
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Line stopped", CancellationToken.None);
            }
            catch { }
            try { ws.Dispose(); } catch { }
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
                    SubdomainUrl = ResolveSubdomainUrl(Config.ServerUrl, Config.TunnelHost);
                    AddLog("INFO", $"✓ 已成功连接至边缘网关 (Host: {Config.TunnelHost})");
                    if (!string.IsNullOrEmpty(SubdomainUrl))
                    {
                        AddLog("INFO", $"🌐 独立子域名入口: {SubdomainUrl}");
                    }

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
                if (ex.Message.Contains("403"))
                {
                    LastError = "边缘网关鉴权失败 (HTTP 403 Forbidden)：请检查客户端鉴权 Token (ApiKey) 或穿透 Host 权限是否有效";
                }
                else if (ex.Message.Contains("401"))
                {
                    LastError = "边缘网关未授权 (HTTP 401 Unauthorized)：请检查客户端鉴权 Token (ApiKey) 是否正确";
                }
                else if (ex.Message.Contains("404"))
                {
                    LastError = "边缘网关端点不存在 (HTTP 404 Not Found)：请检查服务端 URL 路径是否正确";
                }
                else
                {
                    LastError = "网关握手失败 (非有效 WebSocket 服务端): " + ex.Message;
                }
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
                var ws = Interlocked.Exchange(ref _ws, null);
                if (ws != null)
                {
                    try { ws.Dispose(); } catch { }
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

    /// <summary>
    /// 解析独立二级子域名格式公网入口（如 https://lwt.asairo.de/）
    /// 支持推导 Cloudflare / ProxyByCF 泛解析与子域名直通模式，规避 IP 或 localhost
    /// </summary>
    public static string? ResolveSubdomainUrl(string? serverUrl, string? tunnelHost)
    {
        if (string.IsNullOrWhiteSpace(serverUrl) || string.IsNullOrWhiteSpace(tunnelHost))
            return null;

        var hostClean = tunnelHost.Trim().ToLowerInvariant();
        if (hostClean.Length == 0 || hostClean.Contains('/') || hostClean.Contains(':'))
            return null;

        var raw = serverUrl.Trim();
        if (!raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            raw = "https://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.Trim().ToLowerInvariant();

        // 排除本地回环、局域网私有地址、IP 地址与不支持二级子域名的 Cloudflare 默认域名
        if (IPAddress.TryParse(host, out _) ||
            host == "localhost" ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".workers.dev", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var scheme = (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("ws", StringComparison.OrdinalIgnoreCase))
            ? "http"
            : "https";

        var portPart = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";

        var parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        string baseDomain;
        if (parts.Length == 2)
        {
            // 例如 asairo.de -> lwt.asairo.de
            baseDomain = host;
        }
        else if (parts.Length == 3)
        {
            // 判断是否为国家二级代码顶级域（如 example.co.uk, test.com.cn）
            var isTwoLevelTld = (parts[1] is "co" or "com" or "net" or "org" or "gov" or "edu") && parts[2].Length == 2;
            if (isTwoLevelTld)
            {
                baseDomain = host;
            }
            else
            {
                // 例如 p.asairo.de -> 去掉网关域前缀 p -> asairo.de -> lwt.asairo.de
                baseDomain = $"{parts[1]}.{parts[2]}";
            }
        }
        else
        {
            // 多级域名：如 p.asairo.co.uk 或 edge.service.asairo.de
            // 剥离最左侧子网关前缀
            baseDomain = string.Join('.', parts.Skip(1));
        }

        return $"{scheme}://{hostClean}.{baseDomain}{portPart}/";
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
                if (!await SafeSendWebSocketAsync(ws, pingBytes, WebSocketMessageType.Text, true, token))
                {
                    break;
                }
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
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || _isStopped)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                AddLog("WRN", $"网关连接读取异常 ({ex.WebSocketErrorCode}): {ex.Message}");
                break;
            }
            catch (Exception ex)
            {
                AddLog("WRN", $"网关连接异常: {ex.Message}");
                break;
            }

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

            Interlocked.Add(ref _receivedBytes, result.Count);
            ms.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
            {
                var payloadBytes = ms.ToArray();
                ms.SetLength(0);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    try
                    {
                        HandleJsonMessage(ws, payloadBytes, token);
                    }
                    catch (Exception ex)
                    {
                        AddLog("ERR", $"[消息处理异常]: {ex.Message}");
                    }
                }
            }
        }
    }

    public static Uri CombineLocalTargetUri(string localTargetUrl, string reqPath)
    {
        var rawBase = (localTargetUrl ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(rawBase)) rawBase = "http://127.0.0.1:8080";
        var baseUri = new Uri(rawBase);
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var pathOnly = reqPath ?? "/";
        var queryOnly = string.Empty;
        var qIdx = pathOnly.IndexOf('?');
        if (qIdx >= 0)
        {
            queryOnly = pathOnly[(qIdx + 1)..];
            pathOnly = pathOnly[..qIdx];
        }

        if (!pathOnly.StartsWith('/'))
        {
            pathOnly = "/" + pathOnly;
        }

        var mergedPath = string.IsNullOrEmpty(basePath)
            ? pathOnly
            : $"{basePath}{pathOnly}";

        var builder = new UriBuilder(baseUri)
        {
            Path = mergedPath
        };
        if (!string.IsNullOrEmpty(queryOnly))
        {
            builder.Query = queryOnly;
        }
        return builder.Uri;
    }

    private void HandleJsonMessage(ClientWebSocket ws, byte[] payloadBytes, CancellationToken token)
    {
        var jsonStr = Encoding.UTF8.GetString(payloadBytes);
        AddLog("INFO", $"[WS RECV] {jsonStr}");
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
                SubdomainUrl = ResolveSubdomainUrl(Config.ServerUrl, host ?? Config.TunnelHost);
                AddLog("INFO", $"🚀 公网映射挂载成功: {PublicUrl}");
                if (!string.IsNullOrEmpty(SubdomainUrl))
                {
                    AddLog("INFO", $"🚀 独立子域名直通入口: {SubdomainUrl}");
                }
                break;
            }
            case "pong" or "PONG":
                break;
            case "HTTP_REQUEST":
            {
                if (!root.TryGetProperty("requestId", out var rIdProp) || rIdProp.GetString() is not { } requestId)
                {
                    break;
                }
                var method = root.TryGetProperty("method", out var mProp) ? mProp.GetString() ?? "GET" : "GET";
                var path = root.TryGetProperty("path", out var pathP) ? pathP.GetString() : "/";
                AddLog("INFO", $"[HTTP_REQUEST 收到] {method} {path} ({requestId})");

                var session = new TunnelRequestSession(requestId, method, path ?? "/", token);
                _sessions[requestId] = session;

                var reqHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("headers", out var hObj))
                {
                    foreach (var prop in hObj.EnumerateObject())
                    {
                        reqHeaders[prop.Name] = prop.Value.GetString() ?? string.Empty;
                    }
                }

                // 流媒体协同与拖拽寻轨抢占检测
                if (MediaStreamCoordinator.IsMediaResource(path ?? "/"))
                {
                    var sessionScope = MediaStreamCoordinator.ExtractSessionScope(reqHeaders, path ?? "/");
                    var canonicalKey = MediaStreamCoordinator.GetCanonicalMediaKey(sessionScope, path ?? "/");
                    session.CanonicalMediaKey = canonicalKey;

                    reqHeaders.TryGetValue("range", out var rangeHeader);
                    var isProbe = MediaStreamCoordinator.IsBoundedRangeProbe(rangeHeader);

                    var (superseded, wasPreempted) = _mediaCoordinator.CoordinateStream(canonicalKey, session, isProbe);
                    if (wasPreempted && superseded != null)
                    {
                        AddLog("INFO", $">> [媒体寻轨抢占] 收到全新播放请求，中止旧流: {superseded.RequestId} -> {requestId}");
                    }
                }

                var reqClone = root.Clone();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ForwardHttpRequestAsync(ws, reqClone, session);
                    }
                    catch (Exception ex)
                    {
                        AddLog("ERR", $"[ForwardHttpRequestAsync 顶层未捕获异常]: {ex}");
                    }
                }, token);
                break;
            }
            case "HTTP_ABORT":
            {
                var abortId = root.TryGetProperty("requestId", out var aP) ? aP.GetString() : null;
                if (abortId != null && _sessions.TryGetValue(abortId, out var abortSession))
                {
                    abortSession.Abort();
                    AddLog("INFO", $">> [HTTP_ABORT] 收到网关中断通知，即时中止本地拉流: {abortId}");
                }
                break;
            }
            case "HTTP_ACK":
            {
                var ackId = root.TryGetProperty("requestId", out var aP) ? aP.GetString() : null;
                var ackBytes = root.TryGetProperty("bytes", out var bP) ? bP.GetInt32() : 0;
                if (ackId != null && _sessions.TryGetValue(ackId, out var ackSession))
                {
                    ackSession.AddCredit(ackBytes);
                }
                break;
            }
        }
    }

    internal Task ForwardHttpRequestAsync(WebSocket ws, JsonElement reqFrame, CancellationToken token)
    {
        var requestId = reqFrame.TryGetProperty("requestId", out var rP) ? rP.GetString() ?? "unknown" : "unknown";
        var method = reqFrame.TryGetProperty("method", out var mP) ? mP.GetString() ?? "GET" : "GET";
        var path = reqFrame.TryGetProperty("path", out var pP) ? pP.GetString() ?? "/" : "/";
        var session = new TunnelRequestSession(requestId, method, path, token);
        _sessions[requestId] = session;
        return ForwardHttpRequestAsync(ws, reqFrame, session);
    }

    private static (HttpRequestMessage Request, bool IsWsUpgrade) CreateForwardHttpRequest(
        string method,
        Uri uri,
        JsonElement reqFrame,
        byte[]? bodyBytes)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), uri);
        var isWsUpgrade = false;

        if (reqFrame.TryGetProperty("headers", out var headersObj))
        {
            foreach (var prop in headersObj.EnumerateObject())
            {
                var k = prop.Name;
                var v = prop.Value.GetString();
                if (k.Equals("upgrade", StringComparison.OrdinalIgnoreCase) && string.Equals(v, "websocket", StringComparison.OrdinalIgnoreCase))
                {
                    isWsUpgrade = true;
                }
                if (HopByHopHeaders.Contains(k)) continue;
                if (k.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.Equals("accept-encoding", StringComparison.OrdinalIgnoreCase)) continue;

                req.Headers.TryAddWithoutValidation(k, v);
            }
        }

        req.Headers.TryAddWithoutValidation("Host", uri.Authority);
        req.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
        req.Headers.TryAddWithoutValidation("X-Forwarded-Host", uri.Authority);

        if (bodyBytes != null && method != "GET" && method != "HEAD")
        {
            req.Content = new ByteArrayContent(bodyBytes);
        }

        return (req, isWsUpgrade);
    }

    internal async Task ForwardHttpRequestAsync(WebSocket ws, JsonElement reqFrame, TunnelRequestSession session)
    {
        var requestId = session.RequestId;
        var token = session.Cts.Token;
        try
        {
            session.SetState(TunnelRequestState.Connecting);
            var method = session.Method;
            var path = session.Path;
            var bodyB64 = reqFrame.TryGetProperty("body", out var bP) ? bP.GetString() : null;

            var targetUri = CombineLocalTargetUri(Config.LocalTargetUrl, path);
            var cachedRedirect = !string.IsNullOrEmpty(session.CanonicalMediaKey)
                ? _mediaCoordinator.GetCachedRedirect(session.CanonicalMediaKey!)
                : null;

            var activeUri = targetUri;
            if (!string.IsNullOrEmpty(cachedRedirect))
            {
                activeUri = new Uri(cachedRedirect);
                AddLog("INFO", $">> [STRM/直链加速] 命中已缓存的局域网 STRM 直链，跳过 302 重定向: {cachedRedirect}");
            }

            var currentMethod = method;
            byte[]? currentBodyBytes = !string.IsNullOrEmpty(bodyB64) && method != "GET" && method != "HEAD"
                ? Convert.FromBase64String(bodyB64)
                : null;

            var (httpRequest, isWsUpgrade) = CreateForwardHttpRequest(currentMethod, activeUri, reqFrame, currentBodyBytes);
            if (isWsUpgrade)
            {
                using (httpRequest)
                {
                    var wsHeaders = new Dictionary<string, string>
                    {
                        ["upgrade"] = "websocket",
                        ["connection"] = "Upgrade",
                        ["content-type"] = "text/plain; charset=utf-8"
                    };
                    var wsBody = Convert.ToBase64String(Encoding.UTF8.GetBytes("WebSocket upgrade not tunnelled over HTTP multiplexer"));
                    var wsRespBytes = TunnelFrameSerializer.SerializeHttpResponse(requestId, 426, wsHeaders, wsBody);
                    await SafeSendWebSocketAsync(ws, wsRespBytes, WebSocketMessageType.Text, true, token);
                    session.SetState(TunnelRequestState.Completed);
                    return;
                }
            }

            HttpResponseMessage httpResponse;
            using (httpRequest)
            {
                (httpResponse, _, _) = await ExecuteLocalHttpWith302Async(httpRequest, activeUri.ToString(), currentMethod, currentBodyBytes, token, session.CanonicalMediaKey);
            }

            // 若命中缓存直链但远端已失效 (如 401/403/404)，清除缓存并回退至本地原始服务重新拉取
            if (!string.IsNullOrEmpty(cachedRedirect) && (int)httpResponse.StatusCode is 401 or 403 or 404)
            {
                AddLog("WRN", $">> 已缓存的 STRM 直链失效 (HTTP {(int)httpResponse.StatusCode})，清除缓存并回退至本地服务: {cachedRedirect}");
                _mediaCoordinator.InvalidateRedirect(session.CanonicalMediaKey!);
                httpResponse.Dispose();

                var (fallbackReq, _) = CreateForwardHttpRequest(currentMethod, targetUri, reqFrame, currentBodyBytes);
                using (fallbackReq)
                {
                    (httpResponse, _, _) = await ExecuteLocalHttpWith302Async(fallbackReq, targetUri.ToString(), currentMethod, currentBodyBytes, token, session.CanonicalMediaKey);
                }
            }

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
                var is3xxRedirect = statusCode is >= 300 and <= 308;

                if (isNoBody || is3xxRedirect || isSmall)
                {
                    var bodyBytes = isNoBody || is3xxRedirect ? Array.Empty<byte>() : await httpResponse.Content.ReadAsByteArrayAsync(token);
                    var frameBytes = TunnelFrameSerializer.SerializeHttpResponse(requestId, statusCode, respHeaders, Convert.ToBase64String(bodyBytes));
                    await SafeSendWebSocketAsync(ws, frameBytes, WebSocketMessageType.Text, true, token);
                    session.SetState(TunnelRequestState.Completed);
                }
                else
                {
                    session.SetState(TunnelRequestState.Streaming);
                    var headBytes = TunnelFrameSerializer.SerializeHttpResponseStart(requestId, statusCode, respHeaders);
                    if (!await SafeSendWebSocketAsync(ws, headBytes, WebSocketMessageType.Text, true, token))
                    {
                        session.SetState(TunnelRequestState.Completed);
                        return;
                    }

                    using var stream = await httpResponse.Content.ReadAsStreamAsync(token);
                    var streamBuf = ArrayPool<byte>.Shared.Rent(32 * 1024);
                    var reqIdBytes = Encoding.UTF8.GetBytes(requestId);
                    var idLen = (byte)reqIdBytes.Length;

                    try
                    {
                        int bytesRead;
                        while ((bytesRead = await stream.ReadAsync(streamBuf.AsMemory(0, 32 * 1024), token)) > 0)
                        {
                            // 1. 等待滑动窗口信用额度 (支持 16MB 起播突发免限流)
                            await session.ConsumeCreditAsync(bytesRead, token);

                            // 2. Cloudflare DO 零拷贝流式分片格式: [1字节 0x01][1字节 idLen][idLen 字节 reqId][原始二进制分片]
                            var chunkTotalLen = 2 + idLen + bytesRead;
                            var chunkRented = ArrayPool<byte>.Shared.Rent(chunkTotalLen);
                            try
                            {
                                chunkRented[0] = 0x01;
                                chunkRented[1] = idLen;
                                Buffer.BlockCopy(reqIdBytes, 0, chunkRented, 2, idLen);
                                Buffer.BlockCopy(streamBuf, 0, chunkRented, 2 + idLen, bytesRead);

                                if (!await SafeSendWebSocketAsync(ws, new ReadOnlyMemory<byte>(chunkRented, 0, chunkTotalLen), WebSocketMessageType.Binary, true, token))
                                {
                                    break;
                                }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(chunkRented);
                            }
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(streamBuf);
                    }

                    var endBytes = TunnelFrameSerializer.SerializeHttpResponseEnd(requestId);
                    await SafeSendWebSocketAsync(ws, endBytes, WebSocketMessageType.Text, true, token);
                    session.SetState(TunnelRequestState.Completed);
                }
            }
        }
        catch (OperationCanceledException) when (session.State == TunnelRequestState.Aborted || _instanceCts?.IsCancellationRequested == true)
        {
            AddLog("INFO", $">> [HTTP_ABORT 结束] 客户端请求已优雅中止取消: {requestId}");
        }
        catch (Exception ex)
        {
            if (session.State == TunnelRequestState.Aborted)
            {
                return;
            }
            session.SetState(TunnelRequestState.Errored);
            AddLog("ERR", $"本地代理转发失败: {ex.Message}");
            var errHeaders = new Dictionary<string, string> { ["content-type"] = "text/plain; charset=utf-8" };
            var errBody = Convert.ToBase64String(Encoding.UTF8.GetBytes("Bad Gateway: " + ex.Message));
            var errBytes = TunnelFrameSerializer.SerializeHttpResponse(requestId, 502, errHeaders, errBody);
            try
            {
                await SafeSendWebSocketAsync(ws, errBytes, WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch { }
        }
        finally
        {
            if (!string.IsNullOrEmpty(session.CanonicalMediaKey))
            {
                _mediaCoordinator.Unregister(session.CanonicalMediaKey, session);
            }
            _sessions.TryRemove(requestId, out _);
            session.Dispose();
        }
    }

    internal async Task<(HttpResponseMessage Response, bool FollowedLanRedirect, string FinalUrl)> ExecuteLocalHttpWith302Async(
        HttpRequestMessage httpRequest,
        string initialUrl,
        string method,
        byte[]? bodyBytes,
        CancellationToken token,
        string? canonicalMediaKey = null)
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

                    if (!string.IsNullOrEmpty(canonicalMediaKey))
                    {
                        _mediaCoordinator.CacheRedirect(canonicalMediaKey, nextUrl);
                    }

                    // RFC 9110：301/302/303 非 GET/HEAD 降级为 GET 并清除 Body
                    if ((int)httpResponse.StatusCode is 301 or 302 or 303 && currentMethod != "GET" && currentMethod != "HEAD")
                    {
                        currentMethod = "GET";
                        currentBodyBytes = null;
                    }

                    httpResponse.Dispose();

                    using var nextReq = new HttpRequestMessage(new HttpMethod(currentMethod), nextUri);
                    // 继承原始请求的所有请求标头（如 Range, User-Agent, Accept 等）
                    foreach (var h in httpRequest.Headers)
                    {
                        if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                        nextReq.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    }
                    nextReq.Headers.TryAddWithoutValidation("Host", nextUri.Authority);
                    nextReq.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
                    if (currentBodyBytes != null && currentMethod != "GET" && currentMethod != "HEAD")
                    {
                        nextReq.Content = new ByteArrayContent(currentBodyBytes);
                        if (httpRequest.Content != null)
                        {
                            foreach (var ch in httpRequest.Content.Headers)
                            {
                                nextReq.Content.Headers.TryAddWithoutValidation(ch.Key, ch.Value);
                            }
                        }
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
