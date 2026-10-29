namespace LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

/// <summary>已保存的 Linux 指令。</summary>
public class LinuxCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string CommandText { get; set; } = string.Empty;

    /// <summary>0=命令行 1=Bash 脚本（CommandText 为脚本正文，SQLite TEXT 亲和无长度限制）。</summary>
    public int ScriptType { get; set; }

    public string? Description { get; set; }

    public Guid? GroupId { get; set; }

    public bool IsPinned { get; set; }

    public int SortOrder { get; set; }

    /// <summary>执行超时秒数；为空时使用 Shell 默认值。</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>推荐执行目标主机 Id（偏好建议，空表示本地服务器）。</summary>
    public string? DefaultTargetHostId { get; set; }

    public DateTime? LastExecTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}
