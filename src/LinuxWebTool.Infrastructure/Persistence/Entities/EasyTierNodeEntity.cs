namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>
/// EasyTier 节点持久化实体
/// </summary>
public sealed class EasyTierNodeEntity
{
    public string Id { get; set; } = string.Empty;
    public string InstanceName { get; set; } = string.Empty;
    public string NetworkName { get; set; } = "default";
    public string NetworkSecret { get; set; } = string.Empty;
    public string? VirtualIpv4 { get; set; }
    public bool EnableDhcp { get; set; } = true;
    public string ListenersJson { get; set; } = "[]";
    public string PeersJson { get; set; } = "[]";
    public string ProxyNetworksJson { get; set; } = "[]";
    public string RoutesJson { get; set; } = "[]";
    public string? RawTomlOverride { get; set; }
    public bool AutoStart { get; set; } = true;
    public int Status { get; set; } // 0: Stopped, 1: Running, 2: Error
    public string? LastError { get; set; }
    public DateTime UpdateTime { get; set; } = DateTime.UtcNow;
}
