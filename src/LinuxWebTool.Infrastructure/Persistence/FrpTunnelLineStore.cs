using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>FRP 穿透多线路持久化仓储</summary>
[DapperAot]
public partial class FrpTunnelLineStore(DbConnectionFactory factory)
{
    public async Task<IReadOnlyList<FrpTunnelLineEntity>> GetAllLinesAsync()
    {
        using var db = factory.CreateConnection();
        var lines = await db.QueryAsync<FrpTunnelLineEntity>(
            "SELECT * FROM frp_tunnel_lines ORDER BY SortOrder ASC, CreateTime ASC");
        return lines.AsList();
    }

    public async Task<FrpTunnelLineEntity?> GetLineByIdAsync(string id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<FrpTunnelLineEntity>(
            "SELECT * FROM frp_tunnel_lines WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task<FrpTunnelLineEntity?> GetLineByHostAsync(string host)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<FrpTunnelLineEntity>(
            "SELECT * FROM frp_tunnel_lines WHERE TunnelHost = @Host", new { Host = host });
    }

    public async Task InsertLineAsync(FrpTunnelLineEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.CreateTime = DateTime.UtcNow;
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            INSERT INTO frp_tunnel_lines (
                Id, Name, ServerUrl, BackupServerUrls, TunnelHost, ApiKey,
                LocalTargetUrl, AutoStart, HeartbeatIntervalSeconds, EnableLan302Proxy,
                ProxyType, ProxyUrl, ProxyBypass, Status, SortOrder, CreateTime, UpdateTime
            ) VALUES (
                @Id, @Name, @ServerUrl, @BackupServerUrls, @TunnelHost, @ApiKey,
                @LocalTargetUrl, @AutoStart, @HeartbeatIntervalSeconds, @EnableLan302Proxy,
                @ProxyType, @ProxyUrl, @ProxyBypass, @Status, @SortOrder, @CreateTime, @UpdateTime
            )";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task UpdateLineAsync(FrpTunnelLineEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            UPDATE frp_tunnel_lines SET
                Name = @Name,
                ServerUrl = @ServerUrl,
                BackupServerUrls = @BackupServerUrls,
                TunnelHost = @TunnelHost,
                ApiKey = @ApiKey,
                LocalTargetUrl = @LocalTargetUrl,
                AutoStart = @AutoStart,
                HeartbeatIntervalSeconds = @HeartbeatIntervalSeconds,
                EnableLan302Proxy = @EnableLan302Proxy,
                ProxyType = @ProxyType,
                ProxyUrl = @ProxyUrl,
                ProxyBypass = @ProxyBypass,
                SortOrder = @SortOrder,
                UpdateTime = @UpdateTime
            WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task DeleteLineAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM frp_tunnel_lines WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task UpdateStatusAsync(string id, string status)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync(
            "UPDATE frp_tunnel_lines SET Status = @Status, UpdateTime = @UpdateTime WHERE Id = @Id COLLATE NOCASE",
            new { Id = id, Status = status, UpdateTime = DateTime.UtcNow });
    }
}
