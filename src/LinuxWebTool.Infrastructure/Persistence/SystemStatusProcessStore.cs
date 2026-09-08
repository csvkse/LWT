using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>系统状态进程序列历史快照仓储（写入 / 时间窗口查询 / 过期清理）。</summary>
public class SystemStatusProcessStore(ISqlSugarClient db)
{
    public async Task InsertAsync(IEnumerable<SystemStatusProcessSnapshot> snapshots)
    {
        var batch = snapshots as SystemStatusProcessSnapshot[] ?? snapshots.ToArray();
        if (batch.Length > 0)
        {
            await db.Insertable(batch).ExecuteCommandAsync();
        }
    }

    /// <summary>查询最近 hours 小时的进程序列点（时间正序，供曲线 / 明细渲染）。</summary>
    public async Task<List<ProcessSnapshotPoint>> QueryAsync(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTime.Now.AddHours(-hours);
        var rows = await db.Queryable<SystemStatusProcessSnapshot>()
            .Where(s => s.Time >= since)
            .OrderBy(s => s.Time)
            .ToListAsync();
        return rows.Select(ToPoint).ToList();
    }

    /// <summary>查询指定时间近邻（前后 tolerance 分钟）内的进程序列点，用于展开某个采样点明细。</summary>
    public async Task<List<ProcessSnapshotPoint>> QueryAroundAsync(DateTime time, int toleranceMinutes = 5)
    {
        var since = time.AddMinutes(-toleranceMinutes);
        var until = time.AddMinutes(toleranceMinutes);
        var rows = await db.Queryable<SystemStatusProcessSnapshot>()
            .Where(s => s.Time >= since && s.Time <= until)
            .OrderBy(s => s.Time)
            .ToListAsync();
        return rows.Select(ToPoint).ToList();
    }

    public Task ClearOlderThan(DateTime cutoff)
    {
        return db.Deleteable<SystemStatusProcessSnapshot>().Where(s => s.Time < cutoff).ExecuteCommandAsync();
    }

    private static ProcessSnapshotPoint ToPoint(SystemStatusProcessSnapshot s) => new()
    {
        Time = s.Time,
        Pid = s.Pid,
        Name = s.Name,
        CpuPercent = s.CpuPercent,
        MemPercent = s.MemPercent,
        MemBytes = s.MemBytes,
        DiskReadBps = s.DiskReadBps,
        DiskWriteBps = s.DiskWriteBps,
        NetSentBps = s.NetSentBps,
        NetRecvBps = s.NetRecvBps,
    };
}
