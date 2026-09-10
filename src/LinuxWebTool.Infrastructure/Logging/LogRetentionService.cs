using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Support;
using LinuxWebTool.Infrastructure.Transcode;

namespace LinuxWebTool.Infrastructure.Logging;

/// <summary>启动时及固定周期清理文件日志、转码日志和数据库历史记录。</summary>
public sealed class LogRetentionService(
    FileLoggerOptions fileOptions,
    LogRetentionOptions retentionOptions,
    TranscodeOptions transcodeOptions,
    DataPaths dataPaths,
    ExecutionStore executionStore,
    OperationLogStore operationLogStore,
    ILogger<LogRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CleanupSafelyAsync();

        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Max(1, retentionOptions.CleanupIntervalHours)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await CleanupSafelyAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
    }

    private async Task CleanupSafelyAsync()
    {
        try
        {
            var now = DateTime.Now;
            DeleteExpiredFiles(fileOptions.Directory, now.AddDays(-Math.Max(1, retentionOptions.FileLogDays)),
                fileOptions.AppFilePrefix + "-", fileOptions.DebugFilePrefix + "-");
            DeleteExpiredFiles(dataPaths.Resolve(Path.Combine("logs", "transcode")),
                now.AddDays(-Math.Max(1, transcodeOptions.LogRetentionDays)));
            await executionStore.ClearOlderThanAsync(now.AddDays(-Math.Max(1, retentionOptions.ExecutionHistoryDays)));
            await operationLogStore.ClearOlderThanAsync(now.AddDays(-Math.Max(1, retentionOptions.OperationLogDays)));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "日志保留策略清理失败");
        }
    }

    internal static int DeleteExpiredFiles(string directory, DateTime cutoff, params string[] prefixes)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            var info = new FileInfo(path);
            if (info.LastWriteTime >= cutoff ||
                prefixes.Length > 0 && !prefixes.Any(prefix => info.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            info.Delete();
            deleted++;
        }
        return deleted;
    }
}
