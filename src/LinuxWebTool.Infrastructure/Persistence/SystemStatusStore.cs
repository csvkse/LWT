using LinuxWebTool.Contracts.Models;
using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>系统状态快照仓储（写入 / 时间窗口查询 / 过期清理）。</summary>
public class SystemStatusStore(ISqlSugarClient db)
{
    public async Task InsertAsync(SystemStatusSnapshot snapshot)
    {
        snapshot.Time = DateTime.Now;
        await db.Insertable(snapshot).ExecuteCommandAsync();
    }

    /// <summary>以显式时间戳插入（与进程 / 磁盘 / 网络快照共用同一采样时刻）。</summary>
    public async Task InsertAsync(SystemStatusSnapshot snapshot, DateTime time)
    {
        snapshot.Time = time;
        await db.Insertable(snapshot).ExecuteCommandAsync();
    }

    /// <summary>查询最近 hours 小时的序列点（时间正序，供曲线渲染）。</summary>
    public async Task<List<StatusSnapshotPoint>> QueryAsync(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTime.Now.AddHours(-hours);
        var rows = await db.Queryable<SystemStatusSnapshot>()
            .Where(s => s.Time >= since)
            .OrderBy(s => s.Time)
            .ToListAsync();
        return rows.Select(ToPoint).ToList();
    }

    public Task ClearOlderThan(DateTime cutoff)
    {
        return db.Deleteable<SystemStatusSnapshot>().Where(s => s.Time < cutoff).ExecuteCommandAsync();
    }

    /// <summary>
    /// 判断最近 window 内是否存在任一资源越阈值的整机快照（异常窗口判定）。
    /// 整机快照按 SamplingIntervalSeconds 采样，足以反映 CPU/内存/磁盘/网络是否越阈。
    /// </summary>
    public async Task<bool> IsRecentAboveAsync(
        TimeSpan window,
        double cpuThreshold,
        double memThreshold,
        double diskThreshold,
        long netThresholdBps)
    {
        var since = DateTime.Now.Add(-window);
        var rows = await db.Queryable<SystemStatusSnapshot>()
            .Where(s => s.Time >= since)
            .ToListAsync();
        return rows.Any(s =>
            s.CpuUsage >= cpuThreshold ||
            s.MemUsage >= memThreshold ||
            (diskThreshold > 0 && s.DiskRootUsage >= diskThreshold) ||
            (netThresholdBps > 0 && (s.NetSentBps >= netThresholdBps || s.NetRecvBps >= netThresholdBps)));
    }

    private static StatusSnapshotPoint ToPoint(SystemStatusSnapshot s) => new()
    {
        Time = s.Time,
        CpuUsage = s.CpuUsage,
        Load1 = s.Load1,
        MemUsage = s.MemUsage,
        DiskRootUsage = s.DiskRootUsage,
        NetSentBps = s.NetSentBps,
        NetRecvBps = s.NetRecvBps,
    };
}
