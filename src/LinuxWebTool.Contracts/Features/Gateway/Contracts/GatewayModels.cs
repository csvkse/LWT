namespace LinuxWebTool.Contracts.Features.Gateway.Contracts;

/// <summary>网关 L7 路由项契约</summary>
public sealed record GatewayRouteItem(
    string Id,
    string RouteId,
    string ClusterId,
    string MatchPath,
    string? MatchHosts,
    string? Transforms,
    string? Metadata,
    int OrderNum,
    bool IsEnabled,
    DateTime UpdateTime);

/// <summary>保存网关 L7 路由请求</summary>
public sealed record SaveGatewayRouteRequest(
    string RouteId,
    string ClusterId,
    string MatchPath,
    string? MatchHosts,
    string? Transforms,
    string? Metadata,
    int OrderNum,
    bool IsEnabled);

/// <summary>网关 L7 集群项契约</summary>
public sealed record GatewayClusterItem(
    string Id,
    string ClusterId,
    string LoadBalancingPolicy,
    string Destinations,
    string? HealthCheckConfig,
    DateTime UpdateTime);

/// <summary>保存网关 L7 集群请求</summary>
public sealed record SaveGatewayClusterRequest(
    string ClusterId,
    string LoadBalancingPolicy,
    string Destinations,
    string? HealthCheckConfig);

/// <summary>即席网站代理条目契约</summary>
public sealed record GatewayWebsiteItem(
    string Id,
    string Name,
    string TargetUrl,
    bool RewriteBody,
    bool RewriteCookie,
    bool IsEnabled,
    DateTime UpdateTime);

/// <summary>保存网站代理请求</summary>
public sealed record SaveGatewayWebsiteRequest(
    string Name,
    string TargetUrl,
    bool RewriteBody,
    bool RewriteCookie,
    bool IsEnabled);

/// <summary>四层 TCP/UDP 转发规则契约</summary>
public sealed record GatewayTcpRouteItem(
    string Id,
    string Name,
    string Protocol, // TCP | UDP
    int ListenPort,
    string ForwardHost,
    int ForwardPort,
    bool IsEnabled,
    DateTime UpdateTime);

/// <summary>保存四层 TCP/UDP 转发规则请求</summary>
public sealed record SaveGatewayTcpRouteRequest(
    string Name,
    string Protocol,
    int ListenPort,
    string ForwardHost,
    int ForwardPort,
    bool IsEnabled);
