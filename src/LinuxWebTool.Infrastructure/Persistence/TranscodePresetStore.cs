using LinuxWebTool.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>转码预设仓储。</summary>
public class TranscodePresetStore(ISqlSugarClient db)
{
    public Task<List<TranscodePreset>> GetAllAsync()
    {
        return db.Queryable<TranscodePreset>()
            .OrderBy(p => p.IsBuiltin, OrderByType.Desc)
            .OrderBy(p => p.CreateTime)
            .ToListAsync();
    }

    public async Task<TranscodePreset?> GetByIdAsync(Guid id)
    {
        return (TranscodePreset?)await db.Queryable<TranscodePreset>().FirstAsync(p => p.Id == id);
    }

    public Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        return db.Queryable<TranscodePreset>()
            .AnyAsync(p => p.Name == name && (excludeId == null || p.Id != excludeId));
    }

    public async Task InsertAsync(TranscodePreset preset)
    {
        preset.CreateTime = DateTime.Now;
        preset.UpdateTime = DateTime.Now;
        await db.Insertable(preset).ExecuteCommandAsync();
    }

    public async Task UpdateAsync(TranscodePreset preset)
    {
        preset.UpdateTime = DateTime.Now;
        await db.Updateable(preset).ExecuteCommandAsync();
    }

    public Task DeleteAsync(Guid id)
    {
        return db.Deleteable<TranscodePreset>().Where(p => p.Id == id).ExecuteCommandAsync();
    }

    /// <summary>被监听规则引用的数量（删除前校验）。</summary>
    public Task<int> CountWatchRuleUsageAsync(Guid presetId)
    {
        return db.Queryable<WatchRule>().CountAsync(r => r.PresetId == presetId);
    }
}
