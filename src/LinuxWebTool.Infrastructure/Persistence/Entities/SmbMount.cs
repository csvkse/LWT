using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>SMB 挂载配置。挂载 / 卸载 / 状态探测由 SmbMountService 执行，启动重挂由 SmbMountStartupService 负责。</summary>
[SugarTable("smb_mount")]
public class SmbMount
{
    [SugarColumn(IsPrimaryKey = true)]
    public Guid Id { get; set; } = Guid.NewGuid();

    [SugarColumn(ColumnDataType = "nvarchar(100)")]
    public string Name { get; set; } = string.Empty;

    /// <summary>服务器共享地址，归一化为 //host/share。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(200)")]
    public string Server { get; set; } = string.Empty;

    /// <summary>本地挂载点（Linux 绝对路径）。</summary>
    [SugarColumn(ColumnDataType = "nvarchar(500)")]
    public string LocalPath { get; set; } = string.Empty;

    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(100)")]
    public string? Username { get; set; }

    /// <summary>SMB 密码（挂载本身需要原文；同时落地 data/mount-creds 凭据文件，600 权限）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(200)")]
    public string? Password { get; set; }

    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(100)")]
    public string? Domain { get; set; }

    /// <summary>附加挂载选项（逗号分隔）。</summary>
    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(500)")]
    public string? Options { get; set; }

    public bool AutoMount { get; set; }

    public bool Enabled { get; set; } = true;

    [SugarColumn(IsNullable = true, ColumnDataType = "nvarchar(500)")]
    public string? Description { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
