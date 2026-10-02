using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

[DapperAot]
public partial class RcloneMountStore(DbConnectionFactory factory)
{
    public async Task<IEnumerable<RcloneMount>> GetAllAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<RcloneMount>("SELECT * FROM rclone_mount ORDER BY CreateTime ASC");
    }

    public async Task<RcloneMount?> GetByIdAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<RcloneMount>(
            "SELECT * FROM rclone_mount WHERE Id = @Id COLLATE NOCASE LIMIT 1", new { Id = id });
    }

    public async Task<bool> ExistsLocalPathAsync(string localPath, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = excludeId.HasValue
            ? "SELECT 1 FROM rclone_mount WHERE LocalPath = @LocalPath AND Id != @ExcludeId COLLATE NOCASE LIMIT 1"
            : "SELECT 1 FROM rclone_mount WHERE LocalPath = @LocalPath LIMIT 1";
        return await db.QueryFirstOrDefaultAsync<int>(sql, new { LocalPath = localPath, ExcludeId = excludeId }) > 0;
    }

    public async Task<bool> ExistsNameAsync(string name, Guid? excludeId)
    {
        using var db = factory.CreateConnection();
        var sql = excludeId.HasValue
            ? "SELECT 1 FROM rclone_mount WHERE Name = @Name AND Id != @ExcludeId COLLATE NOCASE LIMIT 1"
            : "SELECT 1 FROM rclone_mount WHERE Name = @Name LIMIT 1";
        return await db.QueryFirstOrDefaultAsync<int>(sql, new { Name = name, ExcludeId = excludeId }) > 0;
    }

    public async Task InsertAsync(RcloneMount mount)
    {
        mount.CreateTime = DateTime.Now;
        mount.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("""
            INSERT INTO rclone_mount
            (Id, Name, Kind, LocalPath, RemotePath, Host, Port, Username, Password, KeyFile, HostKey,
             Endpoint, Bucket, Region, AccessKeyId, SecretAccessKey, AutoMount, Enabled, Description, CreateTime, UpdateTime)
            VALUES
            (@Id, @Name, @Kind, @LocalPath, @RemotePath, @Host, @Port, @Username, @Password, @KeyFile, @HostKey,
             @Endpoint, @Bucket, @Region, @AccessKeyId, @SecretAccessKey, @AutoMount, @Enabled, @Description, @CreateTime, @UpdateTime)
            """, mount);
    }

    public async Task UpdateAsync(RcloneMount mount)
    {
        mount.UpdateTime = DateTime.Now;
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("""
            UPDATE rclone_mount SET Name=@Name, Kind=@Kind, LocalPath=@LocalPath, RemotePath=@RemotePath,
              Host=@Host, Port=@Port, Username=@Username, Password=@Password, KeyFile=@KeyFile, HostKey=@HostKey,
              Endpoint=@Endpoint, Bucket=@Bucket, Region=@Region, AccessKeyId=@AccessKeyId,
              SecretAccessKey=@SecretAccessKey, AutoMount=@AutoMount, Enabled=@Enabled,
              Description=@Description, UpdateTime=@UpdateTime WHERE Id=@Id COLLATE NOCASE
            """, mount);
    }

    public async Task DeleteAsync(Guid id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM rclone_mount WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }
}
