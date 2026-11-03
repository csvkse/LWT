namespace LinuxWebTool.WebHost.Features.Plugins.Ingress;

using LinuxWebTool.Contracts.Features.Plugins.Contracts;

/// <summary>
/// 处于运行状态且激活了 Ingress 规则的活跃路由项（预解析有效路径与消歧元数据）。
/// 纯内存持有，用于数据面极速无锁匹配。
/// </summary>
public sealed record ActiveIngressRoute(
    PluginInstanceDto Plugin,
    PluginIngressRoute Route,
    string EffectivePath,
    bool IsExact);

/// <summary>
/// 响应式纯内存 Ingress 路由表（数据面与控制面完全解耦）。
/// 读端无锁纳秒级并发访问，写端由插件状态机与清单变更事件异步驱动（RCU 读写分离模式）。
/// </summary>
public interface IIngressRoutingTable
{
    /// <summary>
    /// 获取当前生效的活跃路由快照（无锁只读引用，耗时 < 1ns）。
    /// </summary>
    IReadOnlyList<ActiveIngressRoute> GetActiveRoutes();

    /// <summary>
    /// 触发异步重构并原子替换内存路由快照。
    /// </summary>
    Task RefreshAsync(CancellationToken ct = default);
}
