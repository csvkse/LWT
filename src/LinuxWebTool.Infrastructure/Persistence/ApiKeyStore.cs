using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>API Key 持久化仓储</summary>
[DapperAot]
public partial class ApiKeyStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<ApiKeyEntity>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<ApiKeyEntity>("SELECT * FROM api_key ORDER BY CreatedAt DESC");
    }

    public async Task<ApiKeyEntity?> GetByIdAsync(string id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<ApiKeyEntity>("SELECT * FROM api_key WHERE Id = @Id", new { Id = id });
    }

    public async Task<ApiKeyEntity?> GetByHashAsync(string keyHash)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<ApiKeyEntity>("SELECT * FROM api_key WHERE KeyHash = @KeyHash", new { KeyHash = keyHash });
    }

    public async Task InsertAsync(ApiKeyEntity entity)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
            INSERT INTO api_key (Id, Name, KeyPrefix, KeyHash, IsEnabled, AllowApi, AllowMcp, AllowTerminal, AllowSchedules, AllowFiles, AllowTranscode, AllowGateway, CreatedAt, LastUsedAt, ExpiresAt)
            VALUES (@Id, @Name, @KeyPrefix, @KeyHash, @IsEnabled, @AllowApi, @AllowMcp, @AllowTerminal, @AllowSchedules, @AllowFiles, @AllowTranscode, @AllowGateway, @CreatedAt, @LastUsedAt, @ExpiresAt)";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task UpdateAsync(ApiKeyEntity entity)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
            UPDATE api_key
            SET Name = @Name, IsEnabled = @IsEnabled, AllowApi = @AllowApi, AllowMcp = @AllowMcp,
                AllowTerminal = @AllowTerminal, AllowSchedules = @AllowSchedules, AllowFiles = @AllowFiles,
                AllowTranscode = @AllowTranscode, AllowGateway = @AllowGateway, ExpiresAt = @ExpiresAt
            WHERE Id = @Id";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task DeleteAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM api_key WHERE Id = @Id", new { Id = id });
    }

    public async Task TouchLastUsedAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("UPDATE api_key SET LastUsedAt = @LastUsedAt WHERE Id = @Id", new { LastUsedAt = DateTime.UtcNow, Id = id });
    }
}
