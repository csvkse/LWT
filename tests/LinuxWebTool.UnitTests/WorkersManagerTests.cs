using System.IO.Compression;
using System.Security;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Infrastructure.Features.Workers;
using LinuxWebTool.Infrastructure.Features.Workers.Adapters;
using LinuxWebTool.Infrastructure.Features.Workers.Supervisor;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class WorkersManagerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly WorkersManager _manager;

    public WorkersManagerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "lwt_workers_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _manager = new WorkersManager(NullLogger<WorkersManager>.Instance, _tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task GetAllAsync_ShouldAutoCreate_FiveDefaultWorkerSeeds()
    {
        var list = await _manager.GetAllAsync();
        Assert.NotNull(list);
        Assert.True(list.Count >= 5);

        var ids = list.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("demo-js-webhook", ids);
        Assert.Contains("demo-py-api", ids);
        Assert.Contains("demo-dotnet-service", ids);
        Assert.Contains("demo-sh-daemon", ids);
        Assert.Contains("demo-ps-daemon", ids);
    }

    [Fact]
    public void WorkerPortManager_ShouldAllocateWithinRange_AndReleaseCleanly()
    {
        var pm = new WorkerPortManager();
        var port1 = pm.AllocatePort("worker-a");
        var port2 = pm.AllocatePort("worker-b");

        Assert.InRange(port1, 18000, 18999);
        Assert.InRange(port2, 18000, 18999);
        Assert.NotEqual(port1, port2);

        pm.ReleasePort("worker-a");
        Assert.Null(pm.GetPort("worker-a"));
    }

    [Fact]
    public void WorkerStateMachine_ShouldTriggerCrashLoopCircuitBreaker_AfterFiveFastCrashes()
    {
        var sm = new WorkerStateMachine("test-worker", new WorkerSupervisionConfig
        {
            MaxCrashRetries = 5,
            FlappingThresholdSec = 10
        });

        // 模拟第 1 到 4 次瞬时崩溃（在 10s 内退出）
        for (var i = 1; i <= 4; i++)
        {
            sm.NotifyStarting();
            var (shouldRestart, delay, nextState) = sm.RecordExit(1, "Simulated crash");
            Assert.True(shouldRestart);
            Assert.True(delay.TotalSeconds > 0);
            Assert.Equal(WorkerState.Backoff, nextState);
            Assert.Equal(i, sm.FastCrashCount);
        }

        // 模拟第 5 次崩溃：应触发熔断
        sm.NotifyStarting();
        var (finalRestart, _, finalState) = sm.RecordExit(1, "Crash 5");
        Assert.False(finalRestart);
        Assert.Equal(WorkerState.Degraded, finalState);
        Assert.Equal(5, sm.FastCrashCount);
        Assert.Contains("CrashLoop", sm.LastError);

        // 重置状态
        sm.Reset();
        Assert.Equal(WorkerState.Stopped, sm.State);
        Assert.Equal(0, sm.FastCrashCount);
    }

    [Fact]
    public void RuntimeAdapters_ShouldBuildCorrectEnvironmentVariables()
    {
        var manifest = new WorkerManifest
        {
            Id = "unit-test-worker",
            Name = "Unit Test Worker",
            Runtime = WorkerRuntimeType.Python,
            Entrypoint = "worker.py",
            EnvironmentVariables = new Dictionary<string, string> { ["CUSTOM_VAR"] = "Hello" }
        };

        var adapter = new PythonRuntimeAdapter();
        var spec = adapter.BuildStartSpec(manifest, "C:\\mock\\path", 18055);

        Assert.Equal("18055", spec.EnvironmentVariables["PORT"]);
        Assert.Equal("unit-test-worker", spec.EnvironmentVariables["LWT_WORKER_ID"]);
        Assert.Equal("C:\\mock\\path", spec.EnvironmentVariables["LWT_DATA_DIR"]);
        Assert.Equal("1", spec.EnvironmentVariables["PYTHONUNBUFFERED"]);
        Assert.Equal("Hello", spec.EnvironmentVariables["CUSTOM_VAR"]);
    }

    [Fact]
    public async Task ImportZipAsync_ShouldThrowSecurityException_OnZipSlipPathTraversal()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            // 构造含有 ../../ 穿越路径的恶意 Entry
            var maliciousEntry = zip.CreateEntry("../../../etc/evil.sh");
            using var entryStream = maliciousEntry.Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write("echo malicious");
        }
        ms.Position = 0;

        await Assert.ThrowsAsync<SecurityException>(async () =>
        {
            await _manager.ImportZipAsync(ms);
        });
    }

    [Fact]
    public async Task KvStorage_ShouldSupport_SetGetAndDelete()
    {
        var workerId = "demo-js-webhook";
        await _manager.GetAllAsync(); // 确保目录存在

        var item = new WorkerKvItem
        {
            Key = "test_key",
            Value = "test_value_123"
        };

        var setOk = await _manager.SetKvAsync(workerId, item);
        Assert.True(setOk);

        var list = await _manager.GetKvListAsync(workerId);
        var found = list.FirstOrDefault(x => x.Key == "test_key");
        Assert.NotNull(found);
        Assert.Equal("test_value_123", found.Value);

        var deleteOk = await _manager.DeleteKvAsync(workerId, "test_key");
        Assert.True(deleteOk);

        var listAfter = await _manager.GetKvListAsync(workerId);
        Assert.DoesNotContain(listAfter, x => x.Key == "test_key");
    }

    [Fact]
    public async Task SetEnabledAsync_ShouldToggleDisabledMarker_AndReflectInWorkerDto()
    {
        var workerId = "demo-js-webhook";
        await _manager.GetAllAsync(); // 确保目录存在

        // 默认应当为启用
        var initial = await _manager.GetByIdAsync(workerId);
        Assert.NotNull(initial);
        Assert.True(initial.IsEnabled);

        // 设置禁用
        var setDisabled = await _manager.SetEnabledAsync(workerId, false);
        Assert.True(setDisabled);

        var disabledWorker = await _manager.GetByIdAsync(workerId);
        Assert.NotNull(disabledWorker);
        Assert.False(disabledWorker.IsEnabled);

        // 禁用状态下无法启动
        var startResult = await _manager.StartAsync(workerId);
        Assert.False(startResult);

        // 恢复启用
        var setEnabled = await _manager.SetEnabledAsync(workerId, true);
        Assert.True(setEnabled);

        var enabledWorker = await _manager.GetByIdAsync(workerId);
        Assert.NotNull(enabledWorker);
        Assert.True(enabledWorker.IsEnabled);
    }

    [Fact]
    public async Task GetSourceMetadataAsync_ShouldReturnFallbackLocal_WhenNoSourceJson()
    {
        var workerId = "demo-js-webhook";
        await _manager.GetAllAsync();

        var meta = await _manager.GetSourceMetadataAsync(workerId);
        Assert.NotNull(meta);
        Assert.Equal("builtin", meta.SourceType);
        Assert.Equal("1.0.0", meta.InstalledVersion);

        // 创建自定义 worker
        await _manager.CreateOrUpdateAsync(new WorkerCreateOrUpdateRequest
        {
            Id = "custom-test-worker",
            Name = "Custom Test Worker",
            Runtime = WorkerRuntimeType.Python,
            Entrypoint = "worker.py",
            ScriptContent = "print('hello')"
        });

        var customMeta = await _manager.GetSourceMetadataAsync("custom-test-worker");
        Assert.NotNull(customMeta);
        Assert.Equal("custom", customMeta.SourceType);
    }
}
