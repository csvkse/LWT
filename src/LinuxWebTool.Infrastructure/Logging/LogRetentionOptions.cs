namespace LinuxWebTool.Infrastructure.Logging;

public sealed class LogRetentionOptions
{
    public const string SectionName = "Retention";

    public int FileLogDays { get; set; } = 30;
    public int ExecutionHistoryDays { get; set; } = 90;
    public int OperationLogDays { get; set; } = 180;
    public int CleanupIntervalHours { get; set; } = 24;
}
