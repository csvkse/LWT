using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>系统状态网卡历史快照仓储（写入 / 时间窗口查询 / 过期清理）。</summary>
public class SystemStatusNetStore(ISqlSugarClient db)
{
    public async Task InsertAsync(IEnumerable<SystemStatusNetSnapshot> snapshots)
    {
        var batch = snapshots as SystemStatusNetSnapshot[] ?? snapshots.ToArray();
        if (batch.Length > 0)
        {
            await db.Insertable(batch).ExecuteCommandAsync();
        }
    }

    /// <summary>查询最近 hours 小时的网卡序列点（时间正序，供曲线渲染）。</summary>
    public async Task<List<NetSnapshotPoint>> QueryAsync(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTime.Now.AddHours(-hours);
        var rows = await db.Queryable<SystemStatusNetSnapshot>()
            .Where(s => s.Time >= since)
            .OrderBy(s => s.Time)
            .ToListAsync();
        return rows.Select(ToPoint).ToList();
    }

    public Task ClearOlderThan(DateTime cutoff)
    {
        return db.Deleteable<SystemStatusNetSnapshot>().Where(s => s.Time < cutoff).ExecuteCommandAsync();
    }

    private static NetSnapshotPoint ToPoint(SystemStatusNetSnapshot s) => new()
    {
        Time = s.Time,
        Name = s.Name,
        SentBytesPerSec = s.SentBytesPerSec,
        RecvBytesPerSec = s.RecvBytesPerSec,
    };
}
