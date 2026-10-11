using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class BackupSyncEnginesTests : IDisposable
{
    private readonly string _tempRoot;

    public BackupSyncEnginesTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "lwt_test_" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public async Task HardlinkEngine_创建增量快照成功并生成自描述Manifest()
    {
        var sourceDir = Path.Combine(_tempRoot, "src");
        var targetDir = Path.Combine(_tempRoot, "snapshots");
        Directory.CreateDirectory(sourceDir);

        File.WriteAllText(Path.Combine(sourceDir, "file1.txt"), "hello world");
        File.WriteAllText(Path.Combine(sourceDir, "file2.txt"), "foo bar");

        var engine = new HardlinkSnapshotEngine(NullLogger<HardlinkSnapshotEngine>.Instance);
        var taskId = Guid.NewGuid();

        // 第一次快照
        var snap1 = await engine.CreateSnapshotAsync(sourceDir, targetDir, taskId, null);
        Assert.NotNull(snap1);
        Assert.Equal(2, snap1.TotalFiles);
        Assert.True(File.Exists(Path.Combine(snap1.SnapshotPath, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(snap1.SnapshotPath, "file1.txt")));

        // 修改 file2，file1 保持不变
        await Task.Delay(50);
        File.WriteAllText(Path.Combine(sourceDir, "file2.txt"), "modified content!!");

        // 第二次快照
        var snap2 = await engine.CreateSnapshotAsync(sourceDir, targetDir, taskId, null);
        Assert.Equal(2, snap2.TotalFiles);
        Assert.NotEqual(snap1.SnapshotId, snap2.SnapshotId);
        Assert.Equal("hello world", File.ReadAllText(Path.Combine(snap2.SnapshotPath, "file1.txt")));
        Assert.Equal("modified content!!", File.ReadAllText(Path.Combine(snap2.SnapshotPath, "file2.txt")));
    }

    [Fact]
    public async Task MirrorSyncEngine_单向镜像与垃圾箱暂存()
    {
        var sourceDir = Path.Combine(_tempRoot, "mirror_src");
        var targetDir = Path.Combine(_tempRoot, "mirror_dst");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);

        File.WriteAllText(Path.Combine(sourceDir, "doc.txt"), "version 1");
        File.WriteAllText(Path.Combine(sourceDir, "extra.txt"), "keep");

        var syncEngine = new MirrorSyncEngine(NullLogger<MirrorSyncEngine>.Instance);

        // 首次同步
        var res1 = await syncEngine.ExecuteSyncAsync(
            sourceDir, targetDir, SyncDirection.OneWayMirror, DeleteHandling.MoveToTrash, 30, null);

        Assert.Equal(2, res1.AddedFiles);
        Assert.True(File.Exists(Path.Combine(targetDir, "doc.txt")));
        Assert.True(File.Exists(Path.Combine(targetDir, "extra.txt")));

        // 源端删除 extra.txt，修改 doc.txt
        File.Delete(Path.Combine(sourceDir, "extra.txt"));
        File.WriteAllText(Path.Combine(sourceDir, "doc.txt"), "version 2 modified");

        var res2 = await syncEngine.ExecuteSyncAsync(
            sourceDir, targetDir, SyncDirection.OneWayMirror, DeleteHandling.MoveToTrash, 30, null);

        Assert.Equal(1, res2.UpdatedFiles);
        Assert.Equal(1, res2.TrashedFiles);
        Assert.False(File.Exists(Path.Combine(targetDir, "extra.txt")));
        Assert.Equal("version 2 modified", File.ReadAllText(Path.Combine(targetDir, "doc.txt")));

        // 确认移入回收站
        var trashDirs = Directory.GetDirectories(targetDir, ".lwt_trash_*");
        Assert.Single(trashDirs);
        Assert.True(File.Exists(Path.Combine(trashDirs[0], "extra.txt")));
    }

    [Fact]
    public async Task SnapshotRestoreEngine_快照还原到新目录()
    {
        var snapDir = Path.Combine(_tempRoot, "snap_to_restore");
        var restoreDest = Path.Combine(_tempRoot, "restored_result");
        Directory.CreateDirectory(snapDir);

        File.WriteAllText(Path.Combine(snapDir, "a.txt"), "content A");
        Directory.CreateDirectory(Path.Combine(snapDir, "sub"));
        File.WriteAllText(Path.Combine(snapDir, "sub", "b.txt"), "content B");
        File.WriteAllText(Path.Combine(snapDir, "manifest.json"), "{}");

        var engine = new SnapshotRestoreEngine(NullLogger<SnapshotRestoreEngine>.Instance);
        var result = await engine.RestoreAsync(snapDir, restoreDest, cleanTargetBeforeRestore: true);

        Assert.Equal(2, result.RestoredFilesCount);
        Assert.True(File.Exists(Path.Combine(restoreDest, "a.txt")));
        Assert.True(File.Exists(Path.Combine(restoreDest, "sub", "b.txt")));
        Assert.False(File.Exists(Path.Combine(restoreDest, "manifest.json"))); // manifest 应被过滤
    }
}
