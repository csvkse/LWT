using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

/// <summary>
/// 插件内部端口动态分配、双栈冲突探测与自动避让管理器。
/// </summary>
public sealed class PluginPortManager
{
    public const int MinPort = 18000;
    public const int MaxPort = 18999;

    public sealed record PortAllocationResult
    {
        public required int Port { get; init; }
        public bool WasAvoided { get; init; }
        public int? RequestedPort { get; init; }
        public string? ConflictReason { get; init; }
    }

    private readonly ConcurrentDictionary<string, PortAllocationResult> _allocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// 兼容旧调用入口：直接获取端口数字。
    /// </summary>
    public int AllocatePort(string pluginId, int? preferredPort = null)
    {
        return AllocatePortWithDetails(pluginId, preferredPort, "avoid").Port;
    }

    /// <summary>
    /// 完整分配入口：支持策略控制（avoid 避让 / strict 报错）并返回避让元数据。
    /// </summary>
    public PortAllocationResult AllocatePortWithDetails(string pluginId, int? preferredPort = null, string? conflictPolicy = "avoid")
    {
        lock (_lock)
        {
            // 1. 若当前插件已有记录且仍然可用，优先复用
            if (_allocations.TryGetValue(pluginId, out var existing))
            {
                var (isAvail, _) = CheckPort(existing.Port, pluginId);
                if (isAvail && (!preferredPort.HasValue || preferredPort.Value == existing.Port))
                {
                    return existing;
                }
            }

            // 2. 检查 preferredPort
            if (preferredPort.HasValue && preferredPort.Value >= 1024 && preferredPort.Value <= 65535)
            {
                var (isTargetAvail, occupiedBy) = CheckPort(preferredPort.Value, pluginId);
                if (isTargetAvail)
                {
                    var res = new PortAllocationResult
                    {
                        Port = preferredPort.Value,
                        WasAvoided = false,
                        RequestedPort = preferredPort.Value
                    };
                    _allocations[pluginId] = res;
                    return res;
                }

                // 若已被占用且策略为严格模式 (strict)，抛出异常阻止启动
                if (string.Equals(conflictPolicy, "strict", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"目标端口 {preferredPort.Value} 已被占用 ({occupiedBy})，且插件配置为严格冲突策略 (strict)，拒绝启动。");
                }

                // 避让模式 (avoid)：在动态池中自动搜索下一个可用端口
                for (var port = MinPort; port <= MaxPort; port++)
                {
                    if (port == preferredPort.Value) continue;
                    var (avail, _) = CheckPort(port, pluginId);
                    if (avail)
                    {
                        var res = new PortAllocationResult
                        {
                            Port = port,
                            WasAvoided = true,
                            RequestedPort = preferredPort.Value,
                            ConflictReason = $"目标端口 {preferredPort.Value} 已被占用 ({occupiedBy})，已自动避让至可用端口 {port}。"
                        };
                        _allocations[pluginId] = res;
                        return res;
                    }
                }

                throw new InvalidOperationException($"目标端口 {preferredPort.Value} 已被占用 ({occupiedBy})，且插件端口池 ({MinPort}-{MaxPort}) 已耗尽，无法自动避让。");
            }

            // 3. 未指定 preferredPort 时，从池中顺序分配
            for (var port = MinPort; port <= MaxPort; port++)
            {
                var (avail, _) = CheckPort(port, pluginId);
                if (avail)
                {
                    var res = new PortAllocationResult
                    {
                        Port = port,
                        WasAvoided = false,
                        RequestedPort = null
                    };
                    _allocations[pluginId] = res;
                    return res;
                }
            }

            throw new InvalidOperationException($"插件内部端口池 ({MinPort}-{MaxPort}) 已耗尽，无法分配新端口。");
        }
    }

    public void ReleasePort(string pluginId)
    {
        _allocations.TryRemove(pluginId, out _);
    }

    public int? GetPort(string pluginId)
    {
        return _allocations.TryGetValue(pluginId, out var alloc) ? alloc.Port : null;
    }

    public PortAllocationResult? GetAllocation(string pluginId)
    {
        return _allocations.TryGetValue(pluginId, out var alloc) ? alloc : null;
    }

    /// <summary>
    /// 全面双栈（IPv4 与 IPv6）及操作系统已监听端点检测。
    /// </summary>
    public (bool IsAvailable, string? OccupiedBy) CheckPort(int port, string? currentPluginId = null)
    {
        if (port < 1024 || port > 65535)
        {
            return (false, "端口超出有效范围 (1024-65535)");
        }

        // 1. 检查内部插件分配表
        foreach (var (pid, alloc) in _allocations)
        {
            if (alloc.Port == port && !string.Equals(pid, currentPluginId, StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"已被平台插件 [{pid}] 占用");
            }
        }

        // 2. 操作系统网络状态表检测 (System Table Check)
        try
        {
            var activeListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (activeListeners.Any(ep => ep.Port == port))
            {
                return (false, "已被操作系统或其他系统进程监听");
            }
        }
        catch
        {
            // 在受限容器环境下读取系统表可能抛出异常，降级到 Socket 试探
        }

        // 3. IPv4 独占试探绑定
        try
        {
            using var socketV4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socketV4.ExclusiveAddressUse = true;
            socketV4.Bind(new IPEndPoint(IPAddress.Any, port));
        }
        catch (SocketException ex)
        {
            return (false, $"IPv4 套接字绑定冲突 ({ex.SocketErrorCode})");
        }
        catch (Exception ex)
        {
            return (false, $"IPv4 探测异常: {ex.Message}");
        }

        // 4. IPv6 独占试探绑定
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                using var socketV6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                socketV6.DualMode = true;
                socketV6.ExclusiveAddressUse = true;
                socketV6.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            }
            catch (SocketException ex)
            {
                return (false, $"IPv6 套接字绑定冲突 ({ex.SocketErrorCode})");
            }
            catch (Exception ex)
            {
                return (false, $"IPv6 探测异常: {ex.Message}");
            }
        }

        return (true, null);
    }

    /// <summary>
    /// 获取建议的下一个可用端口。
    /// </summary>
    public int GetRecommendedPort(int? preferredPort = null)
    {
        var start = preferredPort.HasValue && preferredPort.Value >= MinPort && preferredPort.Value <= MaxPort
            ? preferredPort.Value + 1
            : MinPort;

        for (var port = start; port <= MaxPort; port++)
        {
            if (CheckPort(port).IsAvailable) return port;
        }

        for (var port = MinPort; port < start; port++)
        {
            if (CheckPort(port).IsAvailable) return port;
        }

        return MinPort;
    }
}
