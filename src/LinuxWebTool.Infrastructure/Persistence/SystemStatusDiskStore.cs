using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>系统状态磁盘挂载点历史快照仓储（写入 / 时间窗口查询 / 过期清理）。</summary>
public class SystemStatusDiskStore(ISqlSugarClient db)
{
    public async Task InsertAsync(IEnumerable<SystemStatusDiskSnapshot> snapshots)
    {
        var batch = snapshots as SystemStatusDiskSnapshot[] ?? snapshots.ToArray();
        if (batch.Length > 0)
        {
            await db.Insertable(batch).ExecuteCommandAsync();
        }
    }

    /// <summary>查询最近 hours 小时的挂载点序列点（时间正序，供曲线渲染）。</summary>
    public async Task<List<DiskSnapshotPoint>> QueryAsync(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTime.Now.AddHours(-hours);
        var rows = await db.Queryable<SystemStatusDiskSnapshot>()
            .Where(s => s.Time >= since)
            .OrderBy(s => s.Time)
            .ToListAsync();
        return rows.Select(ToPoint).ToList();
    }

    public Task ClearOlderThan(DateTime cutoff)
    {
        return db.Deleteable<SystemStatusDiskSnapshot>().Where(s => s.Time < cutoff).ExecuteCommandAsync();
    }

    private static DiskSnapshotPoint ToPoint(SystemStatusDiskSnapshot s) => new()
    {
        Time = s.Time,
        Mount = s.Mount,
        FileSystem = s.FileSystem,
        UsagePercent = s.UsagePercent,
        TotalBytes = s.TotalBytes,
        UsedBytes = s.UsedBytes,
        FreeBytes = s.FreeBytes,
    };
}
