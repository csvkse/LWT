using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using LinuxWebTool.Infrastructure.Features.Services.Adapters.Config;
using LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;
using LinuxWebTool.Infrastructure.Features.Services.Detectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class ServicesHubTests
{
    private sealed class MockShellExecutor : IShellExecutor
    {
        public Func<ShellRequest, ShellResult>? Handler { get; set; }

        public Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default)
        {
            if (Handler != null)
            {
                return Task.FromResult(Handler(request));
            }

            return Task.FromResult(new ShellResult
            {
                ExitCode = 0,
                StandardOutput = string.Empty,
                ErrorOutput = string.Empty,
                DurationMs = 5,
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow
            });
        }
    }

    [Fact]
    public async Task ServiceEnvironmentDetector_DetectsCurrentPlatform_ReturnsValidProfile()
    {
        var executor = new MockShellExecutor();
        var detector = new ServiceEnvironmentDetector(executor, NullLogger<ServiceEnvironmentDetector>.Instance);

        var env = await detector.DetectAsync();

        Assert.NotNull(env);
        Assert.False(string.IsNullOrWhiteSpace(env.OperatingSystem));
        Assert.False(string.IsNullOrWhiteSpace(env.Platform));
        Assert.False(string.IsNullOrWhiteSpace(env.Hostname));

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("Windows", env.OperatingSystem, StringComparison.OrdinalIgnoreCase);
            Assert.False(env.IsDockerContainer);
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Contains("Linux", env.OperatingSystem, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ServiceEnvironmentDetector_CachesResult_WithinTtl()
    {
        var executor = new MockShellExecutor();
        var detector = new ServiceEnvironmentDetector(executor, NullLogger<ServiceEnvironmentDetector>.Instance);

        var first = await detector.DetectAsync();
        var second = await detector.DetectAsync();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task SystemServiceConfigAdapter_GetSshConfig_ReturnsSafeDefaults()
    {
        var executor = new MockShellExecutor();
        var adapter = new SystemServiceConfigAdapter(executor, NullLogger<SystemServiceConfigAdapter>.Instance);

        var config = await adapter.GetSshConfigAsync();

        Assert.NotNull(config);
        Assert.True(config.Port > 0);
        Assert.NotNull(config.AuthorizedKeys);
    }

    [Fact]
    public async Task SystemServiceConfigAdapter_UpdateSshConfig_ValidatesPortBoundaries()
    {
        var executor = new MockShellExecutor();
        var adapter = new SystemServiceConfigAdapter(executor, NullLogger<SystemServiceConfigAdapter>.Instance);

        var invalidPortReq = new UpdateSshConfigRequest
        {
            Port = 70000,
            PermitRootLogin = "yes",
            PasswordAuthentication = true,
            PubkeyAuthentication = true
        };

        var result = await adapter.UpdateSshConfigAsync(invalidPortReq);
        Assert.False(result.Success);
        Assert.Contains("端口号", result.Message);
    }

    [Fact]
    public async Task ScriptExtensionManager_DiscoversAndValidatesExtensionLifecycle()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_test_ext_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDir);
            var executor = new MockShellExecutor();
            var manager = new ScriptExtensionManager(executor, NullLogger<ScriptExtensionManager>.Instance, tempDir);

            // 1. 初始化时应当创建默认示例扩展
            var exts = await manager.ListExtensionsAsync();
            Assert.NotEmpty(exts);
            var demo = exts.FirstOrDefault(e => e.Manifest.Id == "sample-healthcheck");
            Assert.NotNull(demo);
            Assert.Equal("系统健康守护扩展 (示例)", demo.Manifest.Name);

            // 2. 状态查询应当正常处理
            var statRes = await manager.ExecuteExtensionActionAsync(new ExtensionActionRequest
            {
                ExtensionId = "sample-healthcheck",
                Action = "status"
            });
            Assert.NotNull(statRes);

            // 3. 不存在的扩展应该安全报错
            var notFound = await manager.ExecuteExtensionActionAsync(new ExtensionActionRequest
            {
                ExtensionId = "non-existent-plugin",
                Action = "start"
            });
            Assert.False(notFound.Success);
            Assert.Contains("未找到扩展包", notFound.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    [Fact]
    public void ServicesHub_JsonSerialization_SupportsAllContractsForAot()
    {
        // 验证 Services Hub 相关的全部 DTO 能够正常序列化与反序列化
        var env = new ServiceEnvironmentInfo
        {
            OperatingSystem = "Linux",
            Platform = "Unix",
            Is64Bit = true,
            Hostname = "test-node",
            KernelVersion = "6.6.0-generic",
            IsDockerContainer = true,
            IsPrivilegedContainer = true,
            HasSystemd = true,
            SystemdPierced = true,
            HasDockerSocket = true,
            InstalledRunners = ["bash", "python3"]
        };

        var json = JsonSerializer.Serialize(env);
        Assert.NotNull(json);
        var restored = JsonSerializer.Deserialize<ServiceEnvironmentInfo>(json);
        Assert.NotNull(restored);
        Assert.Equal("Linux", restored.OperatingSystem);
        Assert.True(restored.IsPrivilegedContainer);

        var unit = new SystemdUnitItem
        {
            Unit = "nginx.service",
            LoadState = "loaded",
            ActiveState = "active",
            SubState = "running",
            Description = "A high performance web server and a reverse proxy server",
            UnitFileState = "enabled"
        };
        var unitJson = JsonSerializer.Serialize(unit);
        var restoredUnit = JsonSerializer.Deserialize<SystemdUnitItem>(unitJson);
        Assert.NotNull(restoredUnit);
        Assert.Equal("nginx.service", restoredUnit.Unit);

        var actionResult = new ServiceActionResult
        {
            Success = true,
            Message = "Service restarted successfully",
            Output = "job 123 done"
        };
        var resJson = JsonSerializer.Serialize(actionResult);
        var restoredRes = JsonSerializer.Deserialize<ServiceActionResult>(resJson);
        Assert.NotNull(restoredRes);
        Assert.True(restoredRes.Success);
    }
}
