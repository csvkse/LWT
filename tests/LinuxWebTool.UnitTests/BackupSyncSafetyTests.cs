using LinuxWebTool.Contracts.Features.Mount.Contracts;
using LinuxWebTool.Infrastructure.Features.BackupSync.Safety;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class BackupSyncSafetyTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DbConnectionFactory _factory;
    private readonly BackupSyncStore _store;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _keepAliveConnection;

    public BackupSyncSafetyTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "lwt_safety_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);

        var dbName = "safetypool_" + Guid.NewGuid().ToString("N");
        _factory = new DbConnectionFactory($"Data Source={dbName};Mode=Memory;Cache=Shared;Default Timeout=30");
        _keepAliveConnection = _factory.CreateConnection();
        _keepAliveConnection.Open();
        DbSetup.Initialize(_factory);
        _store = new BackupSyncStore(_factory);
    }

    public void Dispose()
    {
        try
        {
            _keepAliveConnection.Dispose();
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task EmptySourceDisaster_当源端为空但目标端已存在数据时_单向镜像触发灾难熔断()
    {
        var srcDir = Path.Combine(_tempRoot, "empty_src");
        var targetDir = Path.Combine(_tempRoot, "populated_dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(targetDir);

        File.WriteAllText(Path.Combine(targetDir, "important_file.data"), "critical database");

        var task = new BackupSyncTask
        {
            Id = Guid.NewGuid(),
            Name = "生产数据同步测试",
            Mode = (int)TaskMode.Sync,
            SyncDirection = (int)SyncDirection.OneWayMirror,
            SourcePath = srcDir,
            TargetPath = targetDir,
            Enabled = true
        };

        var strategy = new EmptySourceDisasterPreventionStrategy(_store, NullLogger<EmptySourceDisasterPreventionStrategy>.Instance);
        var context = new PreflightContext { Task = task };

        var result = await strategy.EvaluateAsync(task, context);

        Assert.False(result.IsPassed);
        Assert.True(result.IsFatal);
        Assert.Contains("灾难清空熔断拦截", result.Message);
    }

    [Fact]
    public async Task EmptySourceDisaster_增量备份时源端为空且历史快照非空_触发防污染熔断()
    {
        var srcDir = Path.Combine(_tempRoot, "empty_backup_src");
        var targetDir = Path.Combine(_tempRoot, "backup_snapshots");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(targetDir);

        var task = new BackupSyncTask
        {
            Id = Guid.NewGuid(),
            Name = "开发工程增量备份",
            Mode = (int)TaskMode.Backup,
            SourcePath = srcDir,
            TargetPath = targetDir,
            Enabled = true
        };
        await _store.InsertTaskAsync(task);

        // 插入一条历史快照记录，证明该任务此前包含真实数据
        await _store.InsertSnapshotAsync(new BackupSnapshot
        {
            SnapshotId = "20261026_010000",
            TaskId = task.Id,
            CreatedAt = DateTime.Now.AddDays(-1),
            TotalFiles = 10,
            TotalSizeBytes = 50000,
            SnapshotPath = Path.Combine(targetDir, "20261026_010000")
        });

        var strategy = new EmptySourceDisasterPreventionStrategy(_store, NullLogger<EmptySourceDisasterPreventionStrategy>.Instance);
        var context = new PreflightContext { Task = task };

        var result = await strategy.EvaluateAsync(task, context);

        Assert.False(result.IsPassed);
        Assert.True(result.IsFatal);
        Assert.Contains("增量备份熔断拦截", result.Message);
    }

    [Fact]
    public async Task StateMachine_连续失败达3次后_断路器自动跳闸为AutoPaused()
    {
        var task = new BackupSyncTask
        {
            Id = Guid.NewGuid(),
            Name = "网络不稳任务",
            Mode = (int)TaskMode.Sync,
            SourcePath = "/mnt/nas/data",
            TargetPath = "/backup/local",
            Enabled = true
        };
        await _store.InsertTaskAsync(task);

        var opLogger = new OperationLogger(new OperationLogStore(_factory), NullLogger<OperationLogger>.Instance);
        var scheduler = new DummyScheduler();
        var recordStore = new BackupSyncRecordStore(_factory);
        var retention = new LinuxWebTool.Infrastructure.Features.BackupSync.Retention.SmartHybridRecordRetentionStrategy();
        var sm = new BackupSyncStateMachine(_store, recordStore, retention, scheduler, opLogger, NullLogger<BackupSyncStateMachine>.Instance);

        // 第一次失败
        await sm.RecordFailureAsync(task, "网络连接超时 #1", null);
        var t1 = await _store.GetTaskByIdAsync(task.Id);
        Assert.Equal(BackupSyncStateMachine.StatusFailed, t1!.LastRunStatus);
        Assert.Equal(1, t1.ConsecutiveFailures);

        // 第二次失败
        await sm.RecordFailureAsync(task, "网络连接超时 #2", null);
        var t2 = await _store.GetTaskByIdAsync(task.Id);
        Assert.Equal(BackupSyncStateMachine.StatusFailed, t2!.LastRunStatus);
        Assert.Equal(2, t2.ConsecutiveFailures);

        // 第三次失败 -> 触发断路器熔断自动暂停
        await sm.RecordFailureAsync(task, "网络连接超时 #3", null);
        var t3 = await _store.GetTaskByIdAsync(task.Id);
        Assert.Equal(BackupSyncStateMachine.StatusAutoPaused, t3!.LastRunStatus);
        Assert.Equal(3, t3.ConsecutiveFailures);
        Assert.NotNull(t3.AutoPausedReason);
        Assert.Contains("连续失败 3 次", t3.AutoPausedReason);
    }

    [Fact]
    public async Task StateMachine_手动恢复AutoPaused任务_重置为Idle且清零失败计数()
    {
        var task = new BackupSyncTask
        {
            Id = Guid.NewGuid(),
            Name = "待恢复熔断任务",
            Mode = (int)TaskMode.Backup,
            SourcePath = "/data",
            TargetPath = "/backup",
            Enabled = true,
            LastRunStatus = BackupSyncStateMachine.StatusAutoPaused,
            AutoPausedReason = "挂载异常自动熔断",
            ConsecutiveFailures = 4
        };
        await _store.InsertTaskAsync(task);

        var opLogger = new OperationLogger(new OperationLogStore(_factory), NullLogger<OperationLogger>.Instance);
        var scheduler = new DummyScheduler();
        var recordStore = new BackupSyncRecordStore(_factory);
        var retention = new LinuxWebTool.Infrastructure.Features.BackupSync.Retention.SmartHybridRecordRetentionStrategy();
        var sm = new BackupSyncStateMachine(_store, recordStore, retention, scheduler, opLogger, NullLogger<BackupSyncStateMachine>.Instance);

        var resumed = await sm.ResumeTaskAsync(task.Id);

        Assert.Equal(BackupSyncStateMachine.StatusIdle, resumed.LastRunStatus);
        Assert.Null(resumed.AutoPausedReason);
        Assert.Equal(0, resumed.ConsecutiveFailures);

        var fromDb = await _store.GetTaskByIdAsync(task.Id);
        Assert.Equal(BackupSyncStateMachine.StatusIdle, fromDb!.LastRunStatus);
        Assert.Null(fromDb.AutoPausedReason);
        Assert.Equal(0, fromDb.ConsecutiveFailures);
    }

    [Fact]
    public async Task LocalFileSystemAdapter_目标目录写入Canary探针且正常清理()
    {
        var targetDir = Path.Combine(_tempRoot, "canary_test_dir");
        var adapter = new LocalFileSystemPathAdapter(NullLogger<LocalFileSystemPathAdapter>.Instance);

        var report = await adapter.InspectAsync(targetDir, PathRole.Target);

        Assert.True(report.IsAccessible);
        Assert.True(report.IsWritable);
        Assert.True(Directory.Exists(targetDir));

        // 验证探针文件已被清理
        var tmpFiles = Directory.GetFiles(targetDir, ".lwt_canary_*");
        Assert.Empty(tmpFiles);
    }

    private sealed class DummyScheduler : IBackupSyncScheduler
    {
        public Task<DateTime?> SyncAsync(BackupSyncTask task) => Task.FromResult<DateTime?>(DateTime.Now.AddDays(1));
        public Task RemoveAsync(Guid taskId) => Task.CompletedTask;
        public Task TriggerNowAsync(BackupSyncTask task) => Task.CompletedTask;
        public Task SyncAllAsync(IEnumerable<BackupSyncTask> tasks) => Task.CompletedTask;
    }
}
