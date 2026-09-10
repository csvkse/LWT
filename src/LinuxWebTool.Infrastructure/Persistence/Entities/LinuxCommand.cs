
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>宸蹭繚瀛樼殑 Linux 鎸囦护銆?/summary>
public class LinuxCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string CommandText { get; set; } = string.Empty;

    /// <summary>0=鍛戒护琛?1=Bash 鑴氭湰锛圕ommandText 涓鸿剼鏈鏂囷紝SQLite TEXT 浜插拰鏃犻暱搴﹂檺鍒讹級銆?/summary>
    public int ScriptType { get; set; }

    public string? Description { get; set; }

    public Guid? GroupId { get; set; }

    public bool IsPinned { get; set; }

    public int SortOrder { get; set; }

    /// <summary>鎵ц瓒呮椂绉掓暟锛涗负绌烘椂浣跨敤 Shell 榛樿鍊笺€?/summary>
    public int? TimeoutSeconds { get; set; }

    public DateTime? LastExecTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

