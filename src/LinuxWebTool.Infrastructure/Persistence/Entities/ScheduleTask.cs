
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>瀹氭椂浠诲姟锛氭寜 Cron 琛ㄨ揪寮忚皟搴︽墽琛屽凡淇濆瓨鐨勬寚浠ゃ€?/summary>
public class ScheduleTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public Guid CommandId { get; set; }

    public string CronExpression { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public Guid? GroupId { get; set; }

    public bool IsPinned { get; set; }

    public int SortOrder { get; set; }

    public int? TimeoutSeconds { get; set; }

    /// <summary>鑴氭湰浣嶇疆鍙傛暟鍘熷涓诧紙寮曞彿鎰熺煡鎷嗗垎涓?$1 $2...锛涘畾鏃舵墽琛屼娇鐢ㄥ浐瀹氬弬鏁帮級銆?/summary>
    public string? Arguments { get; set; }

    public DateTime? LastRunTime { get; set; }

    public DateTime? NextRunTime { get; set; }

    public DateTime CreateTime { get; set; } = DateTime.Now;

    public DateTime UpdateTime { get; set; } = DateTime.Now;
}

