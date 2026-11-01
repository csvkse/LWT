using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

/// <summary>
/// 插件内部端口动态分配与冲突探测管理器。
/// </summary>
public sealed class PluginPortManager
{
    private const int MinPort = 18000;
    private const int MaxPort = 18999;
    private readonly ConcurrentDictionary<string, int> _pluginPortMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public int AllocatePort(string pluginId, int? preferredPort = null)
    {
        lock (_lock)
        {
            if (_pluginPortMap.TryGetValue(pluginId, out var existingPort))
            {
                if (IsPortAvailable(existingPort)) return existingPort;
            }

            if (preferredPort.HasValue && preferredPort.Value >= 1024 && preferredPort.Value <= 65535)
            {
                if (IsPortAvailable(preferredPort.Value))
                {
                    _pluginPortMap[pluginId] = preferredPort.Value;
                    return preferredPort.Value;
                }
            }

            for (var port = MinPort; port <= MaxPort; port++)
            {
                if (!_pluginPortMap.Values.Contains(port) && IsPortAvailable(port))
                {
                    _pluginPortMap[pluginId] = port;
                    return port;
                }
            }

            throw new InvalidOperationException($"插件内部端口池 ({MinPort}-{MaxPort}) 已耗尽，无法分配新端口。");
        }
    }

    public void ReleasePort(string pluginId)
    {
        _pluginPortMap.TryRemove(pluginId, out _);
    }

    public int? GetPort(string pluginId)
    {
        return _pluginPortMap.TryGetValue(pluginId, out var port) ? port : null;
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
