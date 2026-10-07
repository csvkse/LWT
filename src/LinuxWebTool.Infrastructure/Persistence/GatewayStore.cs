using Dapper;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>网关路由、集群与网站代理持久化仓储</summary>
[DapperAot]
public partial class GatewayStore(DbConnectionFactory factory)
{
    // === L7 路由 ===
    public async Task<IEnumerable<GatewayRouteEntity>> GetAllRoutesAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<GatewayRouteEntity>("SELECT * FROM gateway_route ORDER BY OrderNum ASC, UpdateTime DESC");
    }

    public async Task<GatewayRouteEntity?> GetRouteByIdAsync(string id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<GatewayRouteEntity>("SELECT * FROM gateway_route WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task InsertRouteAsync(GatewayRouteEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            INSERT INTO gateway_route (Id, RouteId, ClusterId, MatchPath, MatchHosts, Transforms, Metadata, OrderNum, IsEnabled, UpdateTime)
            VALUES (@Id, @RouteId, @ClusterId, @MatchPath, @MatchHosts, @Transforms, @Metadata, @OrderNum, @IsEnabled, @UpdateTime)";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task UpdateRouteAsync(GatewayRouteEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            UPDATE gateway_route
            SET RouteId = @RouteId, ClusterId = @ClusterId, MatchPath = @MatchPath, MatchHosts = @MatchHosts,
                Transforms = @Transforms, Metadata = @Metadata, OrderNum = @OrderNum, IsEnabled = @IsEnabled, UpdateTime = @UpdateTime
            WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task DeleteRouteAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM gateway_route WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    // === L7 集群 ===
    public async Task<IEnumerable<GatewayClusterEntity>> GetAllClustersAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<GatewayClusterEntity>("SELECT * FROM gateway_cluster ORDER BY UpdateTime DESC");
    }

    public async Task<GatewayClusterEntity?> GetClusterByIdAsync(string id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<GatewayClusterEntity>("SELECT * FROM gateway_cluster WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task InsertClusterAsync(GatewayClusterEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            INSERT INTO gateway_cluster (Id, ClusterId, LoadBalancingPolicy, Destinations, HealthCheckConfig, UpdateTime)
            VALUES (@Id, @ClusterId, @LoadBalancingPolicy, @Destinations, @HealthCheckConfig, @UpdateTime)";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task UpdateClusterAsync(GatewayClusterEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            UPDATE gateway_cluster
            SET ClusterId = @ClusterId, LoadBalancingPolicy = @LoadBalancingPolicy, Destinations = @Destinations,
                HealthCheckConfig = @HealthCheckConfig, UpdateTime = @UpdateTime
            WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task DeleteClusterAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM gateway_cluster WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    // === 网站代理 ===
    public async Task<IEnumerable<GatewayWebsiteEntity>> GetAllWebsitesAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<GatewayWebsiteEntity>("SELECT * FROM gateway_website ORDER BY UpdateTime DESC");
    }

    public async Task<GatewayWebsiteEntity?> GetWebsiteByIdAsync(string id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<GatewayWebsiteEntity>("SELECT * FROM gateway_website WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task InsertWebsiteAsync(GatewayWebsiteEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            INSERT INTO gateway_website (Id, Name, TargetUrl, RewriteBody, RewriteCookie, IsEnabled, UpdateTime)
            VALUES (@Id, @Name, @TargetUrl, @RewriteBody, @RewriteCookie, @IsEnabled, @UpdateTime)";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task UpdateWebsiteAsync(GatewayWebsiteEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            UPDATE gateway_website
            SET Name = @Name, TargetUrl = @TargetUrl, RewriteBody = @RewriteBody, RewriteCookie = @RewriteCookie,
                IsEnabled = @IsEnabled, UpdateTime = @UpdateTime
            WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task DeleteWebsiteAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM gateway_website WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    // === L4 TCP/UDP 路由 ===
    public async Task<IEnumerable<GatewayTcpRouteEntity>> GetAllTcpRoutesAsync()
    {
        using var db = factory.CreateConnection();
        return await db.QueryAsync<GatewayTcpRouteEntity>("SELECT * FROM gateway_tcp_route ORDER BY UpdateTime DESC");
    }

    public async Task<GatewayTcpRouteEntity?> GetTcpRouteByIdAsync(string id)
    {
        using var db = factory.CreateConnection();
        return await db.QueryFirstOrDefaultAsync<GatewayTcpRouteEntity>("SELECT * FROM gateway_tcp_route WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }

    public async Task InsertTcpRouteAsync(GatewayTcpRouteEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            INSERT INTO gateway_tcp_route (Id, Name, Protocol, ListenPort, ForwardHost, ForwardPort, IsEnabled, UpdateTime)
            VALUES (@Id, @Name, @Protocol, @ListenPort, @ForwardHost, @ForwardPort, @IsEnabled, @UpdateTime)";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task UpdateTcpRouteAsync(GatewayTcpRouteEntity entity)
    {
        using var db = factory.CreateConnection();
        entity.UpdateTime = DateTime.UtcNow;
        const string sql = @"
            UPDATE gateway_tcp_route
            SET Name = @Name, Protocol = @Protocol, ListenPort = @ListenPort, ForwardHost = @ForwardHost,
                ForwardPort = @ForwardPort, IsEnabled = @IsEnabled, UpdateTime = @UpdateTime
            WHERE Id = @Id COLLATE NOCASE";
        await db.ExecuteAsync(sql, entity);
    }

    public async Task DeleteTcpRouteAsync(string id)
    {
        using var db = factory.CreateConnection();
        await db.ExecuteAsync("DELETE FROM gateway_tcp_route WHERE Id = @Id COLLATE NOCASE", new { Id = id });
    }
}
