using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// 快照还原引擎：支持回滚覆盖至源目录，或安全提取还原至指定新目录。
/// </summary>
public class SnapshotRestoreEngine(ILogger<SnapshotRestoreEngine> logger)
{
    public async Task<RestoreSnapshotResult> RestoreAsync(
        string snapshotDir,
        string targetDirectory,
        bool cleanTargetBeforeRestore,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(snapshotDir))
        {
            throw new DirectoryNotFoundException($"快照目录不存在：{snapshotDir}");
        }

        Directory.CreateDirectory(targetDirectory);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (cleanTargetBeforeRestore)
        {
            logger.LogWarning("还原前清空目标目录：{Target}", targetDirectory);
            var existingDir = new DirectoryInfo(targetDirectory);
            foreach (var f in existingDir.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                try { f.Delete(); } catch { }
            }
            foreach (var d in existingDir.EnumerateDirectories())
            {
                try { d.Delete(true); } catch { }
            }
        }

        long filesCount = 0;
        long totalBytes = 0;
        var snapDirInfo = new DirectoryInfo(snapshotDir);

        await Task.Run(() =>
        {
            foreach (var file in snapDirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                // 跳过快照元数据描述文件
                if (file.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(snapshotDir, file.FullName);
                var destFile = Path.Combine(targetDirectory, rel);
                var destDir = Path.GetDirectoryName(destFile);

                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                file.CopyTo(destFile, overwrite: true);
                File.SetLastWriteTimeUtc(destFile, file.LastWriteTimeUtc);
                filesCount++;
                totalBytes += file.Length;
            }
        }, ct);

        sw.Stop();
        logger.LogInformation("快照还原完成：还原至 {Dest}，文件数 {Files}，总字节 {Bytes}，耗时 {Ms}ms",
            targetDirectory, filesCount, totalBytes, sw.ElapsedMilliseconds);

        return new RestoreSnapshotResult
        {
            RestoredFilesCount = (int)filesCount,
            RestoredBytes = totalBytes,
            TargetDirectory = targetDirectory,
            DurationMs = sw.ElapsedMilliseconds
        };
    }
}
