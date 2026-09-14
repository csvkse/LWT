using LinuxWebTool.Contracts.Models;

namespace LinuxWebTool.Infrastructure.SystemInfo;

/// <summary>系统状态 API 只读该缓存，磁盘采集失败或超时不阻塞请求。</summary>
public interface IDiskStatusCache
{
    IReadOnlyList<DiskStatus> GetSnapshot();
}

public sealed class DiskStatusCache : IDiskStatusCache
{
    private readonly object _sync = new();
    private IReadOnlyList<DiskStatus> _snapshot = [];

    public IReadOnlyList<DiskStatus> GetSnapshot()
    {
        lock (_sync)
        {
            return _snapshot;
        }
    }

    public void SetSnapshot(IReadOnlyList<DiskStatus> snapshot)
    {
        lock (_sync)
        {
            _snapshot = snapshot;
        }
    }
}
