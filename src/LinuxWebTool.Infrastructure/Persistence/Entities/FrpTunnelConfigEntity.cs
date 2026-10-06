namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>FRP 反向穿透配置持久化实体</summary>
public class FrpTunnelConfigEntity
{
    public string Id { get; set; } = "default";
    public string ServerUrl { get; set; } = string.Empty;
    public string TunnelHost { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string LocalTargetUrl { get; set; } = "http://127.0.0.1:8080";
    public bool AutoStart { get; set; }
    public int HeartbeatIntervalSeconds { get; set; } = 15;
    public int Status { get; set; }
    public DateTime? LastConnectedAt { get; set; }
    public string? LastDisconnectReason { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}
