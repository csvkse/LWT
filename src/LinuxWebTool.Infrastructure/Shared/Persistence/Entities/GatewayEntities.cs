namespace LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

/// <summary>网关 L7 路由持久化实体</summary>
public class GatewayRouteEntity
{
    public string Id { get; set; } = string.Empty;
    public string RouteId { get; set; } = string.Empty;
    public string ClusterId { get; set; } = string.Empty;
    public string MatchPath { get; set; } = string.Empty;
    public string? MatchHosts { get; set; }
    public string? Transforms { get; set; }
    public string? Metadata { get; set; }
    public int OrderNum { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>网关 L7 集群持久化实体</summary>
public class GatewayClusterEntity
{
    public string Id { get; set; } = string.Empty;
    public string ClusterId { get; set; } = string.Empty;
    public string LoadBalancingPolicy { get; set; } = "RoundRobin";
    public string Destinations { get; set; } = "[]";
    public string? HealthCheckConfig { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>即席网站代理持久化实体</summary>
public class GatewayWebsiteEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public bool RewriteBody { get; set; } = true;
    public bool RewriteCookie { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}

/// <summary>四层 TCP/UDP 转发持久化实体</summary>
public class GatewayTcpRouteEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Protocol { get; set; } = "TCP";
    public int ListenPort { get; set; }
    public string ForwardHost { get; set; } = string.Empty;
    public int ForwardPort { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}
