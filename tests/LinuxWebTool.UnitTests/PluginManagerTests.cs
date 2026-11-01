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
}
