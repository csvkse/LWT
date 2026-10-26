namespace LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

/// <summary>远程主机持久化实体</summary>
public class RemoteHostEntity
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Protocol { get; set; } = "ssh";
    public string AuthType { get; set; } = "Password";
    public string Username { get; set; } = "root";
    public string? EncryptedCredential { get; set; }
    public string? CredentialNonce { get; set; }
    public string? CredentialTag { get; set; }
    public string? HostKeyFingerprint { get; set; }
    public string? Tags { get; set; }
    public string? Remark { get; set; }
    public int SortOrder { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastConnectedAt { get; set; }
}
