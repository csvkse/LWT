using System.Net;
using System.Net.Http.Json;
using LinuxWebTool.Contracts.Features.BackupSync.Contracts;
using LinuxWebTool.IntegrationTests.Support;
using LinuxWebTool.WebHost.Composition;
using Xunit;

namespace LinuxWebTool.IntegrationTests;

public class BackupSyncIntegrationTests : IDisposable
{
    private readonly string _testWorkspace;

    public BackupSyncIntegrationTests()
    {
        _testWorkspace = Path.Combine(Path.GetTempPath(), "lwt_integ_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testWorkspace);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testWorkspace))
            {
                Directory.Delete(_testWorkspace, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task CronPreview_返回规范化Cron与预测触发时间()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();
        var request = new CronPreviewRequest
        {
            CronExpression = "0 0 3 * * ?",
            Count = 5
        };

        var response = await client.PostAsJsonAsync("/api/BackupSync/CronPreview", request, AppJsonSerializerContext.Default.CronPreviewRequest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var result = await response.Content.ReadFromJsonAsync<CronPreviewResponse>(AppJsonSerializerContext.Default.CronPreviewResponse);
        Assert.NotNull(result);
        Assert.True(result.IsValid);
        Assert.Equal(5, result.NextRunTimes.Count);
        Assert.All(result.NextRunTimes, dt => Assert.Equal(3, dt.Hour));
    }

    [Fact]
    public async Task 备份任务端到端生命周期_创建_触发_快照查询_锁定保护()
    {
        var client = await TestServerFixture.CreateAdminClientAsync();

        var srcDir = Path.Combine(_testWorkspace, "src");
        var dstDir = Path.Combine(_testWorkspace, "dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        File.WriteAllText(Path.Combine(srcDir, "test.txt"), "Integration Test Data");

        // 1. 创建备份任务
        var createReq = new SaveBackupSyncTaskRequest
        {
            Name = "集成测试备份",
            Mode = TaskMode.Backup,
            SourcePath = srcDir,
            TargetPath = dstDir,
            CronExpression = "0 0 2 * * ?",
            KeepLastCount = 5,
            KeepDays = 7,
            KeepWeeks = 4,
            KeepMonths = 12
        };

        var createResp = await client.PostAsJsonAsync("/api/BackupSync/Tasks", createReq, AppJsonSerializerContext.Default.SaveBackupSyncTaskRequest);
        Assert.Equal(HttpStatusCode.OK, createResp.StatusCode);

        var createdTask = await createResp.Content.ReadFromJsonAsync<BackupSyncTaskItem>(AppJsonSerializerContext.Default.BackupSyncTaskItem);
        Assert.NotNull(createdTask);
        Assert.Equal("集成测试备份", createdTask.Name);
        var taskId = createdTask.Id;

        try
        {
            // 2. 查询任务列表包含新增任务
            var listResp = await client.GetAsync("/api/BackupSync/Tasks");
            Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
            var tasks = await listResp.Content.ReadFromJsonAsync<List<BackupSyncTaskItem>>(AppJsonSerializerContext.Default.ListBackupSyncTaskItem);
            Assert.NotNull(tasks);
            Assert.Contains(tasks, t => t.Id == taskId);

            // 3. 立即运行任务生成快照
            var runResp = await client.PostAsync($"/api/BackupSync/Tasks/{taskId}/Run", null);
            Assert.Equal(HttpStatusCode.OK, runResp.StatusCode);
            var runResult = await runResp.Content.ReadFromJsonAsync<TaskRunResultResponse>(AppJsonSerializerContext.Default.TaskRunResultResponse);
            Assert.NotNull(runResult);
            Assert.True(runResult.Success);
            Assert.Equal(1, runResult.ProcessedFiles);

            // 4. 查询该任务的历史快照
            var snapsResp = await client.GetAsync($"/api/BackupSync/Tasks/{taskId}/Snapshots");
            Assert.Equal(HttpStatusCode.OK, snapsResp.StatusCode);
            var snapshots = await snapsResp.Content.ReadFromJsonAsync<List<BackupSnapshotItem>>(AppJsonSerializerContext.Default.ListBackupSnapshotItem);
            Assert.NotNull(snapshots);
            Assert.Single(snapshots);
            var snapshot = snapshots[0];
            Assert.False(snapshot.IsPinned);

            // 5. 锁定快照保护
            var pinReq = new TogglePinSnapshotRequest { Pinned = true, Note = "保护节点" };
            var pinResp = await client.PutAsJsonAsync($"/api/BackupSync/Tasks/{taskId}/Snapshots/{snapshot.SnapshotId}/Pin", pinReq, AppJsonSerializerContext.Default.TogglePinSnapshotRequest);
            Assert.Equal(HttpStatusCode.OK, pinResp.StatusCode);

            // 6. 验证快照状态已锁定
            var recheckResp = await client.GetAsync($"/api/BackupSync/Tasks/{taskId}/Snapshots");
            var updatedSnapshots = await recheckResp.Content.ReadFromJsonAsync<List<BackupSnapshotItem>>(AppJsonSerializerContext.Default.ListBackupSnapshotItem);
            Assert.NotNull(updatedSnapshots);
            Assert.True(updatedSnapshots[0].IsPinned);

            // 7. 预演批量删除被保护跳过
            var batchPreviewReq = new BatchDeleteSnapshotsRequest
            {
                TaskId = taskId,
                SnapshotIds = [snapshot.SnapshotId],
                SkipPinned = true
            };
            var previewResp = await client.PostAsJsonAsync("/api/BackupSync/Snapshots/BatchDeletePreview", batchPreviewReq, AppJsonSerializerContext.Default.BatchDeleteSnapshotsRequest);
            Assert.Equal(HttpStatusCode.OK, previewResp.StatusCode);
            var previewResult = await previewResp.Content.ReadFromJsonAsync<BatchDeleteSnapshotsResult>(AppJsonSerializerContext.Default.BatchDeleteSnapshotsResult);
            Assert.NotNull(previewResult);
            Assert.Equal(1, previewResult.SkippedPinnedCount);
            Assert.Equal(0, previewResult.DeletedCount);
        }
        finally
        {
            // 清理任务
            await client.DeleteAsync($"/api/BackupSync/Tasks/{taskId}");
        }
    }
}
