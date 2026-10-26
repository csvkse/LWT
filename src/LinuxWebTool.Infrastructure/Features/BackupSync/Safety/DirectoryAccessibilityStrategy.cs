using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Safety;

/// <summary>
/// 策略2：目录存在性与读写访问能力防护
/// </summary>
public class DirectoryAccessibilityStrategy(ILogger<DirectoryAccessibilityStrategy> logger) : IBackupSyncSafetyStrategy
{
    public string Name => "DirectoryAccessibility";

    public async Task<SafetyEvaluationResult> EvaluateAsync(BackupSyncTask task, PreflightContext context, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(3000);

        try
        {
            var passed = await Task.Run(() =>
            {
                if (!Directory.Exists(task.SourcePath))
                {
                    return SafetyEvaluationResult.Fatal(Name, $"源目录不存在或无访问权限：{task.SourcePath}。已自动暂停以保护系统。");
                }

                if (!Directory.Exists(task.TargetPath))
                {
                    try
                    {
                        Directory.CreateDirectory(task.TargetPath);
                    }
                    catch (Exception ex)
                    {
                        return SafetyEvaluationResult.Fatal(Name, $"目标目录无法创建：{task.TargetPath} ({ex.Message})。已自动暂停。");
                    }
                }

                return SafetyEvaluationResult.Passed(Name);
            }, cts.Token);

            return passed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SafetyEvaluationResult.Fatal(Name, $"访问目录超时：源或目标目录长时间无响应，疑似网络挂载断开。已自动暂停。");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "检查目录访问能力异常：{Source} -> {Target}", task.SourcePath, task.TargetPath);
            return SafetyEvaluationResult.Fatal(Name, $"目录访问异常：{ex.Message}");
        }
    }
}
