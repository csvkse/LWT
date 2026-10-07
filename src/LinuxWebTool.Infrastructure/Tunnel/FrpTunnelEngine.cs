using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Tunnel;

/// <summary>
/// ProxyByCF FRP 反向穿透长连接引擎（纯 C# 原生 AOT 兼容实现）
/// </summary>
public sealed class FrpTunnelEngine(
    FrpTunnelConfigStore configStore,
    ILogger<FrpTunnelEngine> logger)
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
        "te", "trailer", "trailers", "transfer-encoding", "upgrade", "proxy-connection"
    };

    private readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        EnableMultipleHttp2Connections = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(15)
    });

    private readonly ConcurrentQueue<FrpTunnelLogItem> _logs = new();
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _engineCts;
    private Task? _runTask;
    private bool _isStopped = true;

    public string State { get; private set; } = "Disconnected";
    public string? PublicUrl { get; private set; }
    public string? SubdomainUrl { get; private set; }
    public string? LocalTargetUrl { get; private set; }
    public DateTime? ConnectedAt { get; private set; }
    public long SentBytes { get; private set; }
    public long ReceivedBytes { get; private set; }
    public string? LastError { get; private set; }

    public IReadOnlyList<FrpTunnelLogItem> GetRecentLogs() => _logs.ToArray();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (!_isStopped) return;
        _isStopped = false;
        _engineCts = new CancellationTokenSource();
        _runTask = Task.Run(() => RunLoopAsync(_engineCts.Token));
        await Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _isStopped = true;
        State = "Stopped";
        _engineCts?.Cancel();
        if (_ws != null)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client stopped", CancellationToken.None);
            }
            catch { }
            _ws.Dispose();
            _ws = null;
        }
        if (_runTask != null)
        {
            try { await _runTask; } catch { }
        }
        await configStore.UpdateStatusAsync(0, ConnectedAt, "手动停止");
        AddLog("INFO", "穿透隧道客户端已手动停止");
    }

    private async Task RunLoopAsync(CancellationToken stoppingToken)
    {
        var currentDelayMs = 1500;
        const int maxDelayMs = 30000;

        while (!stoppingToken.IsCancellationRequested && !_isStopped)
        {
            var config = await configStore.GetConfigAsync();
            if (string.IsNullOrWhiteSpace(config.ServerUrl) || string.IsNullOrWhiteSpace(config.TunnelHost))
            {
                State = "Disconnected";
                AddLog("WARN", "网关地址或隧道名称为空，等待配置...");
                await Task.Delay(5000, stoppingToken);
                continue;
            }

            LocalTargetUrl = config.LocalTargetUrl;
            try
            {
                State = "Connecting";
                AddLog("INFO", $"正在建立反向穿透长连接: {config.ServerUrl} (Host: {config.TunnelHost})");

                var wsUri = BuildConnectUri(config.ServerUrl, config.TunnelHost, config.ApiKey);
                _ws = new ClientWebSocket();
                if (!string.IsNullOrWhiteSpace(config.ApiKey))
                {
                    _ws.Options.SetRequestHeader("x-pyw-token", config.ApiKey.Trim());
                }

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                connectCts.CancelAfter(TimeSpan.FromSeconds(15));
                await _ws.ConnectAsync(wsUri, connectCts.Token);

                State = "Connected";
                ConnectedAt = DateTime.UtcNow;
                currentDelayMs = 1500; // 重置退避延迟
                LastError = null;
                await configStore.UpdateStatusAsync(1, ConnectedAt, null);
                AddLog("INFO", "反向穿透长连接建立成功，进入消息分发监听");

                // 启动心跳与报文接收
                using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeatTask = RunHeartbeatAsync(_ws, config.HeartbeatIntervalSeconds, sessionCts.Token);
                var receiveTask = RunReceiveLoopAsync(_ws, config, sessionCts.Token);

                await Task.WhenAny(heartbeatTask, receiveTask);
                sessionCts.Cancel();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _isStopped)
            {
                break;
            }
            catch (WebSocketException wsex) when (wsex.WebSocketErrorCode == WebSocketError.Faulted || _ws?.CloseStatus == (WebSocketCloseStatus)4002)
            {
                State = "Displaced";
                LastError = "同名客户端上线接管网关 (Code 4002)";
                AddLog("WARN", "⚠️ 检测到同名客户端上线接管网关，进入 15 秒防互踢静默期...");
                await configStore.UpdateStatusAsync(2, ConnectedAt, LastError);
                await Task.Delay(15000, stoppingToken);
            }
            catch (Exception ex)
            {
                State = "Reconnecting";
                LastError = ex.Message;
                AddLog("ERROR", $"长连接异常断开: {ex.Message}，将在 {currentDelayMs / 1000.0:F1} 秒后尝试重连...");
                await configStore.UpdateStatusAsync(3, ConnectedAt, ex.Message);
                await Task.Delay(currentDelayMs, stoppingToken);
                currentDelayMs = Math.Min((int)(currentDelayMs * 1.5), maxDelayMs);
            }
            finally
            {
                if (_ws != null)
                {
                    try { _ws.Dispose(); } catch { }
                    _ws = null;
                }
            }
        }
    }

    private async Task RunHeartbeatAsync(ClientWebSocket ws, int intervalSeconds, CancellationToken token)
    {
        var interval = TimeSpan.FromSeconds(intervalSeconds > 0 ? intervalSeconds : 15);
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            await Task.Delay(interval, token);
            try
            {
                var pingJson = $"{{\"type\":\"ping\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}";
                var pingBytes = Encoding.UTF8.GetBytes(pingJson);
                await ws.SendAsync(pingBytes.AsMemory(), WebSocketMessageType.Text, true, token);
            }
            catch { break; }
        }
    }

    private async Task RunReceiveLoopAsync(ClientWebSocket ws, FrpTunnelConfigEntity config, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            ValueWebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer.AsMemory(), token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (ws.CloseStatus == (WebSocketCloseStatus)4002)
                    {
                        throw new WebSocketException(WebSocketError.Faulted, "4002 Displaced");
                    }
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            ReceivedBytes += ms.Length;
            if (result.MessageType == WebSocketMessageType.Text)
            {
                var json = Encoding.UTF8.GetString(ms.ToArray());
                await HandleTextMessageAsync(ws, json, config, token);
            }
        }
    }

    private async Task HandleTextMessageAsync(ClientWebSocket ws, string json, FrpTunnelConfigEntity config, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("type", out var typeProp)) return;

        var type = typeProp.GetString();
        switch (type)
        {
            case "TUNNEL_CONNECTED":
            {
                var host = root.TryGetProperty("host", out var hP) ? hP.GetString() : config.TunnelHost;
                var pathModeUrl = root.TryGetProperty("pathModeUrl", out var pP) ? pP.GetString() : $"/tunnel/{host}/";
                PublicUrl = $"{config.ServerUrl.TrimEnd('/')}{pathModeUrl}";
                SubdomainUrl = FrpTunnelInstance.ResolveSubdomainUrl(config.ServerUrl, host ?? config.TunnelHost);
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
                _ = Task.Run(() => ForwardHttpRequestAsync(ws, root.Clone(), config, token), token);
                break;
            }
        }
    }

    private async Task ForwardHttpRequestAsync(ClientWebSocket ws, JsonElement reqFrame, FrpTunnelConfigEntity config, CancellationToken token)
    {
        var requestId = reqFrame.GetProperty("requestId").GetString()!;
        var method = reqFrame.GetProperty("method").GetString() ?? "GET";
        var path = reqFrame.TryGetProperty("path", out var pathP) ? pathP.GetString() : "/";
        var bodyB64 = reqFrame.TryGetProperty("body", out var bP) ? bP.GetString() : null;

        var targetBase = config.LocalTargetUrl.TrimEnd('/');
        var targetUri = new Uri($"{targetBase}{path}");

        using var httpRequest = new HttpRequestMessage(new HttpMethod(method), targetUri);

        // 处理并重写请求标头
        if (reqFrame.TryGetProperty("headers", out var headersObj))
        {
            foreach (var prop in headersObj.EnumerateObject())
            {
                var k = prop.Name;
                if (HopByHopHeaders.Contains(k)) continue;
                if (k.Equals("host", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.Equals("content-length", StringComparison.OrdinalIgnoreCase)) continue;
                if (k.Equals("accept-encoding", StringComparison.OrdinalIgnoreCase)) continue; // 避免未协商压缩

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
            using var httpResponse = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, token);
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
                var frameBytes = TunnelFrameSerializer.SerializeHttpResponse(requestId, statusCode, respHeaders, Convert.ToBase64String(bodyBytes));
                await SendBytesAsync(ws, frameBytes, token);
            }
            else
            {
                // Chunked Streaming 流式分片回传
                var startBytes = TunnelFrameSerializer.SerializeHttpResponseStart(requestId, statusCode, respHeaders);
                await SendBytesAsync(ws, startBytes, token);

                await using var stream = await httpResponse.Content.ReadAsStreamAsync(token);
                var chunk = new byte[64 * 1024];
                var reqIdBytes = Encoding.UTF8.GetBytes(requestId);

                int read;
                while ((read = await stream.ReadAsync(chunk.AsMemory(), token)) > 0)
                {
                    // 二进制帧: [ 0x01, reqIdLen, ...reqId, ...chunk ]
                    var frame = new byte[2 + reqIdBytes.Length + read];
                    frame[0] = 0x01;
                    frame[1] = (byte)reqIdBytes.Length;
                    Buffer.BlockCopy(reqIdBytes, 0, frame, 2, reqIdBytes.Length);
                    Buffer.BlockCopy(chunk, 0, frame, 2 + reqIdBytes.Length, read);

                    await ws.SendAsync(frame.AsMemory(), WebSocketMessageType.Binary, true, token);
                    SentBytes += frame.Length;
                }

                var endBytes = TunnelFrameSerializer.SerializeHttpResponseEnd(requestId);
                await SendBytesAsync(ws, endBytes, token);
            }
        }
        catch (Exception ex)
        {
            var errHeaders = new Dictionary<string, string> { ["content-type"] = "application/json; charset=utf-8" };
            var errBody = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"error\":\"Bad Gateway\",\"message\":\"{ex.Message}\"}}"));
            var errBytes = TunnelFrameSerializer.SerializeHttpResponse(requestId, 502, errHeaders, errBody);
            await SendBytesAsync(ws, errBytes, token);
        }
    }

    private async Task SendBytesAsync(ClientWebSocket ws, byte[] bytes, CancellationToken token)
    {
        await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
        SentBytes += bytes.Length;
    }

    private async Task SendTextAsync(ClientWebSocket ws, string json, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, token);
        SentBytes += bytes.Length;
    }

    private static Uri BuildConnectUri(string serverUrl, string host, string? key)
    {
        var ub = new UriBuilder(serverUrl.TrimEnd('/'));
        ub.Scheme = ub.Scheme == "https" ? "wss" : "ws";
        ub.Path = "/tunnel/connect";
        var query = $"host={Uri.EscapeDataString(host)}";
        if (!string.IsNullOrWhiteSpace(key))
        {
            query += $"&key={Uri.EscapeDataString(key)}&token={Uri.EscapeDataString(key)}";
        }
        ub.Query = query;
        return ub.Uri;
    }

    private void AddLog(string level, string message)
    {
        var item = new FrpTunnelLogItem(DateTime.UtcNow, level, message);
        _logs.Enqueue(item);
        while (_logs.Count > 100) _logs.TryDequeue(out _);
        if (level == "ERROR") logger.LogError("[FRP] {Message}", message);
        else logger.LogInformation("[FRP] {Message}", message);
    }
}
