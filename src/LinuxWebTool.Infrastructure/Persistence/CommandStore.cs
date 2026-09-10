using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>Linux 指令仓储。置顶靠 IsPinned + SortOrder 排序实现。</summary>
[DapperAot]
public partial class CommandStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<LinuxCommand>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<LinuxCommand>("SELECT * FROM linux_command ORDER BY IsPinned DESC, SortOrder ASC, CreateTime ASC");
    }

    public async Task<LinuxCommand?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<LinuxCommand>("SELECT * FROM linux_command WHERE Id = @Id", new { Id = id });
    }

    public async Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT 1 FROM linux_command WHERE Name = @Name";
        if (excludeId.HasValue)
        {
            sql += " AND Id != @ExcludeId";
        }
        return await db.QueryFirstOrDefaultAsync<int?>(sql, new { Name = name, ExcludeId = excludeId }) != null;
    }

    public async Task InsertAsync(LinuxCommand command)
    {
        using var db = factory.CreateConnection();
        command.CreateTime = DateTime.Now;
        command.UpdateTime = DateTime.Now;
        var sql = @"
            INSERT INTO linux_command (Id, Name, CommandText, ScriptType, Description, GroupId, IsPinned, SortOrder, TimeoutSeconds, LastExecTime, CreateTime, UpdateTime)
            VALUES (@Id, @Name, @CommandText, @ScriptType, @Description, @GroupId, @IsPinned, @SortOrder, @TimeoutSeconds, @LastExecTime, @CreateTime, @UpdateTime)";
        await db.ExecuteAsync(sql, command);
    }

    public async Task UpdateAsync(LinuxCommand command)
    {
        using var db = factory.CreateConnection();
        command.UpdateTime = DateTime.Now;
        var sql = @"
            UPDATE linux_command 
            SET Name = @Name, CommandText = @CommandText, ScriptType = @ScriptType, Description = @Description, 
                GroupId = @GroupId, IsPinned = @IsPinned, SortOrder = @SortOrder, TimeoutSeconds = @TimeoutSeconds, 
                LastExecTime = @LastExecTime, CreateTime = @CreateTime, UpdateTime = @UpdateTime
            WHERE Id = @Id";
        await db.ExecuteAsync(sql, command);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM linux_command WHERE Id = @Id", new { Id = id });
    }

    public async Task<int> CountByGroupAsync(Guid groupId)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<int>("SELECT COUNT(*) FROM linux_command WHERE GroupId = @GroupId", new { GroupId = groupId });
    }

    public async Task UpdateLastExecTimeAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("UPDATE linux_command SET LastExecTime = @LastExecTime WHERE Id = @Id", new { LastExecTime = DateTime.Now, Id = id });
    }
}
