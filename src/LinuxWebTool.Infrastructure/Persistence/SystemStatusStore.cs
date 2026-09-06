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
