using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LinuxWebTool.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Gateway;

/// <summary>
/// 高性能四层 UDP 端口转发引擎
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
        }

        StopAll();
        logger.LogInformation("[Gateway UDP] 四层端口转发引擎已停止");
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        var routes = (await store.GetAllTcpRoutesAsync())
            .Where(r => r.IsEnabled && r.Protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var desiredPorts = routes.ToDictionary(r => r.ListenPort);

        foreach (var port in _listeners.Keys.ToList())
        {
            if (!desiredPorts.ContainsKey(port))
            {
                if (_listeners.TryRemove(port, out var ctx))
                {
                    ctx.Cts.Cancel();
                    ctx.Client.Dispose();
                    logger.LogInformation("[Gateway UDP] 端口 {Port} 监听已注销", port);
                }
            }
        }

        foreach (var (port, route) in desiredPorts)
        {
            if (!_listeners.ContainsKey(port))
            {
                var cts = new CancellationTokenSource();
                try
                {
                    var client = new UdpClient(port);
                    var ctx = new UdpListenerContext(client, route.ForwardHost, route.ForwardPort, cts);
                    _listeners[port] = ctx;
                    _ = Task.Run(() => ForwardLoopAsync(ctx, cts.Token), cts.Token);
                    logger.LogInformation("[Gateway UDP] 端口 {Port} 转发至 {Host}:{ForwardPort} 已就绪", port, route.ForwardHost, route.ForwardPort);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[Gateway UDP] 端口 {Port} 启动监听失败", port);
                }
            }
        }
    }

    private async Task ForwardLoopAsync(UdpListenerContext ctx, CancellationToken token)
    {
        using var upstream = new UdpClient();
        while (!token.IsCancellationRequested)
        {
            try
            {
                var receiveResult = await ctx.Client.ReceiveAsync(token);
                await upstream.SendAsync(receiveResult.Buffer, receiveResult.Buffer.Length, ctx.ForwardHost, ctx.ForwardPort);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                logger.LogError(ex, "[Gateway UDP] 数据包转发异常");
            }
        }
    }

    private void StopAll()
    {
        foreach (var ctx in _listeners.Values)
        {
            try
            {
                ctx.Cts.Cancel();
                ctx.Client.Dispose();
            }
            catch { }
        }
        _listeners.Clear();
    }

    private sealed record UdpListenerContext(UdpClient Client, string ForwardHost, int ForwardPort, CancellationTokenSource Cts);
}
