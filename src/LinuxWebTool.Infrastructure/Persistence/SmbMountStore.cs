using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>SMB 挂载配置仓储。</summary>
[DapperAot]
public partial class SmbMountStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<SmbMount>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT * FROM smb_mount ORDER BY CreateTime ASC";
        return await db.QueryAsync<SmbMount>(sql);
    }

    public async Task<SmbMount?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        var sql = "SELECT * FROM smb_mount WHERE Id = @Id LIMIT 1";
        return await db.QueryFirstOrDefaultAsync<SmbMount>(sql, new { Id = id });
    }

    public async Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = excludeId.HasValue 
            ? "SELECT 1 FROM smb_mount WHERE Name = @Name AND Id != @ExcludeId LIMIT 1"
            : "SELECT 1 FROM smb_mount WHERE Name = @Name LIMIT 1";
        var count = await db.QueryFirstOrDefaultAsync<int>(sql, new { Name = name, ExcludeId = excludeId });
        return count > 0;
    }

    public async Task<bool> ExistsLocalPathAsync(string localPath, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = excludeId.HasValue 
            ? "SELECT 1 FROM smb_mount WHERE LocalPath = @LocalPath AND Id != @ExcludeId LIMIT 1"
            : "SELECT 1 FROM smb_mount WHERE LocalPath = @LocalPath LIMIT 1";
        var count = await db.QueryFirstOrDefaultAsync<int>(sql, new { LocalPath = localPath, ExcludeId = excludeId });
        return count > 0;
    }

    public async Task InsertAsync(SmbMount mount)
    {
        mount.CreateTime = DateTime.Now;
        mount.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        var sql = @"
INSERT INTO smb_mount (Id, Name, Server, LocalPath, Username, Password, Domain, Options, AutoMount, Enabled, Description, CreateTime, UpdateTime) 
VALUES (@Id, @Name, @Server, @LocalPath, @Username, @Password, @Domain, @Options, @AutoMount, @Enabled, @Description, @CreateTime, @UpdateTime)";
        await db.ExecuteAsync(sql, mount);
    }

    public async Task UpdateAsync(SmbMount mount)
    {
        mount.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        var sql = @"
UPDATE smb_mount SET 
    Name = @Name, Server = @Server, LocalPath = @LocalPath, Username = @Username, Password = @Password, 
    Domain = @Domain, Options = @Options, AutoMount = @AutoMount, Enabled = @Enabled, 
    Description = @Description, UpdateTime = @UpdateTime
WHERE Id = @Id";
        await db.ExecuteAsync(sql, mount);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        var sql = "DELETE FROM smb_mount WHERE Id = @Id";
        await db.ExecuteAsync(sql, new { Id = id });
    }
}
