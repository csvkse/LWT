using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace LinuxWebTool.Infrastructure.Features.Workers.Supervisor;

/// <summary>
/// Worker 内部端口动态分配与冲突探测管理器。
/// </summary>
public sealed class WorkerPortManager
{
    private const int MinPort = 18000;
    private const int MaxPort = 18999;
    private readonly ConcurrentDictionary<string, int> _workerPortMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    /// <summary>
    /// 为指定 Worker 分配一个未被占用且可用的本地端口。
    /// </summary>
    public int AllocatePort(string workerId, int? preferredPort = null)
    {
        lock (_lock)
        {
            if (_workerPortMap.TryGetValue(workerId, out var existingPort))
            {
                if (IsPortAvailable(existingPort)) return existingPort;
            }

            if (preferredPort.HasValue && preferredPort.Value >= 1024 && preferredPort.Value <= 65535)
            {
                if (IsPortAvailable(preferredPort.Value))
                {
                    _workerPortMap[workerId] = preferredPort.Value;
                    return preferredPort.Value;
                }
            }

            // 从端口池中寻找未占用端口
            for (var port = MinPort; port <= MaxPort; port++)
            {
                if (!_workerPortMap.Values.Contains(port) && IsPortAvailable(port))
                {
                    _workerPortMap[workerId] = port;
                    return port;
                }
            }

            throw new InvalidOperationException($"Worker 内部端口池 ({MinPort}-{MaxPort}) 已耗尽，无法分配新端口。");
        }
    }

    /// <summary>
    /// 释放指定 Worker 的已分配端口。
    /// </summary>
    public void ReleasePort(string workerId)
    {
        _workerPortMap.TryRemove(workerId, out _);
    }

    /// <summary>
    /// 获取当前 Worker 的绑定端口（若有）。
    /// </summary>
    public int? GetPort(string workerId)
    {
        return _workerPortMap.TryGetValue(workerId, out var port) ? port : null;
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
