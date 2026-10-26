using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 备份与同步安全防护策略接口（策略模式）
/// </summary>
public interface IBackupSyncSafetyStrategy
{
    string Name { get; }

    Task<SafetyEvaluationResult> EvaluateAsync(BackupSyncTask task, PreflightContext context, CancellationToken ct = default);
}
