using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using LinuxWebTool.Infrastructure.Features.Services.Adapters.Config;
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
    public void LinuxDistroKeyResolver_MapsOsReleaseAndRejectsUnknownValues()
    {
        Assert.Equal("debian", LinuxDistroKeyResolver.Resolve("ID=debian\nVERSION_ID=\"12\""));
        Assert.Equal("debian", LinuxDistroKeyResolver.Resolve("ID=ubuntu\nVERSION_ID=\"24.04\""));
        Assert.Equal("alpine", LinuxDistroKeyResolver.Resolve("ID=alpine"));
        Assert.Null(LinuxDistroKeyResolver.Resolve("NAME=Unknown Linux"));
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
    public void ServicesHub_JsonSerialization_SupportsHostContractsForAot()
    {
        var env = new ServiceEnvironmentInfo
        {
            OperatingSystem = "Linux",
            Platform = "Unix",
            Is64Bit = true,
            Hostname = "test-node",
            KernelVersion = "6.6.0-generic",
            IsDockerContainer = true,
            IsPrivilegedContainer = true,
            HostDistroKey = "debian",
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
        Assert.Equal("debian", restored.HostDistroKey);

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

    [Fact]
    public async Task SystemServiceConfigAdapter_SambaUserValidation_ValidatesInputs()
    {
        var executor = new MockShellExecutor();
        var adapter = new SystemServiceConfigAdapter(executor, NullLogger<SystemServiceConfigAdapter>.Instance);

        var invalidUserReq = new CreateSambaUserRequest
        {
            Username = "bad user name with spaces!",
            Password = "secret-password"
        };
        var res = await adapter.CreateSambaUserAsync(invalidUserReq);
        Assert.False(res.Success);
        Assert.Contains("用户名格式不合法", res.Message);

        var emptyPassReq = new CreateSambaUserRequest
        {
            Username = "validuser",
            Password = ""
        };
        var emptyRes = await adapter.CreateSambaUserAsync(emptyPassReq);
        Assert.False(emptyRes.Success);
        Assert.Contains("密码不能为空", emptyRes.Message);
    }

    [Fact]
    public void SystemServiceConfigAdapter_ValidateSharePathSafety_BlocksCriticalSystemPaths()
    {
        // 相对路径或空路径应拦截
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("").IsSafe);
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("relative/path").IsSafe);

        // 根目录与关键系统目录应拦截
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("/").IsSafe);
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("//").IsSafe);
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("/etc").IsSafe);
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("/etc/samba").IsSafe);
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("/boot").IsSafe);
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("/sys").IsSafe);
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("/proc").IsSafe);
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("/dev").IsSafe);
        Assert.False(SystemServiceConfigAdapter.ValidateSharePathSafety("/root").IsSafe);

        // 合法存储路径应放行
        Assert.True(SystemServiceConfigAdapter.ValidateSharePathSafety("/var/docker/mediary-data").IsSafe);
        Assert.True(SystemServiceConfigAdapter.ValidateSharePathSafety("/mnt/nas/movies").IsSafe);
        Assert.True(SystemServiceConfigAdapter.ValidateSharePathSafety("/data/share").IsSafe);
        Assert.True(SystemServiceConfigAdapter.ValidateSharePathSafety("/srv/samba").IsSafe);
        Assert.True(SystemServiceConfigAdapter.ValidateSharePathSafety("/home/user/public").IsSafe);
    }

    [Fact]
    public async Task SystemServiceConfigAdapter_CreateSambaShare_ValidatesNameAndPath()
    {
        var executor = new MockShellExecutor();
        var adapter = new SystemServiceConfigAdapter(executor, NullLogger<SystemServiceConfigAdapter>.Instance);

        // 非法共享名称
        var invalidNameReq = new SaveSingleSambaShareRequest
        {
            Name = "bad share*name!",
            Path = "/var/docker/shares"
        };
        var res1 = await adapter.CreateSambaShareAsync(invalidNameReq);
        Assert.False(res1.Success);
        Assert.Contains("共享名称格式不合法", res1.Message);

        // 高危系统路径
        var dangerousPathReq = new SaveSingleSambaShareRequest
        {
            Name = "system_etc",
            Path = "/etc"
        };
        var res2 = await adapter.CreateSambaShareAsync(dangerousPathReq);
        Assert.False(res2.Success);
        Assert.Contains("高危系统目录", res2.Message);
    }
}
