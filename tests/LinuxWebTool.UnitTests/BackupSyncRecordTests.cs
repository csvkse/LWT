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
}
