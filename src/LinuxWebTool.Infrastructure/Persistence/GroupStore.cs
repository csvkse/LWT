using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>分组仓储（指令与定时任务共用）。</summary>
[DapperAot]
public partial class GroupStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<CommandGroup>> GetByTypeAsync(GroupBizType bizType)
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<CommandGroup>(
            "SELECT * FROM command_group WHERE BizType = @BizType ORDER BY SortOrder ASC, CreateTime ASC", 
            new { BizType = (int)bizType });
    }

    public async Task<CommandGroup?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<CommandGroup>("SELECT * FROM command_group WHERE Id = @Id", new { Id = id });
    }

    public async Task<bool> ExistsNameAsync(string name, GroupBizType bizType, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT 1 FROM command_group WHERE Name = @Name AND BizType = @BizType";
        if (excludeId.HasValue)
        {
            sql += " AND Id != @ExcludeId";
        }
        return await db.QueryFirstOrDefaultAsync<int?>(sql, new { Name = name, BizType = (int)bizType, ExcludeId = excludeId }) != null;
    }

    public async Task InsertAsync(CommandGroup group)
    {
        using var db = factory.CreateConnection();
        group.CreateTime = DateTime.Now;
        var sql = @"
            INSERT INTO command_group (Id, Name, BizType, SortOrder, CreateTime)
            VALUES (@Id, @Name, @BizType, @SortOrder, @CreateTime)";
        await db.ExecuteAsync(sql, group);
    }

    public async Task UpdateAsync(CommandGroup group)
    {
        using var db = factory.CreateConnection();
        var sql = @"
            UPDATE command_group 
            SET Name = @Name, BizType = @BizType, SortOrder = @SortOrder, CreateTime = @CreateTime
            WHERE Id = @Id";
        await db.ExecuteAsync(sql, group);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM command_group WHERE Id = @Id", new { Id = id });
    }
}
