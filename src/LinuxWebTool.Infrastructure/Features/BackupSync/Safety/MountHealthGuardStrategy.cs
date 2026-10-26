using LinuxWebTool.Contracts.Features.Mount.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 策略1：关联挂载健康与可达性守卫
/// 拦截源路径或目标路径所属的受管网络挂载（SMB/WebDAV/Rclone）处于脱机或降级状态的任务
/// </summary>
public class MountHealthGuardStrategy(
    PathStorageAdapterCatalog catalog,
    ILogger<MountHealthGuardStrategy> logger) : IBackupSyncSafetyStrategy
{
    public string Name => "MountHealthGuard";

    public async Task<SafetyEvaluationResult> EvaluateAsync(BackupSyncTask task, PreflightContext context, CancellationToken ct = default)
    {
        // 1. 源端健康评估
        context.SourceHealth = await catalog.InspectAsync(task.SourcePath, PathRole.Source, ct);
        if (!context.SourceHealth.IsAccessible)
        {
            logger.LogWarning("任务 {Name} 源端挂载健康检查未通过：{Error}", task.Name, context.SourceHealth.ErrorMessage);
            return SafetyEvaluationResult.Fatal(
                Name,
                $"源目录挂载异常：{context.SourceHealth.ErrorMessage ?? "无法连接"}。已触发自动暂停熔断保护，防止异常扩散。");
        }

        // 2. 目标端健康评估
        context.TargetHealth = await catalog.InspectAsync(task.TargetPath, PathRole.Target, ct);
        if (!context.TargetHealth.IsAccessible || !context.TargetHealth.IsWritable)
        {
            logger.LogWarning("任务 {Name} 目标端挂载健康检查未通过：{Error}", task.Name, context.TargetHealth.ErrorMessage);
            return SafetyEvaluationResult.Fatal(
                Name,
                $"目标目录挂载异常：{context.TargetHealth.ErrorMessage ?? "无法写入"}。已触发自动暂停熔断保护，防止写入中断或错误。");
        }

        return SafetyEvaluationResult.Passed(Name);
    }
}
