namespace LinuxWebTool.Contracts.Features.Tunnel.Contracts;

/// <summary>FRP 隧道单线路完整详情及运行时契约</summary>
public sealed record FrpTunnelLineDto(
    string Id,
    string Name,
    string ServerUrl,
    string? BackupServerUrls,
    string TunnelHost,
    string ApiKey,
    string LocalTargetUrl,
    bool AutoStart,
    int HeartbeatIntervalSeconds,
    bool EnableLan302Proxy,
    string ProxyType,
    string? ProxyUrl,
    string? ProxyBypass,
    string State,
    string? PublicUrl,
    string? SubdomainUrl,
    long UptimeSeconds,
    long SentBytes,
    long ReceivedBytes,
    string? LastError,
    int SortOrder,
    DateTime CreateTime,
    DateTime UpdateTime);

/// <summary>创建 FRP 穿透线路请求契约</summary>
public sealed record CreateFrpTunnelLineRequest(
    string Name,
    string ServerUrl,
    string? BackupServerUrls,
    string TunnelHost,
    string ApiKey,
    string LocalTargetUrl,
    bool AutoStart,
    int HeartbeatIntervalSeconds,
    bool EnableLan302Proxy,
    string ProxyType,
    string? ProxyUrl,
    string? ProxyBypass,
    int SortOrder);

/// <summary>更新 FRP 穿透线路请求契约</summary>
public sealed record UpdateFrpTunnelLineRequest(
    string Name,
    string ServerUrl,
    string? BackupServerUrls,
    string TunnelHost,
    string ApiKey,
    string LocalTargetUrl,
    bool AutoStart,
    int HeartbeatIntervalSeconds,
    bool EnableLan302Proxy,
    string ProxyType,
    string? ProxyUrl,
    string? ProxyBypass,
    int SortOrder);

/// <summary>兼容旧版单线路配置契约</summary>
public sealed record FrpTunnelConfigDto(
    string ServerUrl,
    string TunnelHost,
    string ApiKey,
    string LocalTargetUrl,
    bool AutoStart,
    int HeartbeatIntervalSeconds,
    DateTime UpdateTime);

/// <summary>兼容旧版更新配置请求契约</summary>
public sealed record UpdateFrpConfigRequest(
    string ServerUrl,
    string TunnelHost,
    string ApiKey,
    string LocalTargetUrl,
    bool AutoStart,
    int HeartbeatIntervalSeconds);

/// <summary>FRP 隧道实时状态契约</summary>
public sealed record FrpTunnelStatusDto(
    string State,
    string? PublicUrl,
    string? SubdomainUrl,
    string? LocalTargetUrl,
    DateTime? ConnectedAt,
    long UptimeSeconds,
    long SentBytes,
    long ReceivedBytes,
    string? LastError);

/// <summary>FRP 隧道日志项契约</summary>
public sealed record FrpTunnelLogItem(
    DateTime Timestamp,
    string Level,
    string Message);
