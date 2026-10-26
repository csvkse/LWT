using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LinuxWebTool.Contracts.Features.Gateway.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Gateway.Adapters;

/// <summary>
/// 高性能四层 TCP 端口转发引擎（支持双栈监听、自愈重连、ArrayPool 零分配转发与实时可观测状态）
/// </summary>
public sealed class TcpProxyEngine(GatewayStore store, ILogger<TcpProxyEngine> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, TcpListenerContext> _listeners = new();
    private readonly ConcurrentDictionary<int, L4RuntimeState> _statusMap = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[Gateway TCP] 四层端口转发引擎已启动 (Dual-Stack 就绪)");
        await ReloadAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(10000, stoppingToken);
                await HealFailedListenersAsync(stoppingToken);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Gateway TCP] 巡检自愈过程出现异常");
            }
        }

        StopAll();
        logger.LogInformation("[Gateway TCP] 四层端口转发引擎已停止");
    }

    /// <summary>
    /// 获取指定端口的实时运行状态（供 API 与前端观测仪表盘消费）
    /// </summary>
    public L4RuntimeState GetRuntimeStatus(int port)
    {
        if (_listeners.TryGetValue(port, out var ctx))
        {
            return new L4RuntimeState(
                IsRunning: true,
                LastError: null,
                ActiveConnections: ctx.ActiveConnections,
                BoundAddress: ctx.BoundAddress);
        }

        if (_statusMap.TryGetValue(port, out var state))
        {
            return state;
        }

        return new L4RuntimeState(
            IsRunning: false,
            LastError: null,
            ActiveConnections: 0,
            BoundAddress: null);
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            var routes = (await store.GetAllTcpRoutesAsync())
                .Where(r => r.IsEnabled && r.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
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
                        ctx.Cts.Cancel();
                        try { ctx.Listener.Stop(); } catch { }
                        logger.LogInformation("[Gateway TCP] 端口 {Port} 监听已注销", port);
                    }
                    _statusMap.TryRemove(port, out _);
                }
            }

            // 2. 启动新监听器 或 更新既有监听器的目标配置
            foreach (var (port, route) in desiredPorts)
            {
                if (_listeners.TryGetValue(port, out var existing))
                {
                    if (existing.ForwardHost != route.ForwardHost || existing.ForwardPort != route.ForwardPort)
                    {
                        existing.ForwardHost = route.ForwardHost;
                        existing.ForwardPort = route.ForwardPort;
                        logger.LogInformation("[Gateway TCP] 端口 {Port} 转发目标已热更新为 {Host}:{ForwardPort}", port, route.ForwardHost, route.ForwardPort);
                    }
                    _statusMap[port] = new L4RuntimeState(true, null, existing.ActiveConnections, existing.BoundAddress);
                }
                else
                {
                    StartListener(route);
                }
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    private void StartListener(GatewayTcpRouteEntity route)
    {
        var port = route.ListenPort;
        var (listener, boundAddress, createEx) = CreateDualStackListener(port);
        if (listener == null || createEx != null)
        {
            var err = $"套接字创建失败: {createEx?.Message ?? "未知错误"}";
            _statusMap[port] = new L4RuntimeState(false, err, 0, null);
            logger.LogError(createEx, "[Gateway TCP] 端口 {Port} 创建套接字失败", port);
            return;
        }

        var cts = new CancellationTokenSource();
        try
        {
            listener.Start();
            var ctx = new TcpListenerContext(listener, route.ForwardHost, route.ForwardPort, boundAddress, cts);
            _listeners[port] = ctx;
            _statusMap[port] = new L4RuntimeState(true, null, 0, boundAddress);
            _ = Task.Run(() => AcceptLoopAsync(ctx, cts.Token), cts.Token);
            logger.LogInformation("[Gateway TCP] 端口 {Port} (绑定 {Bound}) 转发至 {Host}:{ForwardPort} 已就绪", port, boundAddress, route.ForwardHost, route.ForwardPort);
        }
        catch (Exception ex)
        {
            try { listener.Stop(); } catch { }
            var err = $"启动监听失败: {ex.Message}";
            _statusMap[port] = new L4RuntimeState(false, err, 0, boundAddress);
            logger.LogError(ex, "[Gateway TCP] 端口 {Port} 启动监听失败 ({Error})", port, ex.Message);
        }
    }

    private static (TcpListener? listener, string boundAddress, Exception? ex) CreateDualStackListener(int port)
    {
        try
        {
            // 优先创建 IPv6 Dual-Stack 监听器，同时接收 IPv4 (IPv4-mapped) 和 IPv6 连接
            var listener = TcpListener.Create(port);
            return (listener, "[::]", null);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressFamilyNotSupported || ex.SocketErrorCode == SocketError.ProtocolNotSupported)
        {
            // 宿主内核若彻底关闭 IPv6 (ipv6.disable=1)，安全降级为纯 IPv4 Any
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                return (listener, "0.0.0.0", null);
            }
            catch (Exception fallbackEx)
            {
                return (null, "0.0.0.0", fallbackEx);
            }
        }
        catch (Exception ex)
        {
            return (null, "unknown", ex);
        }
    }

    private async Task HealFailedListenersAsync(CancellationToken stoppingToken)
    {
        var routes = (await store.GetAllTcpRoutesAsync())
            .Where(r => r.IsEnabled && r.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var route in routes)
        {
            if (stoppingToken.IsCancellationRequested) break;
            if (!_listeners.ContainsKey(route.ListenPort))
            {
                logger.LogDebug("[Gateway TCP] 触发端口 {Port} 自愈重连尝试", route.ListenPort);
                StartListener(route);
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListenerContext ctx, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var clientSocket = await ctx.Listener.AcceptSocketAsync(token);
                _ = Task.Run(() => ForwardClientAsync(clientSocket, ctx, token), token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) break;
                logger.LogError(ex, "[Gateway TCP] Accept 异常 (端口 {Port})", ctx.Listener.LocalEndpoint);
            }
        }
    }

    private async Task ForwardClientAsync(Socket clientSocket, TcpListenerContext ctx, CancellationToken token)
    {
        ctx.IncrementConnection();
        Socket? upstream = null;
        try
        {
            clientSocket.NoDelay = true;

            // 解析目标主机并建立连接（支持 IPv4 / IPv6 / localhost / 域名候选列表串行重试）
            upstream = await ConnectUpstreamAsync(ctx.ForwardHost, ctx.ForwardPort, token);
            if (upstream == null)
            {
                logger.LogWarning("[Gateway TCP] 无法连接到上游目标 {Host}:{Port}", ctx.ForwardHost, ctx.ForwardPort);
                return;
            }

            upstream.NoDelay = true;

            using var clientStream = new NetworkStream(clientSocket, false);
            using var upstreamStream = new NetworkStream(upstream, false);

            using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var relayToken = relayCts.Token;

            var clientToUpstream = RelayDirectionAsync(clientStream, upstreamStream, clientSocket, upstream, relayCts, relayToken);
            var upstreamToClient = RelayDirectionAsync(upstreamStream, clientStream, upstream, clientSocket, relayCts, relayToken);

            // 任意一端断开或异常，立即结束双向转发，释放资源
            await Task.WhenAny(clientToUpstream, upstreamToClient);
            try { relayCts.Cancel(); } catch { }
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                logger.LogDebug(ex, "[Gateway TCP] 连接转发异常: {Host}:{Port}", ctx.ForwardHost, ctx.ForwardPort);
            }
        }
        finally
        {
            ctx.DecrementConnection();
            SafeClose(clientSocket);
            SafeClose(upstream);
        }
    }

    private static async Task<Socket?> ConnectUpstreamAsync(string forwardHost, int forwardPort, CancellationToken token)
    {
        IPAddress[] targetIps;
        if (IPAddress.TryParse(forwardHost, out var directIp))
        {
            targetIps = [directIp];
        }
        else
        {
            try
            {
                targetIps = await Dns.GetHostAddressesAsync(forwardHost, token);
            }
            catch
            {
                return null;
            }
        }

        if (targetIps.Length == 0) return null;

        foreach (var ip in targetIps)
        {
            if (token.IsCancellationRequested) break;
            var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(new IPEndPoint(ip, forwardPort), connectCts.Token);
                return socket;
            }
            catch
            {
                SafeClose(socket);
            }
        }

        return null;
    }

    private static async Task RelayDirectionAsync(
        NetworkStream srcStream,
        NetworkStream dstStream,
        Socket srcSocket,
        Socket dstSocket,
        CancellationTokenSource relayCts,
        CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            int read;
            while ((read = await srcStream.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                await dstStream.WriteAsync(buffer.AsMemory(0, read), token);
            }

            try
            {
                dstSocket.Shutdown(SocketShutdown.Send);
            }
            catch { }
        }
        catch
        {
            try { relayCts.Cancel(); } catch { }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void SafeClose(Socket? socket)
    {
        if (socket == null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        try { socket.Close(); } catch { }
        try { socket.Dispose(); } catch { }
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
            try
            {
                ctx.Cts.Cancel();
                ctx.Listener.Stop();
            }
            catch { }
        }
        _listeners.Clear();
        _statusMap.Clear();
    }

    private sealed class TcpListenerContext(
        TcpListener listener,
        string forwardHost,
        int forwardPort,
        string boundAddress,
        CancellationTokenSource cts)
    {
        public TcpListener Listener { get; } = listener;
        public string ForwardHost { get; set; } = forwardHost;
        public int ForwardPort { get; set; } = forwardPort;
        public string BoundAddress { get; set; } = boundAddress;
        public CancellationTokenSource Cts { get; } = cts;
        private int _activeConnections;
        public int ActiveConnections => Volatile.Read(ref _activeConnections);
        public void IncrementConnection() => Interlocked.Increment(ref _activeConnections);
        public void DecrementConnection() => Interlocked.Decrement(ref _activeConnections);
    }
}
