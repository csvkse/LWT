namespace LinuxWebTool.Contracts.Models;

/// <summary>SMB 挂载运行状态（实时探测结果，非持久化字段）。</summary>
public enum SmbMountStatus
{
    /// <summary>未挂载。</summary>
    NotMounted = 0,
    /// <summary>已挂载（cifs 文件系统）。</summary>
    Mounted = 1,
    /// <summary>异常：挂载点被其他文件系统占用。</summary>
    Abnormal = 2,
    /// <summary>当前运行环境不支持挂载管理（Windows 开发机）。</summary>
    Unsupported = 3,
}

/// <summary>保存 SMB 挂载配置请求。</summary>
public sealed record SaveSmbMountRequest
{
    public required string Name { get; init; }

    /// <summary>服务器共享地址，如 //192.168.1.10/media；反斜杠写法会自动归一化为 //host/share。</summary>
    public required string Server { get; init; }

    /// <summary>本地挂载点（Linux 绝对路径），如 /mnt/media。</summary>
    public required string LocalPath { get; init; }

    public string? Username { get; init; }

    /// <summary>密码；更新时留空（null）表示保留原密码。持久化于 SQLite 并落地 data/mount-creds 凭据文件（600 权限）。</summary>
    public string? Password { get; init; }

    public string? Domain { get; init; }

    /// <summary>附加挂载选项（逗号分隔），如 vers=3.0,iocharset=utf8；为空时仅补 iocharset=utf8。</summary>
    public string? Options { get; init; }

    /// <summary>应用启动时自动重挂（不写 /etc/fstab，由应用负责重放）。</summary>
    public bool AutoMount { get; init; }

    public bool Enabled { get; init; } = true;

    public string? Description { get; init; }
}
