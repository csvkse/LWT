namespace LinuxWebTool.WebHost.Features.Plugins.Ingress;

using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// 后台 Ingress 路由表响应式同步服务。
/// 监听 IPluginManager 的状态机跃迁与清单变更事件，触发 IIngressRoutingTable 内存快照热重载；
/// 并通过周期性心跳对齐保证状态机与路由快照的最终一致性。
/// </summary>
public sealed class IngressRoutingSyncService(
    IPluginManager pluginManager,
    IIngressRoutingTable routingTable,
    ILogger<IngressRoutingSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnChanged()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    logger.LogDebug("[IngressSync] 收到插件生命周期变更事件，触发路由表重载");
                    await routingTable.RefreshAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "[IngressSync] 事件驱动刷新路由表异常");
                }
            }, stoppingToken);
        }

        pluginManager.OnPluginStateOrManifestChanged += OnChanged;

        try
        {
            // 1. 启动时执行一次基准快照加载
            await routingTable.RefreshAsync(stoppingToken);

            // 2. 周期性心跳巡检与状态对齐 (Reconciliation)
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken))
            {
                await routingTable.RefreshAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 正常关机
        }
        finally
        {
            pluginManager.OnPluginStateOrManifestChanged -= OnChanged;
        }
    }
}
