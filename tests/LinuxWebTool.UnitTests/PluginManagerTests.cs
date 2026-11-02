using System.IO.Compression;
using System.Security;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class PluginManagerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly PluginManager _manager;

    public PluginManagerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "lwt_plugin_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _manager = new PluginManager(NullLogger<PluginManager>.Instance, _tempRoot);
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
    public async Task GetAllAsync_ShouldAutoCreate_TenDefaultPluginSeeds()
    {
        var list = await _manager.GetAllAsync();
        Assert.NotNull(list);
        Assert.True(list.Count >= 10);

        var ids = list.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // OneShot
        Assert.Contains("sample-healthcheck", ids);
        Assert.Contains("ssh-access-guard", ids);
        Assert.Contains("caddy-manager", ids);
        Assert.Contains("native-command-demo", ids);
        // Resident
        Assert.Contains("demo-js-webhook", ids);
        Assert.Contains("demo-py-api", ids);
        Assert.Contains("demo-dotnet-service", ids);
        Assert.Contains("demo-sh-daemon", ids);
        Assert.Contains("demo-ps-daemon", ids);
        Assert.Contains("chatgpt-channel", ids);
        Assert.Contains("mcp-bridge", ids);
    }

    [Fact]
    public void PluginPortManager_ShouldAllocateWithinRange_AndReleaseCleanly()
    {
        var pm = new PluginPortManager();
        var port1 = pm.AllocatePort("plugin-a");
        var port2 = pm.AllocatePort("plugin-b");

        Assert.InRange(port1, 18000, 18999);
        Assert.InRange(port2, 18000, 18999);
        Assert.NotEqual(port1, port2);

        pm.ReleasePort("plugin-a");
        Assert.Null(pm.GetPort("plugin-a"));
    }

    [Fact]
    public void PluginPortManager_ShouldAutoAvoid_WhenPreferredPortIsOccupied()
    {
        var pm = new PluginPortManager();
        // 1. 分配并霸占 18015 端口
        var alloc1 = pm.AllocatePortWithDetails("occupant", 18015);
        Assert.Equal(18015, alloc1.Port);
        Assert.False(alloc1.WasAvoided);

        // 2. 另一个插件同样请求 18015，默认 avoid 策略
        var alloc2 = pm.AllocatePortWithDetails("new-plugin", 18015, "avoid");
        Assert.True(alloc2.WasAvoided);
        Assert.NotEqual(18015, alloc2.Port);
        Assert.Equal(18015, alloc2.RequestedPort);
        Assert.InRange(alloc2.Port, 18000, 18999);
        Assert.NotNull(alloc2.ConflictReason);
    }

    [Fact]
    public void PluginPortManager_ShouldThrow_WhenPortIsOccupiedAndPolicyIsStrict()
    {
        var pm = new PluginPortManager();
        pm.AllocatePortWithDetails("occupant", 18016);

        // strict 模式应当抛出 InvalidOperationException
        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            pm.AllocatePortWithDetails("strict-plugin", 18016, "strict");
        });

        Assert.Contains("严格冲突策略", ex.Message);
    }

    [Fact]
    public void PluginPortManager_CheckPortAndRecommend_ShouldWorkReliably()
    {
        var pm = new PluginPortManager();
        var (avail1, _) = pm.CheckPort(18020);
        Assert.True(avail1);

        pm.AllocatePortWithDetails("plugin-x", 18020);
        var (avail2, occupiedBy) = pm.CheckPort(18020);
        Assert.False(avail2);
        Assert.Contains("plugin-x", occupiedBy);

        var rec = pm.GetRecommendedPort(18020);
        Assert.NotEqual(18020, rec);
        Assert.InRange(rec, 18000, 18999);
    }

    [Fact]
    public void PluginStateMachine_ShouldTriggerCrashLoopCircuitBreaker_AfterFiveFastCrashes()
    {
        var sm = new PluginStateMachine("test-plugin", new PluginSupervisionConfig
        {
            MaxRestarts = 5
        });

        // 模拟第 1 到 4 次崩溃
        for (var i = 1; i <= 4; i++)
        {
            sm.NotifyStarting();
            var (shouldRestart, delay, nextState) = sm.RecordExit(1, "Simulated crash");
            Assert.True(shouldRestart);
            Assert.True(delay.TotalSeconds > 0);
            Assert.Equal(PluginState.Backoff, nextState);
            Assert.Equal(i, sm.CrashCount);
        }

        // 模拟第 5 次崩溃：应触发熔断
        sm.NotifyStarting();
        var (finalRestart, _, finalState) = sm.RecordExit(1, "Crash 5");
        Assert.False(finalRestart);
        Assert.Equal(PluginState.Degraded, finalState);
        Assert.Equal(5, sm.CrashCount);
        Assert.Contains("CrashLoop", sm.LastError);

        // 重置状态
        sm.Reset();
        Assert.Equal(PluginState.Stopped, sm.State);
        Assert.Equal(0, sm.CrashCount);
    }

    [Fact]
    public void RuntimeAdapters_ShouldBuildCorrectEnvironmentVariables()
    {
        var manifest = new PluginManifest
        {
            Id = "unit-test-plugin",
            Name = "Unit Test Plugin",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Python,
            Entrypoint = "plugin.py",
            Environment = new Dictionary<string, string> { ["CUSTOM_VAR"] = "Hello" }
        };

        var adapter = new PythonRuntimeAdapter();
        var spec = adapter.BuildStartSpec(manifest, "C:\\mock\\path", 18055);

        Assert.Equal("18055", spec.EnvironmentVariables["PORT"]);
        Assert.Equal("unit-test-plugin", spec.EnvironmentVariables["LWT_PLUGIN_ID"]);
        Assert.Equal("C:\\mock\\path", spec.EnvironmentVariables["LWT_CODE_DIR"]);
        Assert.Equal("C:\\mock\\path", spec.EnvironmentVariables["LWT_DATA_DIR"]);
        Assert.True(spec.EnvironmentVariables.ContainsKey("LWT_VOLUME_DIR"));
        Assert.True(spec.EnvironmentVariables.ContainsKey("LWT_STATE_DIR"));
        Assert.Equal("1", spec.EnvironmentVariables["PYTHONUNBUFFERED"]);
        Assert.Equal("Hello", spec.EnvironmentVariables["CUSTOM_VAR"]);
    }

    [Fact]
    public void RuntimeAdapters_ShouldMapPluginsDataVolumes_WhenUnderPluginsDirectory()
    {
        var manifest = new PluginManifest
        {
            Id = "demo-plugin",
            Name = "Demo Plugin",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Python,
            Entrypoint = "plugin.py"
        };

        var adapter = new PythonRuntimeAdapter();
        var pluginDir = OperatingSystem.IsWindows() ? @"C:\app\data\plugins\demo-plugin" : "/app/data/plugins/demo-plugin";
        var spec = adapter.BuildStartSpec(manifest, pluginDir, 18055);

        var expectedVolume = OperatingSystem.IsWindows()
            ? @"C:\app\data\plugins_data\volumes\demo-plugin"
            : "/app/data/plugins_data/volumes/demo-plugin";
        var expectedRun = OperatingSystem.IsWindows()
            ? @"C:\app\data\plugins_data\run\demo-plugin"
            : "/app/data/plugins_data/run/demo-plugin";
        var expectedState = OperatingSystem.IsWindows()
            ? @"C:\app\data\plugins_data\volumes\demo-plugin\state"
            : "/app/data/plugins_data/volumes/demo-plugin/state";

        Assert.Equal(expectedVolume, spec.EnvironmentVariables["LWT_VOLUME_DIR"]);
        Assert.Equal(expectedRun, spec.EnvironmentVariables["LWT_RUN_DIR"]);
        Assert.Equal(expectedState, spec.EnvironmentVariables["LWT_STATE_DIR"]);
    }

    [Fact]
    public async Task ImportZipAsync_ShouldThrowSecurityException_OnZipSlipPathTraversal()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
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
        var pluginId = "demo-js-webhook";
        await _manager.GetAllAsync(); // 确保目录存在

        var item = new PluginKvItem
        {
            Key = "test_key",
            Value = "test_value_123"
        };

        var setOk = await _manager.SetKvAsync(pluginId, item);
        Assert.True(setOk);

        var list = await _manager.GetKvListAsync(pluginId);
        var found = list.FirstOrDefault(x => x.Key == "test_key");
        Assert.NotNull(found);
        Assert.Equal("test_value_123", found.Value);

        var deleteOk = await _manager.DeleteKvAsync(pluginId, "test_key");
        Assert.True(deleteOk);

        var listAfter = await _manager.GetKvListAsync(pluginId);
        Assert.DoesNotContain(listAfter, x => x.Key == "test_key");
    }

    [Fact]
    public async Task SetEnabledAsync_ShouldToggleDisabledMarker_AndReflectInPluginDto()
    {
        var pluginId = "demo-js-webhook";
        await _manager.GetAllAsync();

        var initial = await _manager.GetByIdAsync(pluginId);
        Assert.NotNull(initial);
        Assert.True(initial.Manifest.Enabled);

        // 设置禁用
        var setDisabled = await _manager.SetEnabledAsync(pluginId, false);
        Assert.True(setDisabled);

        var disabled = await _manager.GetByIdAsync(pluginId);
        Assert.NotNull(disabled);
        Assert.False(disabled.Manifest.Enabled);

        // 恢复启用
        var setEnabled = await _manager.SetEnabledAsync(pluginId, true);
        Assert.True(setEnabled);

        var enabled = await _manager.GetByIdAsync(pluginId);
        Assert.NotNull(enabled);
        Assert.True(enabled.Manifest.Enabled);
    }

    [Fact]
    public async Task GetSourceMetadataAsync_ShouldReturnFallbackLocal_WhenNoSourceJson()
    {
        var pluginId = "demo-js-webhook";
        await _manager.GetAllAsync();

        var meta = await _manager.GetSourceMetadataAsync(pluginId);
        Assert.NotNull(meta);
        Assert.Equal("builtin", meta.SourceType);
        Assert.Equal("1.0.0", meta.InstalledVersion);

        // 创建自定义 plugin
        await _manager.CreateOrUpdateAsync(new PluginCreateOrUpdateRequest
        {
            Manifest = new PluginManifest
            {
                Id = "custom-test-plugin",
                Name = "Custom Test Plugin",
                Profile = PluginProfile.Resident,
                Runtime = PluginRuntime.Python,
                Entrypoint = "main.py"
            }
        });

        var customMeta = await _manager.GetSourceMetadataAsync("custom-test-plugin");
        Assert.NotNull(customMeta);
        Assert.Equal("custom", customMeta.SourceType);
    }

    [Fact]
    public async Task ManualStop_ShouldPersistMarker_AndCleanOnStart()
    {
        var pluginId = "demo-js-webhook";
        await _manager.GetAllAsync();

        var pluginDir = Path.Combine(_tempRoot, "data", "plugins", pluginId);
        var manualStopMarker = Path.Combine(pluginDir, ".manual_stop");

        // 1. 手动停止 -> 必须写入 .manual_stop 标记
        var stopResult = await _manager.StopAsync(pluginId, isManual: true);
        Assert.True(stopResult);
        Assert.True(File.Exists(manualStopMarker), ".manual_stop 标记文件必须存在");

        // 2. 重新启动 -> 必须清除 .manual_stop 标记
        await _manager.StartWithResultAsync(pluginId);
        Assert.False(File.Exists(manualStopMarker), ".manual_stop 标记文件在启动后必须被自动清除");
    }

    [Fact]
    public async Task DynamicFileManagement_ShouldSupport_UploadDownloadDelete_AndProtectPluginJson()
    {
        var pluginId = "demo-js-webhook";
        await _manager.GetAllAsync();

        var testFileName = "test_script.sh";
        var fileContent = "#!/bin/bash\necho 'hello dynamic upload'\n";
        using var uploadStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(fileContent));

        // 1. 上传新文件
        var uploadSuccess = await _manager.UploadFileAsync(pluginId, testFileName, uploadStream);
        Assert.True(uploadSuccess);

        // 2. 下载该文件并验证内容
        var downloadResult = await _manager.DownloadFileAsync(pluginId, testFileName);
        await using (downloadResult.Stream)
        {
            using var reader = new StreamReader(downloadResult.Stream);
            var downloadedText = await reader.ReadToEndAsync();
            Assert.Equal(fileContent, downloadedText);
            Assert.Equal(testFileName, downloadResult.FileName);
        }

        // 3. 删除该文件
        var deleteSuccess = await _manager.DeleteFileAsync(pluginId, testFileName);
        Assert.True(deleteSuccess);

        // 4. 保护测试：plugin.json 禁止删除
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _manager.DeleteFileAsync(pluginId, "plugin.json");
        });
    }
}
