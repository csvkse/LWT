namespace LinuxWebTool.WebHost.Features.Plugins.Ingress;

using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using Microsoft.Extensions.Logging;

/// <summary>
/// 纯内存无锁响应式 Ingress 路由表实现。
/// 采用 RCU（Read-Copy-Update）模式：
/// - 读端：高频无锁并发读取不可变只读列表引用；
/// - 写端：由状态机与清单变更事件驱动，生成新列表后通过原子指针赋值替换。
/// </summary>
public sealed class ReactiveIngressRoutingTable(
    IPluginManager pluginManager,
    ILogger<ReactiveIngressRoutingTable> logger) : IIngressRoutingTable
{
    private volatile IReadOnlyList<ActiveIngressRoute> _activeRoutes = [];
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public IReadOnlyList<ActiveIngressRoute> GetActiveRoutes() => _activeRoutes;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            var plugins = await pluginManager.GetAllAsync(profileFilter: null, ct);
            var list = new List<ActiveIngressRoute>();

            foreach (var plugin in plugins)
            {
                if (plugin.State != PluginState.Running)
                    continue;

                var ingress = plugin.Manifest.Ingress;
                if (ingress == null || !ingress.Enabled || ingress.Routes.Count == 0)
                    continue;

                var isStealth = string.Equals(ingress.Scope, "stealth", StringComparison.OrdinalIgnoreCase);

                foreach (var route in ingress.Routes)
                {
                    var effectivePaths = PluginIngressMiddleware.ResolveEffectivePaths(plugin.Id, route.Match, isStealth, logger);
                    if (effectivePaths.Count == 0) continue;

                    foreach (var (effectivePath, isExact) in effectivePaths)
                    {
                        list.Add(new ActiveIngressRoute(plugin, route, effectivePath, isExact));
                    }
                }
            }

            // 原子替换引用 (RCU)
            _activeRoutes = list;
            logger.LogInformation("[ReactiveIngressRoutingTable] 路由表快照已刷新，当前活跃路由条目数: {Count}", list.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ReactiveIngressRoutingTable] 刷新路由表内存快照异常");
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
