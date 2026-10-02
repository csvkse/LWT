namespace LinuxWebTool.Contracts.Models;

public enum MountExecutionPhase { Idle, Queued, Probing, Mounting, Verifying, Unmounting, WaitingRetry, WaitingAction }
public enum MountManagementMode { Disabled, MonitorOnly, Automatic, ManualPaused }
public enum MountFailureKind { None, Unreachable, AuthenticationFailed, Conflict, Busy, Timeout, Unsupported, Failed, Cancelled, PermissionDenied }

public sealed record MountTaskInfo
{
    public Guid TaskId { get; init; }
    public string Backend { get; init; } = "";
    public Guid MountId { get; init; }
    public string Action { get; init; } = "";
    public bool Completed { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; init; }
    public MountFailureKind FailureKind { get; init; }
}
