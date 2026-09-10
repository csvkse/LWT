using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>监听规则仓储。</summary>
[DapperAot]
public partial class WatchRuleStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<WatchRule>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<WatchRule>(
            "SELECT * FROM watch_rule ORDER BY CreateTime");
    }

    public async Task<WatchRule?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<WatchRule>(
            "SELECT * FROM watch_rule WHERE Id = @Id", new { Id = id });
    }

    public async Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT 1 FROM watch_rule WHERE Name = @Name";
        if (excludeId.HasValue)
        {
            sql += " AND Id != @ExcludeId";
        }
        var count = await db.QueryFirstOrDefaultAsync<int?>(sql, new { Name = name, ExcludeId = excludeId });
        return count.HasValue;
    }

    public async Task InsertAsync(WatchRule rule)
    {
        using var db = factory.CreateConnection();
        rule.CreateTime = DateTime.Now;
        rule.UpdateTime = DateTime.Now;
        var sql = @"
            INSERT INTO watch_rule (
                Id, Name, WatchPath, FilePatterns, PresetId, OutputMode, Recursive, Mode, 
                PollSeconds, Enabled, UseHardwareAccel, HardwareBackend, LastScanTime, CreateTime, UpdateTime
            ) VALUES (
                @Id, @Name, @WatchPath, @FilePatterns, @PresetId, @OutputMode, @Recursive, @Mode, 
                @PollSeconds, @Enabled, @UseHardwareAccel, @HardwareBackend, @LastScanTime, @CreateTime, @UpdateTime
            )";
        await db.ExecuteAsync(sql, rule);
    }

    public async Task UpdateAsync(WatchRule rule)
    {
        using var db = factory.CreateConnection();
        rule.UpdateTime = DateTime.Now;
        var sql = @"
            UPDATE watch_rule SET 
                Name = @Name, WatchPath = @WatchPath, FilePatterns = @FilePatterns, 
                PresetId = @PresetId, OutputMode = @OutputMode, Recursive = @Recursive, 
                Mode = @Mode, PollSeconds = @PollSeconds, Enabled = @Enabled, 
                UseHardwareAccel = @UseHardwareAccel, HardwareBackend = @HardwareBackend, 
                LastScanTime = @LastScanTime, UpdateTime = @UpdateTime
            WHERE Id = @Id";
        await db.ExecuteAsync(sql, rule);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM watch_rule WHERE Id = @Id", new { Id = id });
    }
}
