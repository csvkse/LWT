using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LinuxWebTool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Gateway;

/// <summary>
/// 高性能四层 UDP 端口转发引擎（支持双向数据报转发与 NAT 会话跟踪）
/// </summary>
public sealed class UdpProxyEngine(GatewayStore store, ILogger<UdpProxyEngine> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, UdpListenerContext> _listeners = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[Gateway UDP] 四层端口转发引擎已启动");
        await ReloadAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10000, stoppingToken);
            PruneAllIdleSessions();
        }

        StopAll();
        logger.LogInformation("[Gateway UDP] 四层端口转发引擎已停止");
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var routes = (await store.GetAllTcpRoutesAsync())
            .Where(r => r.IsEnabled && r.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.ListenPort)
            .ToDictionary(g => g.Key, g => g.First());

        var desiredPorts = routes;

        // 1. 停用不再需要的监听器
        foreach (var port in _listeners.Keys.ToList())
        {
            if (!desiredPorts.ContainsKey(port))
            {
                if (_listeners.TryRemove(port, out var ctx))
                {
                    ctx.Dispose();
                    logger.LogInformation("[Gateway UDP] 端口 {Port} 监听已注销", port);
                }
            }
        }

        // 2. 启动新监听器 或 更新既有监听器的目标配置
        foreach (var (port, route) in desiredPorts)
        {
            if (_listeners.TryGetValue(port, out var existing))
            {
                if (existing.ForwardHost != route.ForwardHost || existing.ForwardPort != route.ForwardPort)
                {
                    existing.UpdateTarget(route.ForwardHost, route.ForwardPort);
                    logger.LogInformation("[Gateway UDP] 端口 {Port} 转发目标已热更新为 {Host}:{ForwardPort}", port, route.ForwardHost, route.ForwardPort);
                }
            }
            else
            {
                try
                {
                    var ctx = new UdpListenerContext(port, route.ForwardHost, route.ForwardPort, logger);
                    _listeners[port] = ctx;
                    ctx.Start();
                    logger.LogInformation("[Gateway UDP] 端口 {Port} 转发至 {Host}:{ForwardPort} 已就绪", port, route.ForwardHost, route.ForwardPort);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Gateway UDP] 端口 {Port} 启动监听失败", port);
                }
            }
        }
    }

    private void PruneAllIdleSessions()
    {
        foreach (var ctx in _listeners.Values)
        {
            ctx.PruneIdleSessions(TimeSpan.FromSeconds(60));
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        StopAll();
        return base.StopAsync(cancellationToken);
    }

    private void StopAll()
    {
        foreach (var ctx in _listeners.Values)
        {
            try { ctx.Dispose(); } catch { }
        }
        _listeners.Clear();
    }
}

internal sealed class UdpListenerContext : IDisposable
{
    private readonly Socket _listenerSocket;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<EndPoint, UdpSession> _sessions = new();

    public int Port { get; }
    public string ForwardHost { get; private set; }
    public int ForwardPort { get; private set; }
    private IPAddress? _cachedTargetIp;
    private DateTime _dnsResolvedTime = DateTime.MinValue;
    private static readonly TimeSpan DnsCacheTtl = TimeSpan.FromMinutes(5);

    public UdpListenerContext(int port, string forwardHost, int forwardPort, ILogger logger)
    {
        Port = port;
        ForwardHost = forwardHost;
        ForwardPort = forwardPort;
        _logger = logger;

        _listenerSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _listenerSocket.Bind(new IPEndPoint(IPAddress.Any, port));
    }

    public void UpdateTarget(string forwardHost, int forwardPort)
    {
        ForwardHost = forwardHost;
        ForwardPort = forwardPort;
        _cachedTargetIp = null;
        _dnsResolvedTime = DateTime.MinValue;
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
    }

    private async ValueTask<IPAddress?> ResolveTargetIpAsync(CancellationToken token)
    {
        if (IPAddress.TryParse(ForwardHost, out var directIp))
        {
            return directIp;
        }

        var now = DateTime.UtcNow;
        if (_cachedTargetIp != null && now - _dnsResolvedTime < DnsCacheTtl)
        {
            return _cachedTargetIp;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(ForwardHost, token);
            var target = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6);
            if (target != null)
            {
                _cachedTargetIp = target;
                _dnsResolvedTime = now;
            }
            return target;
        }
        catch
        {
            return _cachedTargetIp;
        }
    }

    public void Start()
    {
        _ = Task.Run(ReceiveLoopAsync, _cts.Token);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await _listenerSocket.ReceiveFromAsync(
                        new ArraySegment<byte>(buffer), SocketFlags.None,
                        new IPEndPoint(IPAddress.Any, 0), _cts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (Exception ex)
                {
                    if (_cts.Token.IsCancellationRequested) break;
                    _logger.LogError(ex, "[Gateway UDP] 端口 {Port} 接收异常", Port);
                    continue;
                }

                if (result.ReceivedBytes <= 0) continue;

                var clientEp = result.RemoteEndPoint;
                if (!_sessions.TryGetValue(clientEp, out var session))
                {
                    try
                    {
                        var targetAddress = await ResolveTargetIpAsync(_cts.Token);
                        if (targetAddress == null)
                        {
                            _logger.LogWarning("[Gateway UDP] 无法解析目标主机 {Host}", ForwardHost);
                            continue;
                        }

                        var newSession = new UdpSession(clientEp, targetAddress, ForwardPort, _listenerSocket, _logger);
                        if (_sessions.TryAdd(clientEp, newSession))
                        {
                            newSession.Start();
                            session = newSession;
                        }
                        else
                        {
                            newSession.Dispose();
                            _sessions.TryGetValue(clientEp, out session);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Gateway UDP] 创建目标端点会话异常");
                        continue;
                    }
                }

                if (session != null)
                {
                    session.LastActiveAt = DateTime.UtcNow;
                    try
                    {
                        await session.BackendSocket.SendToAsync(
                            new ArraySegment<byte>(buffer, 0, result.ReceivedBytes),
                            SocketFlags.None, session.BackendEndPoint, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[Gateway UDP] 发送数据至上游异常");
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void PruneIdleSessions(TimeSpan timeout)
    {
        var now = DateTime.UtcNow;
        foreach (var (ep, s) in _sessions)
        {
            if (now - s.LastActiveAt > timeout && _sessions.TryRemove(ep, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var s in _sessions.Values) s.Dispose();
        _sessions.Clear();
        try { _listenerSocket.Dispose(); } catch { }
    }
}

internal sealed class UdpSession : IDisposable
{
    private readonly Socket _listenerSocket;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();

    public EndPoint ClientEndPoint { get; }
    public Socket BackendSocket { get; }
    public EndPoint BackendEndPoint { get; }
    public DateTime LastActiveAt { get; set; }

    public UdpSession(EndPoint clientEp, IPAddress backendAddress, int backendPort, Socket listenerSocket, ILogger logger)
    {
        ClientEndPoint = clientEp;
        _listenerSocket = listenerSocket;
        _logger = logger;
        LastActiveAt = DateTime.UtcNow;

        BackendSocket = new Socket(backendAddress.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        BackendSocket.Bind(new IPEndPoint(backendAddress.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
        BackendEndPoint = new IPEndPoint(backendAddress, backendPort);
    }

    public void Start()
    {
        _ = Task.Run(async () =>
        {
            var buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                while (!_cts.Token.IsCancellationRequested)
                {
                    SocketReceiveFromResult result;
                    try
                    {
                        var anyEp = BackendSocket.AddressFamily == AddressFamily.InterNetworkV6
                            ? new IPEndPoint(IPAddress.IPv6Any, 0)
                            : new IPEndPoint(IPAddress.Any, 0);
                        result = await BackendSocket.ReceiveFromAsync(
                            new ArraySegment<byte>(buffer), SocketFlags.None,
                            anyEp, _cts.Token);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (Exception ex)
                    {
                        if (_cts.Token.IsCancellationRequested) break;
                        _logger.LogError(ex, "[Gateway UDP] 会话接收上游响应异常");
                        break;
                    }

                    if (result.ReceivedBytes <= 0) continue;
                    LastActiveAt = DateTime.UtcNow;

                    try
                    {
                        await _listenerSocket.SendToAsync(
                            new ArraySegment<byte>(buffer, 0, result.ReceivedBytes),
                            SocketFlags.None, ClientEndPoint, _cts.Token);
                    }
                    catch (Exception ex)
                    {
                        if (_cts.Token.IsCancellationRequested) break;
                        _logger.LogError(ex, "[Gateway UDP] 会话转发响应至客户端异常");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }, _cts.Token);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { BackendSocket.Dispose(); } catch { }
    }
}
