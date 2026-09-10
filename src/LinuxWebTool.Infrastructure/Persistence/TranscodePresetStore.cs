using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>转码预设仓储。</summary>
[DapperAot]
public partial class TranscodePresetStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<TranscodePreset>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<TranscodePreset>(
            "SELECT * FROM transcode_preset ORDER BY IsBuiltin DESC, CreateTime ASC");
    }

    public async Task<TranscodePreset?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<TranscodePreset>(
            "SELECT * FROM transcode_preset WHERE Id = @Id", new { Id = id });
    }

    public async Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT 1 FROM transcode_preset WHERE Name = @Name";
        if (excludeId.HasValue)
        {
            sql += " AND Id != @ExcludeId";
        }
        var count = await db.QueryFirstOrDefaultAsync<int?>(sql, new { Name = name, ExcludeId = excludeId });
        return count.HasValue;
    }

    public async Task InsertAsync(TranscodePreset preset)
    {
        using var db = factory.CreateConnection();
        preset.CreateTime = DateTime.Now;
        preset.UpdateTime = DateTime.Now;
        var sql = @"
            INSERT INTO transcode_preset (
                Id, Name, Container, VideoCodec, VideoQuality, AudioCodec, AudioBitrate, ExtraArgs, 
                Description, IsBuiltin, CreateTime, UpdateTime
            ) VALUES (
                @Id, @Name, @Container, @VideoCodec, @VideoQuality, @AudioCodec, @AudioBitrate, @ExtraArgs, 
                @Description, @IsBuiltin, @CreateTime, @UpdateTime
            )";
        await db.ExecuteAsync(sql, preset);
    }

    public async Task UpdateAsync(TranscodePreset preset)
    {
        using var db = factory.CreateConnection();
        preset.UpdateTime = DateTime.Now;
        var sql = @"
            UPDATE transcode_preset SET 
                Name = @Name, Container = @Container, VideoCodec = @VideoCodec, 
                VideoQuality = @VideoQuality, AudioCodec = @AudioCodec, AudioBitrate = @AudioBitrate, 
                ExtraArgs = @ExtraArgs, Description = @Description, IsBuiltin = @IsBuiltin, 
                UpdateTime = @UpdateTime
            WHERE Id = @Id";
        await db.ExecuteAsync(sql, preset);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM transcode_preset WHERE Id = @Id", new { Id = id });
    }

    /// <summary>被监听规则引用的数量（删除前校验）。</summary>
    public async Task<int> CountWatchRuleUsageAsync(Guid presetId)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<int>(
            "SELECT COUNT(1) FROM watch_rule WHERE PresetId = @PresetId", new { PresetId = presetId });
    }
}
