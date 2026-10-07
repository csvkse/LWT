using Dapper;
namespace LinuxWebTool.Infrastructure.Shared.Persistence;

/// <summary>EasyTier 节点配置持久化仓储</summary>
[DapperAot]
public partial class EasyTierNodeStore(DbConnectionFactory factory)
{
    public async Task<IReadOnlyList<EasyTierNodeEntity>> GetAllNodesAsync()
    {
        using var db = factory.CreateConnection();
        var nodes = await db.QueryAsync<EasyTierNodeEntity>(
            "SELECT * FROM easytier_nodes ORDER BY InstanceName ASC");
        return nodes.AsList();
    }

    public async Task<EasyTierNodeEntity?> GetNodeByIdAsync(string id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<EasyTierNodeEntity>(
            "SELECT * FROM easytier_nodes WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task<EasyTierNodeEntity?> GetNodeByNameAsync(string instanceName)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<EasyTierNodeEntity>(
            "SELECT * FROM easytier_nodes WHERE InstanceName = @InstanceName", new { InstanceName = instanceName });
    }

    public async Task UpsertNodeAsync(EasyTierNodeEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            INSERT INTO easytier_nodes (
                Id, InstanceName, NetworkName, NetworkSecret, VirtualIpv4, EnableDhcp,
                ListenersJson, PeersJson, ProxyNetworksJson, RoutesJson, RawTomlOverride,
                AutoStart, Status, LastError, UpdateTime
            ) VALUES (
                @Id, @InstanceName, @NetworkName, @NetworkSecret, @VirtualIpv4, @EnableDhcp,
                @ListenersJson, @PeersJson, @ProxyNetworksJson, @RoutesJson, @RawTomlOverride,
                @AutoStart, @Status, @LastError, @UpdateTime
            ) ON CONFLICT(Id) DO UPDATE SET
                InstanceName = excluded.InstanceName,
                NetworkName = excluded.NetworkName,
                NetworkSecret = excluded.NetworkSecret,
                VirtualIpv4 = excluded.VirtualIpv4,
                EnableDhcp = excluded.EnableDhcp,
                ListenersJson = excluded.ListenersJson,
                PeersJson = excluded.PeersJson,
                ProxyNetworksJson = excluded.ProxyNetworksJson,
                RoutesJson = excluded.RoutesJson,
                RawTomlOverride = excluded.RawTomlOverride,
                AutoStart = excluded.AutoStart,
                Status = excluded.Status,
                LastError = excluded.LastError,
                UpdateTime = excluded.UpdateTime";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task DeleteNodeAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM easytier_nodes WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task UpdateStatusAsync(string id, int status, string? lastError)
    {
        using var db = factory.CreateConnection();
        const string sql = @"
            UPDATE easytier_nodes
            SET Status = @Status, LastError = @LastError, UpdateTime = @UpdateTime
            WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, new
        {
            Id = id,
            Status = status,
            LastError = lastError,
            UpdateTime = DateTime.UtcNow
        });
    }
}
