using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.Infrastructure.Features.BackupSync.Retention;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class BackupSyncRecordTests : IDisposable
{
    private readonly DbConnectionFactory _factory;
    private readonly BackupSyncRecordStore _recordStore;
    private readonly Microsoft.Data.Sqlite.SqliteConnection _keepAliveConnection;

    public BackupSyncRecordTests()
    {
        var dbName = "recordpool_" + Guid.NewGuid().ToString("N");
        _factory = new DbConnectionFactory($"Data Source={dbName};Mode=Memory;Cache=Shared;Default Timeout=30");
        _keepAliveConnection = _factory.CreateConnection();
        _keepAliveConnection.Open();
        DbSetup.Initialize(_factory);
        _recordStore = new BackupSyncRecordStore(_factory);
    }

    public void Dispose()
    {
        try
        {
            _keepAliveConnection.Dispose();
        }
        catch { }
    }

    [Fact]
    public async Task InsertAndUpdateRecord_SucceedsAndCalculatesMetrics()
    {
        var taskId = Guid.NewGuid();
        var record = new BackupSyncRecord
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            TaskName = "测试定时同步",
            Mode = 1,
            TriggerType = 2,
            Status = "Running",
            StartTime = DateTime.Now,
            ClientIp = "127.0.0.1"
        };

        var id = await _recordStore.InsertRecordAsync(record);
        Assert.Equal(record.Id, id);

        var retrieved = await _recordStore.GetRecordByIdAsync(id);
        Assert.NotNull(retrieved);
        Assert.Equal("Running", retrieved.Status);

        // 更新完成状态
        await _recordStore.UpdateRecordCompletionAsync(
            id,
            "Success",
            DateTime.Now,
            durationMs: 1500,
            processedFiles: 42,
            processedBytes: 1048576,
            addedFiles: 10,
            updatedFiles: 30,
            deletedFiles: 2,
            skippedFiles: 1,
            summary: "同步成功完成",
            errorMessage: null);

        var finished = await _recordStore.GetRecordByIdAsync(id);
        Assert.NotNull(finished);
        Assert.Equal("Success", finished.Status);
        Assert.Equal(1500, finished.DurationMs);
        Assert.Equal(42, finished.ProcessedFiles);
        Assert.Equal(1, finished.SkippedFiles);
        Assert.Equal("同步成功完成", finished.Summary);
    }

    [Fact]
    public async Task QueryRecords_SupportsFilterAndPagination()
    {
        var task1 = Guid.NewGuid();
        var task2 = Guid.NewGuid();

        for (int i = 0; i < 15; i++)
        {
            await _recordStore.InsertRecordAsync(new BackupSyncRecord
            {
                Id = Guid.NewGuid(),
                TaskId = task1,
                TaskName = "任务1",
                Mode = 2,
                TriggerType = 1,
                Status = i % 2 == 0 ? "Success" : "Failed",
                StartTime = DateTime.Now.AddMinutes(-i)
            });
        }

        for (int i = 0; i < 5; i++)
        {
            await _recordStore.InsertRecordAsync(new BackupSyncRecord
            {
                Id = Guid.NewGuid(),
                TaskId = task2,
                TaskName = "任务2",
                Mode = 1,
                TriggerType = 2,
                Status = "Success",
                StartTime = DateTime.Now.AddMinutes(-i)
            });
        }

        // 1. 查询全部分页
        var pagedAll = await _recordStore.QueryRecordsAsync(new BackupSyncRecordQuery { Page = 1, PageSize = 10 });
        Assert.Equal(20, pagedAll.Total);
        Assert.Equal(10, pagedAll.Items.Count);

        // 2. 按 TaskId 过滤
        var pagedTask1 = await _recordStore.QueryRecordsAsync(new BackupSyncRecordQuery { TaskId = task1, Page = 1, PageSize = 20 });
        Assert.Equal(15, pagedTask1.Total);
        Assert.All(pagedTask1.Items, r => Assert.Equal(task1, r.TaskId));

        // 3. 按 Status 过滤
        var pagedFailed = await _recordStore.QueryRecordsAsync(new BackupSyncRecordQuery { Status = "Failed", Page = 1, PageSize = 20 });
        Assert.Equal(7, pagedFailed.Total);
        Assert.All(pagedFailed.Items, r => Assert.Equal("Failed", r.Status));
    }

    [Fact]
    public async Task SmartHybridRetention_PrunesOldRecordsWhileProtectingRecentFailures()
    {
        var taskId = Guid.NewGuid();
        var retention = new SmartHybridRecordRetentionStrategy
        {
            KeepLatestCount = 5,
            RetentionDays = 7
        };

        // 写入 10 条超期成功的记录（10 天前）
        for (int i = 0; i < 10; i++)
        {
            await _recordStore.InsertRecordAsync(new BackupSyncRecord
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                TaskName = "修剪测试任务",
                Mode = 2,
                TriggerType = 2,
                Status = "Success",
                StartTime = DateTime.Now.AddDays(-10).AddMinutes(-i)
            });
        }

        // 写入 1 条超期失败的记录（10 天前，应当享受故障现场豁免保护）
        var failedId = Guid.NewGuid();
        await _recordStore.InsertRecordAsync(new BackupSyncRecord
        {
            Id = failedId,
            TaskId = taskId,
            TaskName = "修剪测试任务",
            Mode = 2,
            TriggerType = 2,
            Status = "Failed",
            StartTime = DateTime.Now.AddDays(-10).AddMinutes(-100),
            ErrorMessage = "关键挂载异常"
        });

        // 写入 5 条最新成功的记录（1 小时内，应当保底保留）
        for (int i = 0; i < 5; i++)
        {
            await _recordStore.InsertRecordAsync(new BackupSyncRecord
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                TaskName = "修剪测试任务",
                Mode = 2,
                TriggerType = 2,
                Status = "Success",
                StartTime = DateTime.Now.AddMinutes(-i)
            });
        }

        // 总计 16 条记录
        var beforePrune = await _recordStore.QueryRecordsAsync(new BackupSyncRecordQuery { TaskId = taskId });
        Assert.Equal(16, beforePrune.Total);

        // 执行智能修剪
        await retention.PruneRecordsAsync(taskId, _recordStore);

        // 校验修剪后结果
        var afterPrune = await _recordStore.QueryRecordsAsync(new BackupSyncRecordQuery { TaskId = taskId, PageSize = 50 });
        // 保留了最新 5 条 + 豁免的 1 条故障现场 = 6 条
        Assert.Equal(6, afterPrune.Total);
        var failedRecord = await _recordStore.GetRecordByIdAsync(failedId);
        Assert.NotNull(failedRecord);
        Assert.Equal("Failed", failedRecord.Status);
    }

    [Fact]
    public async Task MarkInterruptedRunningRecords_RecoversZombieRunningState()
    {
        var zombieId = Guid.NewGuid();
        await _recordStore.InsertRecordAsync(new BackupSyncRecord
        {
            Id = zombieId,
            TaskId = Guid.NewGuid(),
            TaskName = "僵尸任务",
            Mode = 1,
            TriggerType = 2,
            Status = "Running",
            StartTime = DateTime.Now.AddHours(-2)
        });

        var changed = await _recordStore.MarkInterruptedRunningRecordsAsync();
        Assert.True(changed >= 1);

        var zombie = await _recordStore.GetRecordByIdAsync(zombieId);
        Assert.NotNull(zombie);
        Assert.Equal("Interrupted", zombie.Status);
        Assert.NotNull(zombie.ErrorMessage);
        Assert.Contains("中断", zombie.ErrorMessage);
    }

    [Fact]
    public async Task UpdateProgressCheckpoint_UpdatesInFlightMetricsWhileRunning()
    {
        var recordId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        await _recordStore.InsertRecordAsync(new BackupSyncRecord
        {
            Id = recordId,
            TaskId = taskId,
            TaskName = "大文件传输任务",
            Mode = 1,
            TriggerType = 1,
            Status = "Running",
            StartTime = DateTime.Now,
            ProcessedFiles = 0,
            ProcessedBytes = 0,
            DurationMs = 0
        });

        // 第一次检查点：处理了 500 个文件
        await _recordStore.UpdateProgressCheckpointAsync(recordId, 5000, 500, 1024000, "正在传输 (500/10000)");
        var r1 = await _recordStore.GetRecordByIdAsync(recordId);
        Assert.NotNull(r1);
        Assert.Equal("Running", r1.Status);
        Assert.Equal(5000, r1.DurationMs);
        Assert.Equal(500, r1.ProcessedFiles);
        Assert.Equal(1024000, r1.ProcessedBytes);
        Assert.Equal("正在传输 (500/10000)", r1.Summary);

        // 第二次检查点：处理了 2500 个文件
        await _recordStore.UpdateProgressCheckpointAsync(recordId, 25000, 2500, 8192000, "正在传输 (2500/10000)");
        var r2 = await _recordStore.GetRecordByIdAsync(recordId);
        Assert.NotNull(r2);
        Assert.Equal("Running", r2.Status);
        Assert.Equal(25000, r2.DurationMs);
        Assert.Equal(2500, r2.ProcessedFiles);
        Assert.Equal(8192000, r2.ProcessedBytes);

        // 如果记录已经变为 Success，UpdateProgressCheckpoint 不会误覆盖
        await _recordStore.UpdateRecordCompletionAsync(
            recordId, "Success", DateTime.Now, 30000, 10000, 50000000, 10000, 0, 0, 0, "全部完成", null);
        await _recordStore.UpdateProgressCheckpointAsync(recordId, 99999, 0, 0, "迟到的检查点");
        var rFinal = await _recordStore.GetRecordByIdAsync(recordId);
        Assert.NotNull(rFinal);
        Assert.Equal("Success", rFinal.Status);
        Assert.Equal("全部完成", rFinal.Summary);
    }

    [Fact]
    public async Task GetRunningRecordsByTaskId_ReturnsOnlyRunningRecords()
    {
        var taskId = Guid.NewGuid();
        var r1 = Guid.NewGuid();
        var r2 = Guid.NewGuid();
        await _recordStore.InsertRecordAsync(new BackupSyncRecord
        {
            Id = r1,
            TaskId = taskId,
            TaskName = "任务A",
            Mode = 1,
            TriggerType = 1,
            Status = "Running",
            StartTime = DateTime.Now
        });
        await _recordStore.InsertRecordAsync(new BackupSyncRecord
        {
            Id = r2,
            TaskId = taskId,
            TaskName = "任务A",
            Mode = 1,
            TriggerType = 1,
            Status = "Success",
            StartTime = DateTime.Now.AddMinutes(-10)
        });

        var running = (await _recordStore.GetRunningRecordsByTaskIdAsync(taskId)).ToList();
        Assert.Single(running);
        Assert.Equal(r1, running[0].Id);
    }
}
