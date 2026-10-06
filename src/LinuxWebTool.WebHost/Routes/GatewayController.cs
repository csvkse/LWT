using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Gateway;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Security;
using LinuxWebTool.WebHost.Composition;
using LinuxWebTool.WebHost.Extensions;
using LinuxWebTool.WebHost.Gateway;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>
/// 家庭智能网关（L7 HTTP 反向代理、网站代理、L4 TCP/UDP 转发）管理控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class GatewayController(
    GatewayStore store,
    DatabaseProxyConfigProvider proxyConfigProvider,
    TcpProxyEngine tcpEngine,
    IOperationLogger operationLogger) : MinimalApi.ControllerBase
{
    // === L7 路由 ===
    [HttpGet("Routes")]
    public async Task<IResult> GetRoutes()
    {
        var list = (await store.GetAllRoutesAsync()).Select(r => new GatewayRouteItem(
            r.Id, r.RouteId, r.ClusterId, r.MatchPath, r.MatchHosts, r.Transforms, r.Metadata, r.OrderNum, r.IsEnabled, r.UpdateTime
        )).ToList();
        return Ok(list);
    }

    [HttpPost("Routes")]
    public async Task<IResult> CreateRoute([FromBody] SaveGatewayRouteRequest request)
    {
        var entity = new GatewayRouteEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            RouteId = request.RouteId.Trim(),
            ClusterId = request.ClusterId.Trim(),
            MatchPath = request.MatchPath.Trim(),
            MatchHosts = request.MatchHosts?.Trim(),
            Transforms = request.Transforms,
            Metadata = request.Metadata,
            OrderNum = request.OrderNum,
            IsEnabled = request.IsEnabled
        };
        await store.InsertRouteAsync(entity);
        proxyConfigProvider.Reload();
        await operationLogger.LogAsync("新建网关路由", "网关服务", entity.RouteId, entity.MatchPath, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("路由已创建"));
    }

    [HttpPut("Routes/{id}")]
    public async Task<IResult> UpdateRoute(string id, [FromBody] SaveGatewayRouteRequest request)
    {
        var entity = await store.GetRouteByIdAsync(id);
        if (entity == null) return NotFound(new MessageResponse("路由不存在"));

        entity.RouteId = request.RouteId.Trim();
        entity.ClusterId = request.ClusterId.Trim();
        entity.MatchPath = request.MatchPath.Trim();
        entity.MatchHosts = request.MatchHosts?.Trim();
        entity.Transforms = request.Transforms;
        entity.Metadata = request.Metadata;
        entity.OrderNum = request.OrderNum;
        entity.IsEnabled = request.IsEnabled;

        await store.UpdateRouteAsync(entity);
        proxyConfigProvider.Reload();
        await operationLogger.LogAsync("更新网关路由", "网关服务", entity.RouteId, entity.MatchPath, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("路由已更新"));
    }

    [HttpDelete("Routes/{id}")]
    public async Task<IResult> DeleteRoute(string id)
    {
        var entity = await store.GetRouteByIdAsync(id);
        if (entity == null) return NotFound(new MessageResponse("路由不存在"));

        await store.DeleteRouteAsync(id);
        proxyConfigProvider.Reload();
        await operationLogger.LogAsync("删除网关路由", "网关服务", entity.RouteId, "", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("路由已删除"));
    }

    // === L7 集群 ===
    [HttpGet("Clusters")]
    public async Task<IResult> GetClusters()
    {
        var list = (await store.GetAllClustersAsync()).Select(c => new GatewayClusterItem(
            c.Id, c.ClusterId, c.LoadBalancingPolicy, c.Destinations, c.HealthCheckConfig, c.UpdateTime
        )).ToList();
        return Ok(list);
    }

    [HttpPost("Clusters")]
    public async Task<IResult> CreateCluster([FromBody] SaveGatewayClusterRequest request)
    {
        var entity = new GatewayClusterEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            ClusterId = request.ClusterId.Trim(),
            LoadBalancingPolicy = request.LoadBalancingPolicy,
            Destinations = request.Destinations,
            HealthCheckConfig = request.HealthCheckConfig
        };
        await store.InsertClusterAsync(entity);
        proxyConfigProvider.Reload();
        await operationLogger.LogAsync("新建网关集群", "网关服务", entity.ClusterId, entity.Destinations, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("集群已创建"));
    }

    [HttpPut("Clusters/{id}")]
    public async Task<IResult> UpdateCluster(string id, [FromBody] SaveGatewayClusterRequest request)
    {
        var entity = await store.GetClusterByIdAsync(id);
        if (entity == null) return NotFound(new MessageResponse("集群不存在"));

        entity.ClusterId = request.ClusterId.Trim();
        entity.LoadBalancingPolicy = request.LoadBalancingPolicy;
        entity.Destinations = request.Destinations;
        entity.HealthCheckConfig = request.HealthCheckConfig;

        await store.UpdateClusterAsync(entity);
        proxyConfigProvider.Reload();
        await operationLogger.LogAsync("更新网关集群", "网关服务", entity.ClusterId, entity.Destinations, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("集群已更新"));
    }

    [HttpDelete("Clusters/{id}")]
    public async Task<IResult> DeleteCluster(string id)
    {
        var entity = await store.GetClusterByIdAsync(id);
        if (entity == null) return NotFound(new MessageResponse("集群不存在"));

        await store.DeleteClusterAsync(id);
        proxyConfigProvider.Reload();
        await operationLogger.LogAsync("删除网关集群", "网关服务", entity.ClusterId, "", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("集群已删除"));
    }

    // === 网站代理 ===
    [HttpGet("Websites")]
    public async Task<IResult> GetWebsites()
    {
        var list = (await store.GetAllWebsitesAsync()).Select(w => new GatewayWebsiteItem(
            w.Id, w.Name, w.TargetUrl, w.RewriteBody, w.RewriteCookie, w.IsEnabled, w.UpdateTime
        )).ToList();
        return Ok(list);
    }

    [HttpPost("Websites")]
    public async Task<IResult> CreateWebsite([FromBody] SaveGatewayWebsiteRequest request)
    {
        var entity = new GatewayWebsiteEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name.Trim(),
            TargetUrl = request.TargetUrl.Trim(),
            RewriteBody = request.RewriteBody,
            RewriteCookie = request.RewriteCookie,
            IsEnabled = request.IsEnabled
        };
        await store.InsertWebsiteAsync(entity);
        proxyConfigProvider.Reload();
        await operationLogger.LogAsync("登记网站代理", "网关服务", entity.Name, entity.TargetUrl, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("网站代理已添加"));
    }

    [HttpPut("Websites/{id}")]
    public async Task<IResult> UpdateWebsite(string id, [FromBody] SaveGatewayWebsiteRequest request)
    {
        var entity = await store.GetWebsiteByIdAsync(id);
        if (entity == null) return NotFound(new MessageResponse("网站代理不存在"));

        entity.Name = request.Name.Trim();
        entity.TargetUrl = request.TargetUrl.Trim();
        entity.RewriteBody = request.RewriteBody;
        entity.RewriteCookie = request.RewriteCookie;
        entity.IsEnabled = request.IsEnabled;

        await store.UpdateWebsiteAsync(entity);
        proxyConfigProvider.Reload();
        await operationLogger.LogAsync("更新网站代理", "网关服务", entity.Name, entity.TargetUrl, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("网站代理已更新"));
    }

    [HttpDelete("Websites/{id}")]
    public async Task<IResult> DeleteWebsite(string id)
    {
        var entity = await store.GetWebsiteByIdAsync(id);
        if (entity == null) return NotFound(new MessageResponse("网站代理不存在"));

        await store.DeleteWebsiteAsync(id);
        proxyConfigProvider.Reload();
        await operationLogger.LogAsync("删除网站代理", "网关服务", entity.Name, entity.TargetUrl, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("网站代理已删除"));
    }

    // === L4 TCP/UDP 路由 ===
    [HttpGet("TcpRoutes")]
    public async Task<IResult> GetTcpRoutes()
    {
        var list = (await store.GetAllTcpRoutesAsync()).Select(t => new GatewayTcpRouteItem(
            t.Id, t.Name, t.Protocol, t.ListenPort, t.ForwardHost, t.ForwardPort, t.IsEnabled, t.UpdateTime
        )).ToList();
        return Ok(list);
    }

    [HttpPost("TcpRoutes")]
    public async Task<IResult> CreateTcpRoute([FromBody] SaveGatewayTcpRouteRequest request)
    {
        var entity = new GatewayTcpRouteEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = request.Name.Trim(),
            Protocol = request.Protocol.Trim().ToUpperInvariant(),
            ListenPort = request.ListenPort,
            ForwardHost = request.ForwardHost.Trim(),
            ForwardPort = request.ForwardPort,
            IsEnabled = request.IsEnabled
        };
        await store.InsertTcpRouteAsync(entity);
        _ = tcpEngine.ReloadAsync();
        await operationLogger.LogAsync("新建端口转发", "网关服务", $"{entity.Protocol}:{entity.ListenPort}", $"{entity.ForwardHost}:{entity.ForwardPort}", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("端口转发规则已添加"));
    }

    [HttpPut("TcpRoutes/{id}")]
    public async Task<IResult> UpdateTcpRoute(string id, [FromBody] SaveGatewayTcpRouteRequest request)
    {
        var entity = await store.GetTcpRouteByIdAsync(id);
        if (entity == null) return NotFound(new MessageResponse("转发规则不存在"));

        entity.Name = request.Name.Trim();
        entity.Protocol = request.Protocol.Trim().ToUpperInvariant();
        entity.ListenPort = request.ListenPort;
        entity.ForwardHost = request.ForwardHost.Trim();
        entity.ForwardPort = request.ForwardPort;
        entity.IsEnabled = request.IsEnabled;

        await store.UpdateTcpRouteAsync(entity);
        _ = tcpEngine.ReloadAsync();
        await operationLogger.LogAsync("更新端口转发", "网关服务", $"{entity.Protocol}:{entity.ListenPort}", $"{entity.ForwardHost}:{entity.ForwardPort}", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("端口转发规则已更新"));
    }

    [HttpDelete("TcpRoutes/{id}")]
    public async Task<IResult> DeleteTcpRoute(string id)
    {
        var entity = await store.GetTcpRouteByIdAsync(id);
        if (entity == null) return NotFound(new MessageResponse("转发规则不存在"));

        await store.DeleteTcpRouteAsync(id);
        _ = tcpEngine.ReloadAsync();
        await operationLogger.LogAsync("删除端口转发", "网关服务", $"{entity.Protocol}:{entity.ListenPort}", "", clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("端口转发规则已删除"));
    }
}
