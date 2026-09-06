using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>分组仓储（指令与定时任务共用）。</summary>
public class GroupStore(ISqlSugarClient db)
{
    public Task<List<CommandGroup>> GetByTypeAsync(GroupBizType bizType)
    {
        return db.Queryable<CommandGroup>()
            .Where(g => g.BizType == (int)bizType)
            .OrderBy(g => g.SortOrder)
            .OrderBy(g => g.CreateTime)
            .ToListAsync();
    }

    public async Task<CommandGroup?> GetByIdAsync(Guid id)
    {
        return (CommandGroup?)await db.Queryable<CommandGroup>().FirstAsync();
    }

    public Task<bool> ExistsNameAsync(string name, GroupBizType bizType, Guid? excludeId)
    {
        return db.Queryable<CommandGroup>()
            .AnyAsync(g => g.Name == name
                && g.BizType == (int)bizType
                && (excludeId == null || g.Id != excludeId));
    }

    public async Task InsertAsync(CommandGroup group)
    {
        group.CreateTime = DateTime.Now;
        await db.Insertable(group).ExecuteCommandAsync();
    }

    public async Task UpdateAsync(CommandGroup group)
    {
        await db.Updateable(group).ExecuteCommandAsync();
    }

    public Task DeleteAsync(Guid id)
    {
        return db.Deleteable<CommandGroup>().Where(g => g.Id == id).ExecuteCommandAsync();
    }
}
