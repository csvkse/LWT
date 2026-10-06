using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LinuxWebTool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Gateway;

/// <summary>
/// 高性能四层 TCP 端口转发引擎（基于非阻塞 Socket + ArrayPool 零分配转发）
/// </summary>
public sealed class TcpProxyEngine(GatewayStore store, ILogger<TcpProxyEngine> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, TcpListenerContext> _listeners = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("[Gateway TCP] 四层端口转发引擎已启动");
        await ReloadAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(10000, stoppingToken);
        }

        StopAll();
        logger.LogInformation("[Gateway TCP] 四层端口转发引擎已停止");
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var routes = (await store.GetAllTcpRoutesAsync())
            .Where(r => r.IsEnabled && r.Protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var desiredPorts = routes.ToDictionary(r => r.ListenPort);

        // 1. 停用不再需要的监听器
        foreach (var port in _listeners.Keys.ToList())
        {
            if (!desiredPorts.ContainsKey(port))
            {
                if (_listeners.TryRemove(port, out var ctx))
                {
                    ctx.Cts.Cancel();
                    ctx.Listener.Stop();
                    logger.LogInformation("[Gateway TCP] 端口 {Port} 监听已注销", port);
                }
            }
        }

        // 2. 启动新监听器
        foreach (var (port, route) in desiredPorts)
        {
            if (!_listeners.ContainsKey(port))
            {
                var cts = new CancellationTokenSource();
                var listener = new TcpListener(IPAddress.Any, port);
                try
                {
                    listener.Start();
                    var ctx = new TcpListenerContext(listener, route.ForwardHost, route.ForwardPort, cts);
                    _listeners[port] = ctx;
                    _ = Task.Run(() => AcceptLoopAsync(ctx, cts.Token), cts.Token);
                    logger.LogInformation("[Gateway TCP] 端口 {Port} 转发至 {Host}:{ForwardPort} 已就绪", port, route.ForwardHost, route.ForwardPort);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Gateway TCP] 端口 {Port} 启动监听失败", port);
                }
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
                _ = Task.Run(() => ForwardClientAsync(clientSocket, ctx.ForwardHost, ctx.ForwardPort, token), token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Gateway TCP] Accept 异常");
            }
        }
    }

    private async Task ForwardClientAsync(Socket clientSocket, string forwardHost, int forwardPort, CancellationToken token)
    {
        using var client = clientSocket;
        using var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await upstream.ConnectAsync(forwardHost, forwardPort, token);
            using var clientStream = new NetworkStream(client, false);
            using var upstreamStream = new NetworkStream(upstream, false);

            var t1 = CopyStreamAsync(clientStream, upstreamStream, token);
            var t2 = CopyStreamAsync(upstreamStream, clientStream, token);
            await Task.WhenAny(t1, t2);
        }
        catch { }
    }

    private static async Task CopyStreamAsync(NetworkStream src, NetworkStream dst, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            int read;
            while ((read = await src.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), token);
            }
        }
        catch { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
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
    }

    private sealed record TcpListenerContext(TcpListener Listener, string ForwardHost, int ForwardPort, CancellationTokenSource Cts);
}
