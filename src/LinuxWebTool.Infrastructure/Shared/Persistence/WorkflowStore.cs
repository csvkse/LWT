using Dapper;
namespace LinuxWebTool.Infrastructure.Shared.Persistence;

/// <summary>工作流仓储。</summary>
[DapperAot]
public partial class WorkflowStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<WorkflowEntity>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<WorkflowEntity>(
            "SELECT * FROM workflows ORDER BY IsPinned DESC, SortOrder ASC, CreateTime ASC");
    }

    public async Task<WorkflowEntity?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<WorkflowEntity>(
            "SELECT * FROM workflows WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT 1 FROM workflows WHERE Name = @Name";
        if (excludeId.HasValue)
        {
            sql += " AND Id != @ExcludeId COLLATE NOCASE";
        }
        return await db.QueryFirstOrDefaultAsync<int?>(sql, new { Name = name, ExcludeId = excludeId }) != null;
    }

    public async Task InsertAsync(WorkflowEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.CreateTime = DateTime.Now;
        entity.UpdateTime = DateTime.Now;
        var sql = @"
            INSERT INTO workflows (
                Id, Name, Description, ExecutionMode, FailurePolicy, DefaultTargetHostId, 
                DefinitionJson, Enabled, GroupId, IsPinned, SortOrder, LastRunTime, CreateTime, UpdateTime
            ) VALUES (
                @Id, @Name, @Description, @ExecutionMode, @FailurePolicy, @DefaultTargetHostId, 
                @DefinitionJson, @Enabled, @GroupId, @IsPinned, @SortOrder, @LastRunTime, @CreateTime, @UpdateTime
            )";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task UpdateAsync(WorkflowEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.Now;
        var sql = @"
            UPDATE workflows 
            SET Name = @Name, Description = @Description, ExecutionMode = @ExecutionMode, 
                FailurePolicy = @FailurePolicy, DefaultTargetHostId = @DefaultTargetHostId, 
                DefinitionJson = @DefinitionJson, Enabled = @Enabled, GroupId = @GroupId, 
                IsPinned = @IsPinned, SortOrder = @SortOrder, LastRunTime = @LastRunTime, 
                UpdateTime = @UpdateTime
            WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM workflows WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task UpdateLastRunTimeAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync(
            "UPDATE workflows SET LastRunTime = @LastRunTime WHERE Id = @Id COLLATE NOCASE", 
            new { LastRunTime = DateTime.Now, Id = id });
    }

    public async Task ToggleEnabledAsync(Guid id, bool enabled)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync(
            "UPDATE workflows SET Enabled = @Enabled, UpdateTime = @UpdateTime WHERE Id = @Id COLLATE NOCASE", 
            new { Enabled = enabled, UpdateTime = DateTime.Now, Id = id });
    }
}
