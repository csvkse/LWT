using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 备份与同步安全预检流水线（责任链/管道模式）
/// 聚合多项安全策略，并在真实任务执行前实施综合研判
/// </summary>
public class BackupSyncSafetyPipeline(
    IEnumerable<IBackupSyncSafetyStrategy> strategies,
    ILogger<BackupSyncSafetyPipeline> logger)
{
    public async Task<PreflightReport> EvaluateAsync(BackupSyncTask task, CancellationToken ct = default)
    {
        var context = new PreflightContext { Task = task };
        var results = new List<SafetyEvaluationResult>();

        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();
            var res = await strategy.EvaluateAsync(task, context, ct);
            results.Add(res);

            if (!res.IsPassed && res.IsFatal)
            {
                logger.LogWarning("安全策略 [{Strategy}] 触发致命违背：{Message}（任务：{Task}）",
                    strategy.Name, res.Message, task.Name);

                return new PreflightReport
                {
                    IsPassed = false,
                    IsFatal = true,
                    ErrorMessage = res.Message,
                    Details = results
                };
            }
        }

        return new PreflightReport
        {
            IsPassed = true,
            IsFatal = false,
            Details = results
        };
    }
}
