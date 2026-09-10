
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>鍒嗙粍锛堟寚浠?/ 瀹氭椂浠诲姟鍏辩敤涓€寮犺〃锛屾寜 BizType 鍖哄垎锛夈€?/summary>
public class CommandGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>0=鎸囦护鍒嗙粍 1=瀹氭椂浠诲姟鍒嗙粍銆?/summary>
    public int BizType { get; set; }

    public int SortOrder { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;
}

