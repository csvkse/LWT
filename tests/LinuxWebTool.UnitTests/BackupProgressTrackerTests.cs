using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Features.BackupSync.Progress;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class BackupProgressTrackerTests
{
    [Fact]
    public void StartTracking_And_GetProgress_返回初始状态快照()
    {
        var tracker = new BackupProgressTracker();
        var taskId = Guid.NewGuid();

        var reporter = tracker.StartTracking(taskId, "测试备份", TaskMode.Backup);
        var snapshot = tracker.GetProgress(taskId);

        Assert.NotNull(snapshot);
        Assert.Equal(taskId, snapshot.TaskId);
        Assert.Equal("测试备份", snapshot.TaskName);
        Assert.Equal(BackupExecutionPhase.Idle, snapshot.Phase);
        Assert.False(snapshot.IsRunning);
        Assert.Equal(0, snapshot.ProcessedFiles);
        Assert.Equal(0, snapshot.ProcessedBytes);
    }

    [Fact]
    public void UpdatePhase_正确流转状态机阶段()
    {
        var tracker = new BackupProgressTracker();
        var taskId = Guid.NewGuid();
        var reporter = tracker.StartTracking(taskId, "同步任务", TaskMode.Sync);

        reporter.UpdatePhase(BackupExecutionPhase.PreflightChecking, "预检中");
        var snap1 = tracker.GetProgress(taskId);
        Assert.NotNull(snap1);
        Assert.Equal(BackupExecutionPhase.PreflightChecking, snap1.Phase);
        Assert.True(snap1.IsRunning);
        Assert.Equal("预检中", snap1.PhaseDescription);

        reporter.UpdatePhase(BackupExecutionPhase.Transferring);
        var snap2 = tracker.GetProgress(taskId);
        Assert.NotNull(snap2);
        Assert.Equal(BackupExecutionPhase.Transferring, snap2.Phase);
        Assert.True(snap2.IsRunning);
    }

    [Fact]
    public async Task ReportFileProcessed_多线程高并发原子无锁累加无丢失()
    {
        var tracker = new BackupProgressTracker();
        var taskId = Guid.NewGuid();
        var reporter = tracker.StartTracking(taskId, "并发压力测试", TaskMode.Backup);
        reporter.UpdatePhase(BackupExecutionPhase.Transferring);

        const int threads = 8;
        const int itemsPerThread = 2500;
        const long bytesPerItem = 100;

        var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < itemsPerThread; i++)
            {
                reporter.ReportFileProcessed(bytesPerItem, $"/path/item_{t}_{i}.txt", isSkipped: (i % 10 == 0));
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        var snapshot = tracker.GetProgress(taskId);
        Assert.NotNull(snapshot);
        Assert.Equal(threads * itemsPerThread, snapshot.ProcessedFiles);
        Assert.Equal(threads * itemsPerThread * bytesPerItem, snapshot.ProcessedBytes);
        Assert.Equal((threads * itemsPerThread) / 10, snapshot.SkippedFiles);
    }

    [Fact]
    public void GetSnapshot_准确计算百分比与完成归档()
    {
        var tracker = new BackupProgressTracker();
        var taskId = Guid.NewGuid();
        var reporter = tracker.StartTracking(taskId, "百分比测试", TaskMode.Backup);
        reporter.UpdatePhase(BackupExecutionPhase.Transferring);

        reporter.SetTotalScanTarget(1000, 100_000);
        reporter.ReportFileProcessed(50_000, "fileA.dat");
        for (var i = 1; i < 500; i++)
        {
            reporter.ReportFileProcessed(0, $"file_{i}.dat");
        }

        var snap = tracker.GetProgress(taskId);
        Assert.NotNull(snap);
        Assert.Equal(500, snap.ProcessedFiles);
        Assert.Equal(1000, snap.ScannedFiles);
        Assert.Equal(50.0, snap.FilePercent);
        Assert.Equal(50.0, snap.BytePercent);

        tracker.CompleteTracking(taskId, true);
        var finalSnap = tracker.GetProgress(taskId);
        Assert.NotNull(finalSnap);
        Assert.Equal(BackupExecutionPhase.Completed, finalSnap.Phase);
        Assert.False(finalSnap.IsRunning);
    }
}
