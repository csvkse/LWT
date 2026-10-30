using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Features.BackupSync.Progress;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// 跨平台硬链接增量快照引擎。
/// 未修改的文件使用文件系统硬链接引用上一快照，变动或新增文件直接复制，兼具极致性能与秒级独立回溯。
/// </summary>
public partial class HardlinkSnapshotEngine(ILogger<HardlinkSnapshotEngine> logger)
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkWindows(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkLinux(string oldpath, string newpath);

    public sealed record SnapshotCreationResult
    {
        public string SnapshotId { get; init; } = string.Empty;
        public string SnapshotPath { get; init; } = string.Empty;
        public long TotalFiles { get; init; }
        public long TotalSizeBytes { get; init; }
        public long HardlinkedFiles { get; init; }
        public long CopiedFiles { get; init; }
        public long SkippedFiles { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    /// <summary>
    /// 执行一次增量快照创建
    /// </summary>
    public async Task<SnapshotCreationResult> CreateSnapshotAsync(
        string sourcePath,
        string targetBasePath,
        Guid taskId,
        IReadOnlyList<string>? excludePatterns,
        IBackupProgressReporter? progressReporter = null,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(sourcePath))
        {
            throw new DirectoryNotFoundException($"源目录不存在：{sourcePath}");
        }

        Directory.CreateDirectory(targetBasePath);

        var now = DateTime.Now;
        var snapshotId = now.ToString("yyyyMMdd_HHmmss");
        var snapshotDir = Path.Combine(targetBasePath, snapshotId);

        // 避免极短时间内同名冲突
        var counter = 1;
        while (Directory.Exists(snapshotDir))
        {
            snapshotId = $"{now:yyyyMMdd_HHmmss}_{counter++}";
            snapshotDir = Path.Combine(targetBasePath, snapshotId);
        }

        Directory.CreateDirectory(snapshotDir);

        // 寻找上一份最新快照目录（作为 link-dest 参考基准）
        var previousSnapshotDir = FindLatestPreviousSnapshotDir(targetBasePath, snapshotId);
        if (!string.IsNullOrEmpty(previousSnapshotDir))
        {
            try
            {
                var prevManifestFile = Path.Combine(previousSnapshotDir, "manifest.json");
                if (File.Exists(prevManifestFile))
                {
                    var prevManifestJson = File.ReadAllText(prevManifestFile);
                    using var doc = JsonDocument.Parse(prevManifestJson);
                    if (doc.RootElement.TryGetProperty("TotalFiles", out var tfProp) && tfProp.GetInt64() > 0)
                    {
                        var prevTotalBytes = doc.RootElement.TryGetProperty("TotalSizeBytes", out var tbProp) ? tbProp.GetInt64() : 0;
                        progressReporter?.SetTotalScanTarget(tfProp.GetInt64(), prevTotalBytes);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "尝试读取上一快照 manifest 预估总进度失败");
            }
        }
        progressReporter?.UpdatePhase(BackupExecutionPhase.Transferring, "正在比对增量与建立快照...");

        long totalFiles = 0;
        long totalBytes = 0;
        long hardlinkedFiles = 0;
        long copiedFiles = 0;
        long skippedFiles = 0;

        var sourceDirInfo = new DirectoryInfo(sourcePath);
        var normalizedExcludes = excludePatterns?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToList() ?? [];

        await Task.Run(() =>
        {
            foreach (var file in sourceDirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var relativePath = Path.GetRelativePath(sourcePath, file.FullName);

                    // 检查排除通配符
                    if (IsExcluded(relativePath, normalizedExcludes))
                    {
                        continue;
                    }

                    var targetFilePath = Path.Combine(snapshotDir, relativePath);
                    SymlinkAndFileTransferHelper.EnsureDirectoryCreated(targetFilePath);

                    // 1. 符号链接（包含悬空损坏的软链接）保真处理：绝不跟随解引用，直接在目标端重建同名软链
                    if (SymlinkAndFileTransferHelper.IsSymbolicLink(file))
                    {
                        var linkTarget = SymlinkAndFileTransferHelper.GetLinkTarget(file);
                        if (!string.IsNullOrEmpty(linkTarget))
                        {
                            if (SymlinkAndFileTransferHelper.CreateOrUpdateSymlink(targetFilePath, linkTarget, logger))
                            {
                                totalFiles++;
                                copiedFiles++;
                                var linkBytes = Encoding.UTF8.GetByteCount(linkTarget);
                                totalBytes += linkBytes;
                                progressReporter?.ReportFileProcessed(linkBytes, relativePath);
                            }
                            else
                            {
                                skippedFiles++;
                                progressReporter?.ReportFileProcessed(0, relativePath, isSkipped: true);
                            }
                            continue;
                        }
                    }

                    // 2. 普通文件：安全读取大小与修改时间（防御并发消失文件）
                    if (!SymlinkAndFileTransferHelper.TryGetSafeLength(file, out var fileLength))
                    {
                        logger.LogWarning("文件在快照过程中消失或不可读，跳过：{Path}", file.FullName);
                        skippedFiles++;
                        progressReporter?.ReportFileProcessed(0, file.FullName, isSkipped: true);
                        continue;
                    }

                    totalFiles++;
                    totalBytes += fileLength;

                    var linked = false;
                    if (!string.IsNullOrEmpty(previousSnapshotDir))
                    {
                        var previousFilePath = Path.Combine(previousSnapshotDir, relativePath);
                        if (File.Exists(previousFilePath) && !SymlinkAndFileTransferHelper.IsSymbolicLink(new FileInfo(previousFilePath)))
                        {
                            try
                            {
                                var prevFileInfo = new FileInfo(previousFilePath);
                                // 若文件大小与修改时间一致，尝试硬链接
                                if (prevFileInfo.Length == fileLength &&
                                    Math.Abs((prevFileInfo.LastWriteTimeUtc - file.LastWriteTimeUtc).TotalSeconds) < 2)
                                {
                                    linked = TryCreateHardlink(previousFilePath, targetFilePath);
                                    if (linked)
                                    {
                                        hardlinkedFiles++;
                                        progressReporter?.ReportFileProcessed(fileLength, relativePath);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug(ex, "尝试复用上一快照硬链接失败，将执行常规复制：{Path}", previousFilePath);
                            }
                        }
                    }

                    if (!linked)
                    {
                        // 新增或已修改文件：直接复制并保留时间戳
                        SymlinkAndFileTransferHelper.SafeDeleteFileOrLink(targetFilePath);
                        file.CopyTo(targetFilePath, overwrite: true);
                        File.SetLastWriteTimeUtc(targetFilePath, file.LastWriteTimeUtc);
                        copiedFiles++;
                        progressReporter?.ReportFileProcessed(fileLength, relativePath);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "跳过异常或并发变动文件：{Path}", file.FullName);
                    skippedFiles++;
                    progressReporter?.ReportFileProcessed(0, file.FullName, isSkipped: true);
                }
            }
        }, ct);

        // 在快照根目录写入 manifest.json 记录元数据，确保自描述性
        var manifestPath = Path.Combine(snapshotDir, "manifest.json");
        var manifestData = new SnapshotManifest(
            snapshotId,
            taskId,
            now,
            sourcePath,
            totalFiles,
            totalBytes,
            hardlinkedFiles,
            copiedFiles);

        var json = JsonSerializer.Serialize(manifestData, BackupSnapshotJsonContext.Default.SnapshotManifest);
        await File.WriteAllTextAsync(manifestPath, json, ct);

        logger.LogInformation("增量快照创建完成：{Id}，文件 {TotalFiles}（硬链 {Linked}，复制 {Copied}），总容量 {Size} 字节",
            snapshotId, totalFiles, hardlinkedFiles, copiedFiles, totalBytes);

        return new SnapshotCreationResult
        {
            SnapshotId = snapshotId,
            SnapshotPath = snapshotDir,
            TotalFiles = totalFiles,
            TotalSizeBytes = totalBytes,
            HardlinkedFiles = hardlinkedFiles,
            CopiedFiles = copiedFiles,
            SkippedFiles = skippedFiles,
            CreatedAt = now
        };
    }

    /// <summary>
    /// 尝试创建硬链接，跨卷或平台限制失败时平滑降级为复制
    /// </summary>
    public static bool TryCreateHardlink(string sourceExistingPath, string newLinkPath)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return CreateHardLinkWindows(newLinkPath, sourceExistingPath, IntPtr.Zero);
            }
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var ret = LinkLinux(sourceExistingPath, newLinkPath);
                return ret == 0;
            }
        }
        catch
        {
            // 降级由调用方处理
        }
        return false;
    }

    private static string? FindLatestPreviousSnapshotDir(string targetBasePath, string currentSnapshotId)
    {
        if (!Directory.Exists(targetBasePath)) return null;

        var candidateDirs = Directory.GetDirectories(targetBasePath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name) && !name.Equals(currentSnapshotId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(name => name)
            .ToList();

        foreach (var dirName in candidateDirs)
        {
            var fullPath = Path.Combine(targetBasePath, dirName!);
            // 排除临时或回收站目录
            if (dirName!.StartsWith(".trash") || dirName.StartsWith(".tmp"))
            {
                continue;
            }
            return fullPath;
        }

        return null;
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

    /// <summary>
    /// 构建快照文件浏览目录树
    /// </summary>
    public static List<SnapshotFileNode> BuildSnapshotFileTree(string snapshotDir, string subPath = "", int maxDepth = 3)
    {
        var targetDir = string.IsNullOrEmpty(subPath) ? snapshotDir : Path.Combine(snapshotDir, subPath);
        if (!Directory.Exists(targetDir))
        {
            return [];
        }

        var dirInfo = new DirectoryInfo(targetDir);
        var result = new List<SnapshotFileNode>();

        foreach (var dir in dirInfo.EnumerateDirectories().OrderBy(d => d.Name))
        {
            var rel = Path.GetRelativePath(snapshotDir, dir.FullName).Replace('\\', '/');
            result.Add(new SnapshotFileNode
            {
                Name = dir.Name,
                RelativePath = rel,
                IsDirectory = true,
                SizeBytes = 0,
                LastModified = dir.LastWriteTimeUtc,
                Children = maxDepth > 1 ? BuildSnapshotFileTree(snapshotDir, rel, maxDepth - 1) : null
            });
        }

        foreach (var file in dirInfo.EnumerateFiles().OrderBy(f => f.Name))
        {
            if (file.Name.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var rel = Path.GetRelativePath(snapshotDir, file.FullName).Replace('\\', '/');
            result.Add(new SnapshotFileNode
            {
                Name = file.Name,
                RelativePath = rel,
                IsDirectory = false,
                SizeBytes = file.Length,
                LastModified = file.LastWriteTimeUtc
            });
        }

        return result;
    }
}

public sealed record SnapshotManifest(
    string SnapshotId,
    Guid TaskId,
    DateTime CreatedAt,
    string SourcePath,
    long TotalFiles,
    long TotalSizeBytes,
    long HardlinkedFiles,
    long CopiedFiles);

[System.Text.Json.Serialization.JsonSerializable(typeof(SnapshotManifest))]
internal partial class BackupSnapshotJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}

