namespace LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

/// <summary>SFTP 或 S3 挂载配置。密钥只用于生成受限权限的 rclone 配置文件。</summary>
public sealed class RcloneMount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string LocalPath { get; set; } = string.Empty;
    public string RemotePath { get; set; } = string.Empty;
    public string? Host { get; set; }
    public int Port { get; set; } = 22;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? KeyFile { get; set; }
    public string? HostKey { get; set; }
    public string? Endpoint { get; set; }
    public string? Bucket { get; set; }
    public string? Region { get; set; }
    public string? AccessKeyId { get; set; }
    public string? SecretAccessKey { get; set; }
    public bool AutoMount { get; set; }
    public bool Enabled { get; set; } = true;
    public string? Description { get; set; }
    public DateTime CreateTime { get; set; } = DateTime.Now;
    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
