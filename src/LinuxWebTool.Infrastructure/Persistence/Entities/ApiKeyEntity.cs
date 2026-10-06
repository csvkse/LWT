namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>API Key 持久化实体</summary>
public class ApiKeyEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string KeyPrefix { get; set; } = string.Empty;
    public string KeyHash { get; set; } = string.Empty;
    public bool IsEnabled { get; set; } = true;
    public bool AllowApi { get; set; } = true;
    public bool AllowMcp { get; set; } = true;
    public bool AllowTerminal { get; set; } = false;
    public bool AllowSchedules { get; set; } = false;
    public bool AllowFiles { get; set; } = false;
    public bool AllowTranscode { get; set; } = false;
    public bool AllowGateway { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
}
