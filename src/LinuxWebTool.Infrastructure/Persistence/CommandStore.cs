using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>Linux 指令仓储。置顶靠 IsPinned + SortOrder 排序实现。</summary>
public class CommandStore(ISqlSugarClient db)
{
    public Task<List<LinuxCommand>> GetAllAsync()
    {
        return db.Queryable<LinuxCommand>()
            .OrderBy(c => c.IsPinned, OrderByType.Desc)
            .OrderBy(c => c.SortOrder)
            .OrderBy(c => c.CreateTime)
            .ToListAsync();
    }

    public Task<LinuxCommand?> GetByIdAsync(Guid id)
    {
        return db.Queryable<LinuxCommand>().FirstAsync(c => c.Id == id);
    }

    public Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        return db.Queryable<LinuxCommand>()
            .AnyAsync(c => c.Name == name && (excludeId == null || c.Id != excludeId));
    }

    public async Task InsertAsync(LinuxCommand command)
    {
        command.CreateTime = DateTime.Now;
        command.UpdateTime = DateTime.Now;
        await db.Insertable(command).ExecuteCommandAsync();
    }

    public async Task UpdateAsync(LinuxCommand command)
    {
        command.UpdateTime = DateTime.Now;
        await db.Updateable(command).ExecuteCommandAsync();
    }

    public Task DeleteAsync(Guid id)
    {
        return db.Deleteable<LinuxCommand>().Where(c => c.Id == id).ExecuteCommandAsync();
    }

    public Task<int> CountByGroupAsync(Guid groupId)
    {
        return db.Queryable<LinuxCommand>().CountAsync(c => c.GroupId == groupId);
    }

    public Task UpdateLastExecTimeAsync(Guid id)
    {
        return db.Updateable<LinuxCommand>()
            .SetColumns(c => c.LastExecTime == DateTime.Now)
            .Where(c => c.Id == id)
            .ExecuteCommandAsync();
    }
}
