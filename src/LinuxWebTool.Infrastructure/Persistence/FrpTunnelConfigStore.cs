using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>FRP 反向穿透配置持久化仓储</summary>
[DapperAot]
public partial class FrpTunnelConfigStore(DbConnectionFactory factory)
{
    public async Task<FrpTunnelConfigEntity> GetConfigAsync()
    {
        using var db = factory.CreateConnection();
        var entity = await db.QueryFirstOrDefaultAsync<FrpTunnelConfigEntity>("SELECT * FROM frp_tunnel_config WHERE Id = 'default'");
        if (entity != null) return entity;

        // 首启默认配置
        entity = new FrpTunnelConfigEntity
        {
            Id = "default",
            ServerUrl = string.Empty,
            TunnelHost = string.Empty,
            ApiKey = string.Empty,
            LocalTargetUrl = "http://127.0.0.1:8080",
            AutoStart = false,
            HeartbeatIntervalSeconds = 15,
            Status = 0,
            UpdateTime = DateTime.UtcNow
        };
        await SaveConfigAsync(entity);
        return entity;
    }

    public async Task SaveConfigAsync(FrpTunnelConfigEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.Id = "default";
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            INSERT INTO frp_tunnel_config (Id, ServerUrl, TunnelHost, ApiKey, LocalTargetUrl, AutoStart, HeartbeatIntervalSeconds, Status, LastConnectedAt, LastDisconnectReason, UpdateTime)
            VALUES (@Id, @ServerUrl, @TunnelHost, @ApiKey, @LocalTargetUrl, @AutoStart, @HeartbeatIntervalSeconds, @Status, @LastConnectedAt, @LastDisconnectReason, @UpdateTime)
            ON CONFLICT(Id) DO UPDATE SET
                ServerUrl = excluded.ServerUrl,
                TunnelHost = excluded.TunnelHost,
                ApiKey = excluded.ApiKey,
                LocalTargetUrl = excluded.LocalTargetUrl,
                AutoStart = excluded.AutoStart,
                HeartbeatIntervalSeconds = excluded.HeartbeatIntervalSeconds,
                Status = excluded.Status,
                LastConnectedAt = excluded.LastConnectedAt,
                LastDisconnectReason = excluded.LastDisconnectReason,
                UpdateTime = excluded.UpdateTime";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task UpdateStatusAsync(int status, DateTime? connectedAt, string? error)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
            UPDATE frp_tunnel_config
            SET Status = @Status, LastConnectedAt = COALESCE(@LastConnectedAt, LastConnectedAt), LastDisconnectReason = @LastDisconnectReason, UpdateTime = @UpdateTime
            WHERE Id = 'default'";
        await db.ExecuteAsync(sql, new
        {
            Status = status,
            LastConnectedAt = connectedAt,
            LastDisconnectReason = error,
            UpdateTime = DateTime.UtcNow
        });
    }
}
