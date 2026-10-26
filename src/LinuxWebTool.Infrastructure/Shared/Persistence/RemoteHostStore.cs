using Dapper;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Shared.Persistence;

/// <summary>远程主机持久化仓储</summary>
[DapperAot]
public partial class RemoteHostStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<RemoteHostEntity>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<RemoteHostEntity>(
            "SELECT * FROM remote_host ORDER BY SortOrder ASC, CreatedAt DESC");
    }

    public async Task<RemoteHostEntity?> GetByIdAsync(string id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<RemoteHostEntity>(
            "SELECT * FROM remote_host WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task InsertAsync(RemoteHostEntity entity)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
            INSERT INTO remote_host (
                Id, Name, Host, Port, Protocol, AuthType, Username,
                EncryptedCredential, CredentialNonce, CredentialTag, HostKeyFingerprint,
                Tags, Remark, SortOrder, CreatedAt, UpdatedAt, LastConnectedAt
            ) VALUES (
                @Id, @Name, @Host, @Port, @Protocol, @AuthType, @Username,
                @EncryptedCredential, @CredentialNonce, @CredentialTag, @HostKeyFingerprint,
                @Tags, @Remark, @SortOrder, @CreatedAt, @UpdatedAt, @LastConnectedAt
            )";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task UpdateAsync(RemoteHostEntity entity)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
            UPDATE remote_host
            SET Name = @Name,
                Host = @Host,
                Port = @Port,
                Protocol = @Protocol,
                AuthType = @AuthType,
                Username = @Username,
                EncryptedCredential = @EncryptedCredential,
                CredentialNonce = @CredentialNonce,
                CredentialTag = @CredentialTag,
                HostKeyFingerprint = @HostKeyFingerprint,
                Tags = @Tags,
                Remark = @Remark,
                SortOrder = @SortOrder,
                UpdatedAt = @UpdatedAt,
                LastConnectedAt = @LastConnectedAt
            WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task DeleteAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM remote_host WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task TouchConnectedAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync(
            "UPDATE remote_host SET LastConnectedAt = @Now WHERE Id = @Id COLLATE NOCASE",
            new { Now = DateTime.UtcNow, Id = id });
    }
}
