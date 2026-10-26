using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 策略4：目标磁盘剩余空间防击穿防护策略
/// </summary>
public class DiskSpaceSafetyStrategy(ILogger<DiskSpaceSafetyStrategy> logger) : IBackupSyncSafetyStrategy
{
    private const long MinimumRequiredFreeBytes = 50 * 1024 * 1024; // 50MB 阈值

    public string Name => "DiskSpaceSafety";

    public Task<SafetyEvaluationResult> EvaluateAsync(BackupSyncTask task, PreflightContext context, CancellationToken ct = default)
    {
        var freeBytes = context.TargetHealth?.AvailableFreeBytes;
        if (freeBytes.HasValue && freeBytes.Value < MinimumRequiredFreeBytes)
        {
            var freeMb = freeBytes.Value / 1024 / 1024;
            logger.LogWarning("任务 {Name} 目标磁盘可用空间过低：{Mb}MB < 50MB", task.Name, freeMb);
            return Task.FromResult(SafetyEvaluationResult.Fatal(
                Name,
                $"目标磁盘空间严重不足（仅剩约 {freeMb} MB），为防止写满系统根盘或存储卷，任务已自动暂停保护。"));
        }

        return Task.FromResult(SafetyEvaluationResult.Passed(Name));
    }
}
