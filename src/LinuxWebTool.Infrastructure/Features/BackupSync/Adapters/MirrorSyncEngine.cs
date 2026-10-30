using System.Text;
using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Features.BackupSync.Progress;
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
        public long SkippedFiles { get; init; }
        public long DurationMs { get; init; }
    }

    public async Task<SyncExecutionResult> ExecuteSyncAsync(
        string sourcePath,
        string targetPath,
        SyncDirection direction,
        DeleteHandling deleteHandling,
        int trashRetentionDays,
        IReadOnlyList<string>? excludePatterns,
        IBackupProgressReporter? progressReporter = null,
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
        long skippedFiles = 0;

        var sourceDir = new DirectoryInfo(sourcePath);
        var targetDir = new DirectoryInfo(targetPath);
        var normalizedExcludes = ExcludePatternMatcher.NormalizePatterns(excludePatterns);

        var todayTrashDirName = $".lwt_trash_{DateTime.Now:yyyyMMdd}";
        var trashBaseDir = Path.Combine(targetPath, todayTrashDirName);

        // 1. 源到目标：增量同步新增与修改的文件（使用目录级剪枝跳过排除子树）
        progressReporter?.UpdatePhase(BackupExecutionPhase.Scanning, "正在扫描源目录并计算比对差异...");
        var sourceFiles = await Task.Run(() => ExcludePatternMatcher.EnumerateFilesWithPruning(sourceDir, sourcePath, normalizedExcludes, ct), ct);

        progressReporter?.SetTotalScanTarget(sourceFiles.Count, 0);
        progressReporter?.UpdatePhase(BackupExecutionPhase.Transferring, "正在镜像复制变动数据...");

        var sourceRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await Task.Run(() =>
        {
            foreach (var sf in sourceFiles)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var rel = Path.GetRelativePath(sourcePath, sf.FullName);
                    sourceRelPaths.Add(rel);

                    var targetFilePath = Path.Combine(targetPath, rel);
                    SymlinkAndFileTransferHelper.EnsureDirectoryCreated(targetFilePath);

                    // 1. 符号链接（包含悬空损坏的软链接）保真同步
                    if (SymlinkAndFileTransferHelper.IsSymbolicLink(sf))
                    {
                        var linkTarget = SymlinkAndFileTransferHelper.GetLinkTarget(sf);
                        if (!string.IsNullOrEmpty(linkTarget))
                        {
                            var targetLink = SymlinkAndFileTransferHelper.GetLinkTarget(new FileInfo(targetFilePath));
                            if (!string.Equals(linkTarget, targetLink, StringComparison.Ordinal))
                            {
                                if (SymlinkAndFileTransferHelper.CreateOrUpdateSymlink(targetFilePath, linkTarget, logger))
                                {
                                    if (targetLink == null) addedFiles++; else updatedFiles++;
                                    var linkBytes = Encoding.UTF8.GetByteCount(linkTarget);
                                    bytesTransferred += linkBytes;
                                    progressReporter?.ReportFileProcessed(linkBytes, rel);
                                }
                            }
                            else
                            {
                                progressReporter?.ReportFileProcessed(0, rel);
                            }
                            continue;
                        }
                    }

                    // 2. 普通文件安全长度与时间检测
                    if (!SymlinkAndFileTransferHelper.TryGetSafeLength(sf, out var sfLength))
                    {
                        logger.LogWarning("源文件在同步过程中消失或不可读，跳过：{Path}", sf.FullName);
                        skippedFiles++;
                        progressReporter?.ReportFileProcessed(0, sf.FullName, isSkipped: true);
                        continue;
                    }

                    if (!File.Exists(targetFilePath) || SymlinkAndFileTransferHelper.IsSymbolicLink(new FileInfo(targetFilePath)))
                    {
                        SymlinkAndFileTransferHelper.SafeDeleteFileOrLink(targetFilePath);
                        sf.CopyTo(targetFilePath, overwrite: true);
                        SymlinkAndFileTransferHelper.SafeSetLastWriteTimeUtc(targetFilePath, sf.LastWriteTimeUtc, logger);
                        addedFiles++;
                        bytesTransferred += sfLength;
                        progressReporter?.ReportFileProcessed(sfLength, rel);
                    }
                    else
                    {
                        var tf = new FileInfo(targetFilePath);
                        if (tf.Length != sfLength || Math.Abs((tf.LastWriteTimeUtc - sf.LastWriteTimeUtc).TotalSeconds) > 2)
                        {
                            sf.CopyTo(targetFilePath, overwrite: true);
                            SymlinkAndFileTransferHelper.SafeSetLastWriteTimeUtc(targetFilePath, sf.LastWriteTimeUtc, logger);
                            updatedFiles++;
                            bytesTransferred += sfLength;
                            progressReporter?.ReportFileProcessed(sfLength, rel);
                        }
                        else
                        {
                            // 文件无变动
                            progressReporter?.ReportFileProcessed(0, rel);
                        }
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "镜像同步过程中跳过异常文件：{Path}", sf.FullName);
                    skippedFiles++;
                    progressReporter?.ReportFileProcessed(0, sf.FullName, isSkipped: true);
                }
            }
        }, ct);

        // 2. 目标清理：如果为单向镜像（OneWayMirror），处理目标端存在但源端已被删除的文件
        if (direction == SyncDirection.OneWayMirror)
        {
            progressReporter?.UpdatePhase(BackupExecutionPhase.CleaningTarget, "正在比对并清理目标端差异文件...");
            await Task.Run(() =>
            {
                var targetExcludes = new List<string>(normalizedExcludes) { ".lwt_trash*" };
                var targetFiles = ExcludePatternMatcher.EnumerateFilesWithPruning(targetDir, targetPath, targetExcludes, ct);

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
                            progressReporter?.ReportItemsDeleted(1, 0);
                        }
                        else
                        {
                            File.Delete(tf.FullName);
                            deletedFiles++;
                            progressReporter?.ReportItemsDeleted(1, 0);
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
            SkippedFiles = skippedFiles,
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
}
