using LinuxWebTool.Infrastructure.Persistence.Entities;
using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>监听规则仓储。</summary>
public class WatchRuleStore(ISqlSugarClient db)
{
    public Task<List<WatchRule>> GetAllAsync()
    {
        return db.Queryable<WatchRule>()
            .OrderBy(r => r.CreateTime)
            .ToListAsync();
    }

    public async Task<WatchRule?> GetByIdAsync(Guid id)
    {
        return (WatchRule?)await db.Queryable<WatchRule>().FirstAsync(r => r.Id == id);
    }

    public Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        return db.Queryable<WatchRule>()
            .AnyAsync(r => r.Name == name && (excludeId == null || r.Id != excludeId));
    }

    public async Task InsertAsync(WatchRule rule)
    {
        rule.CreateTime = DateTime.Now;
        rule.UpdateTime = DateTime.Now;
        await db.Insertable(rule).ExecuteCommandAsync();
    }

    public async Task UpdateAsync(WatchRule rule)
    {
        rule.UpdateTime = DateTime.Now;
        await db.Updateable(rule).ExecuteCommandAsync();
    }

    public Task DeleteAsync(Guid id)
    {
        return db.Deleteable<WatchRule>().Where(r => r.Id == id).ExecuteCommandAsync();
    }
}
