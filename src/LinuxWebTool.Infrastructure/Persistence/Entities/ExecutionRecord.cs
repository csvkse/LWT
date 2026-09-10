
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>涓€娆℃寚浠ゆ墽琛岀殑鍘嗗彶璁板綍锛堣皟鐢ㄥ巻鍙?/ 瀹氭椂浠诲姟鎵ц鏃ュ織锛夈€?/summary>
public class ExecutionRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>鎵ц鏉ユ簮锛?=Manual 1=Schedule 2=Quick銆?/summary>
    public int Source { get; set; }

    public Guid? CommandId { get; set; }

    public Guid? ScheduleTaskId { get; set; }

    public string CommandName { get; set; } = string.Empty;

    public string CommandText { get; set; } = string.Empty;

    /// <summary>缁撴灉鐘舵€侊細0=Success 1=Failure 2=Timeout 3=Cancelled銆?/summary>
    public int Status { get; set; }

    public int? ExitCode { get; set; }

    public string Output { get; set; } = string.Empty;

    public string ErrorOutput { get; set; } = string.Empty;

    public long DurationMs { get; set; }

    public bool TimedOut { get; set; }

    public bool Truncated { get; set; }

    public string TriggerBy { get; set; } = string.Empty;

    public DateTime StartTime { get; set; } = DateTime.Now;

    public DateTime? EndTime { get; set; }
}

