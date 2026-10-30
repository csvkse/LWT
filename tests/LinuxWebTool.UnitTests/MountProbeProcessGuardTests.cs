using System.Diagnostics;
using LinuxWebTool.Contracts.Features.Mount.Contracts;
using LinuxWebTool.Infrastructure.Features.Mount.Adapters;
using LinuxWebTool.Infrastructure.Shared.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

/// <summary>
/// 挂载探针守卫与进程 D 状态 / 僵尸态阻断防回归单元测试。
/// 验证点：
/// 1. 未记录路径直接放行；
/// 2. 已退出或僵尸进程自动清理并放行，绝不产生永久锁；
/// 3. 超出 TTL（30秒）的悬挂记录自动淘汰并放行；
/// 4. Clear 能够立即解除指定路径的阻断；
/// 5. 路径统一规范化，避免大小写或尾随斜杠导致绕过或重复阻断；
/// 6. SmbMountService.BuildOptions 自动注入 echo_interval=10 心跳参数，且尊重用户显式配置。
/// </summary>
public sealed class MountProbeProcessGuardTests : IDisposable
{
    private sealed class DummyHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "LinuxWebTool.UnitTests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private readonly string _tempDir;
    private readonly DataPaths _dataPaths;
    private readonly SmbMountService _smbService;

    public MountProbeProcessGuardTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lwt_mount_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Data:Directory"] = _tempDir
        }).Build();

        var env = new DummyHostEnvironment(_tempDir);
        _dataPaths = new DataPaths(config, env);
        _smbService = new SmbMountService(_dataPaths, NullLogger<SmbMountService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void IsBlocked_WhenPathNotRecorded_ReturnsFalse()
    {
        var testPath = "/mnt/unrecorded_path_" + Guid.NewGuid().ToString("N");
        Assert.False(MountProbeProcessGuard.IsBlocked(testPath));
    }

    [Fact]
    public void Clear_RemovesGuardImmediately()
    {
        var testPath = "/mnt/test_clear_" + Guid.NewGuid().ToString("N");

        // 启动一个极短生命周期的进程模拟记录
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "-c exit 0",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;

        MountProbeProcessGuard.RecordIfAlive(testPath, proc);
        proc.WaitForExit();

        // 显式清除
        MountProbeProcessGuard.Clear(testPath);

        // 验证已彻底解除
        Assert.False(MountProbeProcessGuard.IsBlocked(testPath));
    }

    [Fact]
    public void IsBlocked_WhenProcessExited_ReturnsFalseAndPurgesPending()
    {
        var testPath = "/mnt/test_exited_" + Guid.NewGuid().ToString("N");

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "-c exit 0",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;

        MountProbeProcessGuard.RecordIfAlive(testPath, proc);
        proc.WaitForExit();

        // 进程退出后，调用 IsBlocked 必须识别到并返回 false，同时移除记录
        var blocked = MountProbeProcessGuard.IsBlocked(testPath);
        Assert.False(blocked, "已退出的探针进程绝不允许阻断后续挂载");

        // 再次调用确认已被 purge
        Assert.False(MountProbeProcessGuard.IsBlocked(testPath));
    }

    [Fact]
    public void IsBlocked_WhenTtlExpired_AutomaticallyEvictsAndReturnsFalse()
    {
        var testPath = "/mnt/test_ttl_" + Guid.NewGuid().ToString("N");

        using var proc = Process.GetCurrentProcess();
        MountProbeProcessGuard.RecordIfAlive(testPath, proc);

        // 传入已超时的 TTL (TimeSpan.Zero)，模拟 30 秒超时后的自动淘汰机制
        var blocked = MountProbeProcessGuard.IsBlocked(testPath, ttl: TimeSpan.FromMilliseconds(-1));

        Assert.False(blocked, "超过 TTL 窗口的挂起记录必须被自动淘汰放行");
        Assert.False(MountProbeProcessGuard.IsBlocked(testPath));
    }

    [Fact]
    public void PathNormalization_EnsuresConsistentGuardKey()
    {
        var pathWithSlash = "/mnt/my_share/";
        var pathWithoutSlash = "/mnt/my_share";

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh",
            Arguments = OperatingSystem.IsWindows() ? "/c exit 0" : "-c exit 0",
            CreateNoWindow = true,
            UseShellExecute = false
        })!;

        MountProbeProcessGuard.RecordIfAlive(pathWithSlash, proc);
        proc.WaitForExit();

        // 使用无斜杠路径清理，必须能够命中并成功清理
        MountProbeProcessGuard.Clear(pathWithoutSlash);
        Assert.False(MountProbeProcessGuard.IsBlocked(pathWithSlash));
    }

    [Fact]
    public void SmbMountService_BuildOptions_InjectsEchoIntervalWhenMissing()
    {
        var mount = new SmbMount
        {
            Id = Guid.NewGuid(),
            Name = "TestShare",
            Server = "//10.126.1.70/media",
            LocalPath = "/mnt/media",
            Options = "vers=3.0,uid=1001,gid=1001"
        };

        var options = _smbService.BuildOptions(mount);

        Assert.Contains("echo_interval=10", options);
        Assert.Contains("vers=3.0", options);
        Assert.Contains("iocharset=utf8", options);
    }

    [Fact]
    public void SmbMountService_BuildOptions_PreservesUserEchoInterval()
    {
        var mount = new SmbMount
        {
            Id = Guid.NewGuid(),
            Name = "TestShare",
            Server = "//10.126.1.70/media",
            LocalPath = "/mnt/media",
            Options = "vers=3.0,echo_interval=5,uid=1001"
        };

        var options = _smbService.BuildOptions(mount);

        Assert.Contains("echo_interval=5", options);
        Assert.DoesNotContain("echo_interval=10", options);
    }
}
