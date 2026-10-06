namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>
/// FRP 穿透线路数据库持久化实体
/// </summary>
public sealed class FrpTunnelLineEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ServerUrl { get; set; } = string.Empty;
    public string? BackupServerUrls { get; set; }
    public string TunnelHost { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string LocalTargetUrl { get; set; } = "http://127.0.0.1:8080";
    public bool AutoStart { get; set; } = true;
    public int HeartbeatIntervalSeconds { get; set; } = 15;
    public bool EnableLan302Proxy { get; set; } = true;
    public string ProxyType { get; set; } = "Direct";
    public string? ProxyUrl { get; set; }
    public string? ProxyBypass { get; set; }
    public string Status { get; set; } = "Disconnected";
    public int SortOrder { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.UtcNow;
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}
