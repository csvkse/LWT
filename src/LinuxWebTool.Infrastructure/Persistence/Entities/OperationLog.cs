
namespace LinuxWebTool.Infrastructure.Persistence.Entities;

/// <summary>鎿嶄綔鏃ュ織锛堝鍒犳敼銆佹墽琛屻€佺櫥褰曠瓑琛屼负瀹¤锛夈€?/summary>
public class OperationLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTime Time { get; set; } = DateTime.Now;

    public string Action { get; set; } = string.Empty;

    public string TargetType { get; set; } = string.Empty;

    public string TargetName { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public string? ClientIp { get; set; }

    public bool Success { get; set; } = true;
}

