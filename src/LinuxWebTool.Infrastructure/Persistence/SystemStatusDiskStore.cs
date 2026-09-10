using Dapper;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>系统状态磁盘挂载点历史快照仓储（写入 / 时间窗口查询 / 过期清理）。</summary>
[DapperAot]
public partial class SystemStatusDiskStore(DbConnectionFactory factory)
{
    public async Task InsertAsync(IEnumerable<SystemStatusDiskSnapshot> snapshots)
    {
        var batch = snapshots as SystemStatusDiskSnapshot[] ?? snapshots.ToArray();
        if (batch.Length > 0)
        {
            using var db = factory.CreateConnection();
            var sql = @"
INSERT INTO system_status_disk_snapshot (Id, Time, Mount, FileSystem, UsagePercent, TotalBytes, UsedBytes, FreeBytes)
VALUES (@Id, @Time, @Mount, @FileSystem, @UsagePercent, @TotalBytes, @UsedBytes, @FreeBytes)";
            await db.ExecuteAsync(sql, batch);
        }
    }

    /// <summary>查询最近 hours 小时的挂载点序列点（时间正序，供曲线渲染）。</summary>
    public async Task<IEnumerable<DiskSnapshotPoint>> QueryAsync(int hours)
    {
        hours = Math.Clamp(hours, 1, 24 * 30);
        var since = DateTime.Now.AddHours(-hours);
        using var db = factory.CreateConnection();
        var sql = @"
SELECT *
FROM system_status_disk_snapshot
WHERE Time >= @Since
ORDER BY Time";
        var rows = await db.QueryAsync<SystemStatusDiskSnapshot>(sql, new { Since = since });
        return rows.Select(ToPoint);
    }

    public async Task ClearOlderThan(DateTime cutoff)
    {
        using var db = factory.CreateConnection();
        var sql = "DELETE FROM system_status_disk_snapshot WHERE Time < @Cutoff";
        await db.ExecuteAsync(sql, new { Cutoff = cutoff });
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
