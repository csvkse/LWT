using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class BackupSyncSymlinkTests : IDisposable
{
    private readonly string _tempRoot;

    public BackupSyncSymlinkTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "lwt_symlink_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }

    private bool TryCreateTestSymlink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException))
        {
            // Windows 开发机未开启开发者模式时跳过软链创建
            return false;
        }
    }

    [Fact]
    public void SymlinkAndFileTransferHelper_BrokenSymlink_DoesNotThrowFileNotFound()
    {
        var linkPath = Path.Combine(_tempRoot, "broken_link.txt");
        var nonExistentTarget = Path.Combine(_tempRoot, "does_not_exist_file.txt");

        if (!TryCreateTestSymlink(linkPath, nonExistentTarget)) return;

        var fileInfo = new FileInfo(linkPath);

        // 1. 验证准确识别为符号链接
        Assert.True(SymlinkAndFileTransferHelper.IsSymbolicLink(fileInfo));

        // 2. 验证能够安全提取目标字符串而不抛出 FileNotFoundException
        var target = SymlinkAndFileTransferHelper.GetLinkTarget(fileInfo);
        Assert.Equal(nonExistentTarget, target);

        // 3. 验证安全获取长度不崩溃
        var hasLength = SymlinkAndFileTransferHelper.TryGetSafeLength(fileInfo, out var length);
        Assert.True(hasLength);
        Assert.True(length > 0);
    }

    [Fact]
    public async Task CreateSnapshot_WithBrokenSymlink_SucceedsAndCreatesSymlinkInSnapshot()
    {
        var sourceDir = Path.Combine(_tempRoot, "source");
        var backupTargetDir = Path.Combine(_tempRoot, "snapshots");
        Directory.CreateDirectory(sourceDir);

        // 创建普通文件
        File.WriteAllText(Path.Combine(sourceDir, "normal.txt"), "hello world");

        // 创建悬空符号链接（模拟容器内漏掉的 node_modules 链接）
        var brokenLink = Path.Combine(sourceDir, "detect-libc");
        var brokenTarget = "/usr/local/lib/node_modules/@deepseek-ai/dsh/node_modules/detect-libc";

        if (!TryCreateTestSymlink(brokenLink, brokenTarget)) return;

        var engine = new HardlinkSnapshotEngine(NullLogger<HardlinkSnapshotEngine>.Instance);
        var result = await engine.CreateSnapshotAsync(sourceDir, backupTargetDir, Guid.NewGuid(), null);

        Assert.NotNull(result);
        Assert.True(result.TotalFiles >= 2);

        // 验证快照目录中生成的软链接
        var snapshotDir = result.SnapshotPath;
        var copiedLinkPath = Path.Combine(snapshotDir, "detect-libc");

        Assert.True(File.Exists(copiedLinkPath) || SymlinkAndFileTransferHelper.IsSymbolicLink(new FileInfo(copiedLinkPath)));
        var resolvedTarget = SymlinkAndFileTransferHelper.GetLinkTarget(new FileInfo(copiedLinkPath));
        Assert.Equal(brokenTarget, resolvedTarget);
    }

    [Fact]
    public async Task MirrorSync_WithBrokenSymlink_ReplicatesSymlinkAccurately()
    {
        var sourceDir = Path.Combine(_tempRoot, "sync_source");
        var targetDir = Path.Combine(_tempRoot, "sync_target");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "data.txt"), "sync content");

        var brokenLink = Path.Combine(sourceDir, "broken_symlink");
        var brokenTarget = "/opt/non_existent_path/library.so";

        if (!TryCreateTestSymlink(brokenLink, brokenTarget)) return;

        var engine = new MirrorSyncEngine(NullLogger<MirrorSyncEngine>.Instance);
        var result = await engine.ExecuteSyncAsync(
            sourceDir,
            targetDir,
            SyncDirection.OneWayMirror,
            DeleteHandling.PermanentDelete,
            trashRetentionDays: 0,
            excludePatterns: null);

        Assert.NotNull(result);
        Assert.True(result.AddedFiles >= 2);

        var targetLinkPath = Path.Combine(targetDir, "broken_symlink");
        Assert.True(File.Exists(targetLinkPath) || SymlinkAndFileTransferHelper.IsSymbolicLink(new FileInfo(targetLinkPath)));
        Assert.Equal(brokenTarget, SymlinkAndFileTransferHelper.GetLinkTarget(new FileInfo(targetLinkPath)));
    }
}
