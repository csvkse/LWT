using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// 直接同步（单向镜像 / 单向累加）引擎，支持回收站暂存与老旧回收站定期清理。
/// </summary>
public class MirrorSyncEngine(ILogger<MirrorSyncEngine> logger)
{
    public sealed record SyncExecutionResult
    {
        public long AddedFiles { get; init; }
        public long UpdatedFiles { get; init; }
        public long DeletedFiles { get; init; }
        public long TrashedFiles { get; init; }
        public long TotalBytesTransferred { get; init; }
        public long DurationMs { get; init; }
    }

    public async Task<SyncExecutionResult> ExecuteSyncAsync(
        string sourcePath,
        string targetPath,
        SyncDirection direction,
        DeleteHandling deleteHandling,
        int trashRetentionDays,
        IReadOnlyList<string>? excludePatterns,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"同步源路径不存在：{sourcePath}");
        }

        Directory.CreateDirectory(targetPath);
        var sw = System.Diagnostics.Stopwatch.StartNew();

        long addedFiles = 0;
        long updatedFiles = 0;
        long deletedFiles = 0;
        long trashedFiles = 0;
        long bytesTransferred = 0;

        var sourceDir = new DirectoryInfo(sourcePath);
        var targetDir = new DirectoryInfo(targetPath);
        var normalizedExcludes = excludePatterns?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList() ?? [];

        var todayTrashDirName = $".lwt_trash_{DateTime.Now:yyyyMMdd}";
        var trashBaseDir = Path.Combine(targetPath, todayTrashDirName);

        // 1. 源到目标：增量同步新增与修改的文件
        var sourceFiles = sourceDir.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => !IsExcluded(Path.GetRelativePath(sourcePath, f.FullName), normalizedExcludes))
            .ToList();

        var sourceRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await Task.Run(() =>
        {
            foreach (var sf in sourceFiles)
            {
                ct.ThrowIfCancellationRequested();

                var rel = Path.GetRelativePath(sourcePath, sf.FullName);
                sourceRelPaths.Add(rel);

                var targetFilePath = Path.Combine(targetPath, rel);
                var targetFileDir = Path.GetDirectoryName(targetFilePath);
                if (!string.IsNullOrEmpty(targetFileDir) && !Directory.Exists(targetFileDir))
                {
                    Directory.CreateDirectory(targetFileDir);
                }

                if (!File.Exists(targetFilePath))
                {
                    sf.CopyTo(targetFilePath, overwrite: true);
                    File.SetLastWriteTimeUtc(targetFilePath, sf.LastWriteTimeUtc);
                    addedFiles++;
                    bytesTransferred += sf.Length;
                }
                else
                {
                    var tf = new FileInfo(targetFilePath);
                    if (tf.Length != sf.Length || Math.Abs((tf.LastWriteTimeUtc - sf.LastWriteTimeUtc).TotalSeconds) > 2)
                    {
                        sf.CopyTo(targetFilePath, overwrite: true);
                        File.SetLastWriteTimeUtc(targetFilePath, sf.LastWriteTimeUtc);
                        updatedFiles++;
                        bytesTransferred += sf.Length;
                    }
                }
            }
        }, ct);

        // 2. 目标清理：如果为单向镜像（OneWayMirror），处理目标端存在但源端已被删除的文件
        if (direction == SyncDirection.OneWayMirror)
        {
            await Task.Run(() =>
            {
                var targetFiles = targetDir.EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var rel = Path.GetRelativePath(targetPath, f.FullName).Replace('\\', '/');
                        // 忽略垃圾箱文件夹
                        return !rel.StartsWith(".lwt_trash") && !IsExcluded(rel, normalizedExcludes);
                    })
                    .ToList();

                foreach (var tf in targetFiles)
                {
                    ct.ThrowIfCancellationRequested();

                    var rel = Path.GetRelativePath(targetPath, tf.FullName);
                    if (!sourceRelPaths.Contains(rel))
                    {
                        if (deleteHandling == DeleteHandling.MoveToTrash)
                        {
                            var trashPath = Path.Combine(trashBaseDir, rel);
                            var trashDir = Path.GetDirectoryName(trashPath);
                            if (!string.IsNullOrEmpty(trashDir) && !Directory.Exists(trashDir))
                            {
                                Directory.CreateDirectory(trashDir);
                            }
                            if (File.Exists(trashPath))
                            {
                                File.Delete(trashPath);
                            }
                            File.Move(tf.FullName, trashPath);
                            trashedFiles++;
                        }
                        else
                        {
                            File.Delete(tf.FullName);
                            deletedFiles++;
                        }
                    }
                }

                // 清理空目录
                CleanEmptyDirectories(targetPath);
            }, ct);

            // 清理过期回收站
            if (deleteHandling == DeleteHandling.MoveToTrash && trashRetentionDays > 0)
            {
                CleanupOldTrash(targetPath, trashRetentionDays);
            }
        }

        sw.Stop();
        logger.LogInformation("同步完成：耗时 {Ms}ms，新增 {Add}，更新 {Update}，删除 {Del}，暂存回收站 {Trash}，传输 {Bytes} 字节",
            sw.ElapsedMilliseconds, addedFiles, updatedFiles, deletedFiles, trashedFiles, bytesTransferred);

        return new SyncExecutionResult
        {
            AddedFiles = addedFiles,
            UpdatedFiles = updatedFiles,
            DeletedFiles = deletedFiles,
            TrashedFiles = trashedFiles,
            TotalBytesTransferred = bytesTransferred,
            DurationMs = sw.ElapsedMilliseconds
        };
    }

    private static void CleanEmptyDirectories(string rootPath)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories)
                         .OrderByDescending(d => d.Length))
            {
                var name = Path.GetFileName(d);
                if (name.StartsWith(".lwt_trash")) continue;

                if (!Directory.EnumerateFileSystemEntries(d).Any())
                {
                    Directory.Delete(d, false);
                }
            }
        }
        catch
        {
            // 目录占用忽略
        }
    }

    private void CleanupOldTrash(string targetPath, int retentionDays)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-retentionDays);
            foreach (var dir in Directory.GetDirectories(targetPath, ".lwt_trash_*"))
            {
                var dirName = Path.GetFileName(dir);
                if (DateTime.TryParseExact(dirName[".lwt_trash_".Length..], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var date))
                {
                    if (date < cutoff)
                    {
                        Directory.Delete(dir, true);
                        logger.LogInformation("已清理过期同步回收站：{Dir}", dirName);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理过期同步回收站失败");
        }
    }

    private static bool IsExcluded(string relativePath, IReadOnlyList<string> excludes)
    {
        if (excludes.Count == 0) return false;
        var normalized = relativePath.Replace('\\', '/');

        foreach (var pattern in excludes)
        {
            var p = pattern.Replace('\\', '/').TrimStart('/');
            if (p.StartsWith("*."))
            {
                var ext = p[1..];
                if (normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (normalized.Contains(p, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
