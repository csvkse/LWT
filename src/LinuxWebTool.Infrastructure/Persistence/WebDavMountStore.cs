using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

[DapperAot]
public partial class WebDavMountStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<WebDavMount>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<WebDavMount>("SELECT * FROM webdav_mount ORDER BY CreateTime ASC");
    }

    public async Task<WebDavMount?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<WebDavMount>(
            "SELECT * FROM webdav_mount WHERE Id = @Id COLLATE NOCASE LIMIT 1", new { Id = id });
    }

    public async Task<bool> ExistsLocalPathAsync(string localPath, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = excludeId.HasValue
            ? "SELECT 1 FROM webdav_mount WHERE LocalPath = @LocalPath AND Id != @ExcludeId COLLATE NOCASE LIMIT 1"
            : "SELECT 1 FROM webdav_mount WHERE LocalPath = @LocalPath LIMIT 1";
        return await db.QueryFirstOrDefaultAsync<int>(sql, new { LocalPath = localPath, ExcludeId = excludeId }) > 0;
    }

    public async Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = excludeId.HasValue
            ? "SELECT 1 FROM webdav_mount WHERE Name = @Name AND Id != @ExcludeId COLLATE NOCASE LIMIT 1"
            : "SELECT 1 FROM webdav_mount WHERE Name = @Name LIMIT 1";
        return await db.QueryFirstOrDefaultAsync<int>(sql, new { Name = name, ExcludeId = excludeId }) > 0;
    }

    public async Task InsertAsync(WebDavMount mount)
    {
        mount.CreateTime = DateTime.Now;
        mount.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("""
            INSERT INTO webdav_mount (Id, Name, Url, LocalPath, Username, Password, AutoMount, Enabled, Description, CreateTime, UpdateTime)
            VALUES (@Id, @Name, @Url, @LocalPath, @Username, @Password, @AutoMount, @Enabled, @Description, @CreateTime, @UpdateTime)
            """, mount);
    }

    public async Task UpdateAsync(WebDavMount mount)
    {
        mount.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("""
            UPDATE webdav_mount SET Name=@Name, Url=@Url, LocalPath=@LocalPath, Username=@Username,
              Password=@Password, AutoMount=@AutoMount, Enabled=@Enabled, Description=@Description, UpdateTime=@UpdateTime
            WHERE Id=@Id COLLATE NOCASE
            """, mount);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM webdav_mount WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }
}
