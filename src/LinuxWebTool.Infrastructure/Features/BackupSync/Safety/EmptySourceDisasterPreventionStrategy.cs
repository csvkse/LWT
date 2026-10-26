using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 策略3：灾难性空源防护策略（防误删与防空快照污染）
/// 借鉴 Syncthing / Rclone 思想，防止网络卷静默脱机后暴露空目录，导致镜像同步清空目标端或增量备份生成空快照
/// </summary>
public class EmptySourceDisasterPreventionStrategy(
    BackupSyncStore store,
    ILogger<EmptySourceDisasterPreventionStrategy> logger) : IBackupSyncSafetyStrategy
{
    public string Name => "EmptySourceDisasterPrevention";

    public async Task<SafetyEvaluationResult> EvaluateAsync(BackupSyncTask task, PreflightContext context, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(3000);

        try
        {
            var sourceHasFiles = await Task.Run(() => HasAnyFile(task.SourcePath), cts.Token);
            context.SourceFilesCount = sourceHasFiles ? 1 : 0;

            if (sourceHasFiles)
            {
                return SafetyEvaluationResult.Passed(Name);
            }

            // 源目录当前检测为 0 个文件或空目录，研判是否属于异常脱机
            if (task.Mode == (int)TaskMode.Sync)
            {
                // 1. 若为单向镜像模式，检查目标端是否有有效文件
                var targetHasFiles = await Task.Run(() => HasAnyFile(task.TargetPath), cts.Token);
                context.TargetExistingFilesCount = targetHasFiles ? 1 : 0;

                if (targetHasFiles)
                {
                    logger.LogCritical("灾难拦截触发：任务 {Name} 源端为空，但目标端已存在数据。阻断执行以防目标被清空！", task.Name);
                    return SafetyEvaluationResult.Fatal(
                        Name,
                        "灾难清空熔断拦截：源目录检测为空（0 个文件），但目标目录已存在有效数据。若执行单向镜像同步将抹除目标端所有文件！任务已自动暂停保护。");
                }

                if (task.LastRunStatus == "Success")
                {
                    logger.LogCritical("灾难拦截触发：任务 {Name} 此前曾成功同步，但当前源端为空！疑似挂载掉线！", task.Name);
                    return SafetyEvaluationResult.Fatal(
                        Name,
                        "灾难清空熔断拦截：源目录检测为空，但该任务此前已成功同步过数据。疑似网络挂载脱机或源端数据异常，任务已自动暂停保护。");
                }
            }
            else if (task.Mode == (int)TaskMode.Backup)
            {
                context.PreviousSnapshots ??= (await store.GetSnapshotsByTaskIdAsync(task.Id)).ToList();
                if (context.PreviousSnapshots.Any(s => s.TotalFiles > 0))
                {
                    logger.LogCritical("增量备份拦截：任务 {Name} 源端为空，上一快照包含历史文件！", task.Name);
                    return SafetyEvaluationResult.Fatal(
                        Name,
                        "增量备份熔断拦截：源目录检测为空（上一快照包含历史文件）。为防止产生空白快照污染 GFS 轮转，任务已自动暂停保护。");
                }
            }

            return SafetyEvaluationResult.Passed(Name);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SafetyEvaluationResult.Fatal(Name, "扫描源端文件超时，无法确认源目录完整性，任务已自动暂停。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "扫描源端目录异常：{Source}", task.SourcePath);
            return SafetyEvaluationResult.Fatal(Name, $"源目录扫描异常：{ex.Message}");
        }
    }

    private static bool HasAnyFile(string dirPath)
    {
        if (!Directory.Exists(dirPath)) return false;
        try
        {
            return Directory.EnumerateFileSystemEntries(dirPath).Any();
        }
        catch
        {
            return false;
        }
    }
}
