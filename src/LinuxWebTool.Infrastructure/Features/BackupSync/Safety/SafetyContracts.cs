using LinuxWebTool.Contracts.Features.Mount.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

public enum PathRole
{
    Source,
    Target
}

/// <summary>
/// 单个存储路径的底层健康探测快照
/// </summary>
public sealed record PathHealthReport
{
    public bool IsAccessible { get; init; } = true;
    public bool IsWritable { get; init; } = true;
    public long? AvailableFreeBytes { get; init; }
    public string? AssociatedMountPath { get; init; }
    public string? AssociatedBackend { get; init; }
    public MountHealthState? MountState { get; init; }
    public string? ErrorMessage { get; init; }

    public static PathHealthReport Healthy(string? mountPath = null, string? backend = null, long? freeBytes = null) => new()
    {
        IsAccessible = true,
        IsWritable = true,
        AssociatedMountPath = mountPath,
        AssociatedBackend = backend,
        MountState = MountHealthState.Healthy,
        AvailableFreeBytes = freeBytes
    };

    public static PathHealthReport Unhealthy(string errorMessage, string? mountPath = null, string? backend = null, MountHealthState? state = null) => new()
    {
        IsAccessible = false,
        IsWritable = false,
        AssociatedMountPath = mountPath,
        AssociatedBackend = backend,
        MountState = state ?? MountHealthState.Unknown,
        ErrorMessage = errorMessage
    };
}

/// <summary>
/// 预检安全评估上下文
/// </summary>
public sealed class PreflightContext
{
    public required BackupSyncTask Task { get; init; }
    public PathHealthReport? SourceHealth { get; set; }
    public PathHealthReport? TargetHealth { get; set; }
    public long? SourceFilesCount { get; set; }
    public long? TargetExistingFilesCount { get; set; }
    public IReadOnlyList<BackupSnapshot>? PreviousSnapshots { get; set; }
}

/// <summary>
/// 单项安全防护策略评估结果
/// </summary>
public sealed record SafetyEvaluationResult
{
    public bool IsPassed { get; init; }
    public bool IsFatal { get; init; }
    public string StrategyName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;

    public static SafetyEvaluationResult Passed(string strategyName) => new()
    {
        IsPassed = true,
        IsFatal = false,
        StrategyName = strategyName,
        Message = "Check passed"
    };

    public static SafetyEvaluationResult Fatal(string strategyName, string message) => new()
    {
        IsPassed = false,
        IsFatal = true,
        StrategyName = strategyName,
        Message = message
    };

    public static SafetyEvaluationResult Warning(string strategyName, string message) => new()
    {
        IsPassed = true,
        IsFatal = false,
        StrategyName = strategyName,
        Message = message
    };
}

/// <summary>
/// 完整预检安全流水线报告
/// </summary>
public sealed record PreflightReport
{
    public bool IsPassed { get; init; }
    public bool IsFatal { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<SafetyEvaluationResult> Details { get; init; } = [];
}
