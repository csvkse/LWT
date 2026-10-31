using System.Diagnostics;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using LinuxWebTool.Infrastructure.Features.Services.Adapters.Systemd;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class SystemdUnitCategorizerTests
{
    private readonly SystemdUnitCategorizer _categorizer = new();

    [Theory]
    [InlineData("docker.service", "Docker Application Container Engine", "app", "system")]
    [InlineData("nginx.service", "A high performance web server and reverse proxy server", "app", "system")]
    [InlineData("mysqld.service", "MySQL Community Server", "app", "system")]
    [InlineData("redis.service", "Advanced key-value store", "app", "system")]
    [InlineData("postgresql.service", "PostgreSQL RDBMS", "app", "system")]
    [InlineData("caddy.service", "Caddy Web Server", "app", "system")]
    [InlineData("easytier.service", "EasyTier mesh VPN daemon", "app", "system")]
    [InlineData("my-custom-api.service", "Internal microservice daemon", "app", "system")]
    [InlineData("app-bot.service", "Telegram trading bot", "app", "system")]
    public void Categorize_AppServices_ShouldReturnAppCategory(string unit, string desc, string expectedCat, string expectedScope)
    {
        var (category, scope) = _categorizer.Categorize(unit, desc);
        Assert.Equal(expectedCat, category);
        Assert.Equal(expectedScope, scope);
    }

    [Theory]
    [InlineData("sshd.service", "OpenSSH server daemon", "network", "system")]
    [InlineData("ufw.service", "CLI Netfilter Manager", "network", "system")]
    [InlineData("firewalld.service", "firewalld - dynamic firewall daemon", "network", "system")]
    [InlineData("NetworkManager.service", "Network Manager", "network", "system")]
    [InlineData("tailscaled.service", "Tailscale client daemon", "network", "system")]
    [InlineData("wireguard-quick@wg0.service", "WireGuard via wg-quick(8) for wg0", "network", "system")]
    public void Categorize_NetworkServices_ShouldReturnNetworkCategory(string unit, string desc, string expectedCat, string expectedScope)
    {
        var (category, scope) = _categorizer.Categorize(unit, desc);
        Assert.Equal(expectedCat, category);
        Assert.Equal(expectedScope, scope);
    }

    [Theory]
    [InlineData("systemd-udevd.service", "Rule-based Manager for Device Events and Files", "storage", "system")]
    [InlineData("smartmontools.service", "Self Monitoring and Reporting Technology (SMART) Daemon", "storage", "system")]
    [InlineData("udisks2.service", "Disk Manager", "storage", "system")]
    [InlineData("fstrim.timer", "Discard unused filesystem blocks once a week", "storage", "system")]
    [InlineData("lvm2-monitor.service", "Monitoring of LVM2 mirrors, snapshots etc. using dmeventd or progress polling", "storage", "system")]
    public void Categorize_StorageServices_ShouldReturnStorageCategory(string unit, string desc, string expectedCat, string expectedScope)
    {
        var (category, scope) = _categorizer.Categorize(unit, desc);
        Assert.Equal(expectedCat, category);
        Assert.Equal(expectedScope, scope);
    }

    [Theory]
    [InlineData("systemd-journald.service", "Journal Service", "system", "system")]
    [InlineData("systemd-logind.service", "User Login Management", "system", "system")]
    [InlineData("dbus.service", "D-Bus System Message Bus", "system", "system")]
    [InlineData("cron.service", "Regular background program processing daemon", "system", "system")]
    [InlineData("polkit.service", "Authorization Manager", "system", "system")]
    [InlineData("getty@tty1.service", "Getty on tty1", "system", "system")]
    public void Categorize_SystemCoreServices_ShouldReturnSystemCategory(string unit, string desc, string expectedCat, string expectedScope)
    {
        var (category, scope) = _categorizer.Categorize(unit, desc);
        Assert.Equal(expectedCat, category);
        Assert.Equal(expectedScope, scope);
    }

    [Fact]
    public void Categorize_UserSessionScope_ShouldRecognizeUserScope()
    {
        var (cat, scope) = _categorizer.Categorize("pulseaudio@user.service", "PulseAudio user daemon");
        Assert.Equal("user", scope);
    }

    [Fact]
    public void Categorize_PerformanceBenchmark_1000Matches_ShouldCompleteUnder10Milliseconds()
    {
        var samples = new (string Unit, string Desc)[]
        {
            ("nginx.service", "Nginx web server"),
            ("systemd-journald.service", "Journal Service"),
            ("sshd.service", "OpenSSH Server"),
            ("smartd.service", "SMART Disk Monitor"),
            ("unknown-custom.service", "Miscellaneous tool"),
            ("redis-server.service", "Redis in-memory database"),
            ("cron.service", "Scheduled cron jobs"),
            ("ufw.service", "Uncomplicated Firewall")
        };

        // 预热 JIT
        for (var i = 0; i < 50; i++)
        {
            _categorizer.Categorize(samples[i % samples.Length].Unit, samples[i % samples.Length].Desc);
        }

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 1000; i++)
        {
            var item = samples[i % samples.Length];
            var (cat, scp) = _categorizer.Categorize(item.Unit, item.Desc);
            Assert.NotNull(cat);
            Assert.NotNull(scp);
        }
        sw.Stop();

        // 1000 次分类必须远小于 15ms（通常 < 2ms）
        Assert.True(sw.ElapsedMilliseconds < 50, $"1000 次分类耗时过高: {sw.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task SystemdServiceAdapter_CriteriaCategoryFilter_ShouldFilterCorrectly()
    {
        var mockShell = new MockShellExecutor
        {
            Handler = req =>
            {
                if (req.CommandText?.Contains("Get-Service", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var json = """
                    [
                        {"Name":"cron","Status":4,"DisplayName":"Regular background program processing daemon"},
                        {"Name":"nginx","Status":4,"DisplayName":"A high performance web server"},
                        {"Name":"sshd","Status":4,"DisplayName":"OpenSSH server daemon"},
                        {"Name":"systemd-udevd","Status":4,"DisplayName":"Rule-based Manager for Device Events and Files"}
                    ]
                    """;
                    return new ShellResult { ExitCode = 0, StandardOutput = json };
                }

                var stdout = """
                cron.service loaded active running Regular background program processing daemon
                nginx.service loaded active running A high performance web server
                sshd.service loaded active running OpenSSH server daemon
                systemd-udevd.service loaded active running Rule-based Manager for Device Events and Files
                """;
                return new ShellResult
                {
                    ExitCode = 0,
                    StandardOutput = stdout,
                    ErrorOutput = string.Empty
                };
            }
        };

        var mockEnv = new MockEnvDetector(new ServiceEnvironmentInfo
        {
            OperatingSystem = "Linux 6.8.0",
            Platform = "Linux",
            Is64Bit = true,
            Hostname = "test-linux",
            KernelVersion = "6.8.0-generic",
            IsDockerContainer = false,
            HasSystemd = true,
            SystemdPierced = false
        });

        var adapter = new SystemdServiceAdapter(
            mockShell,
            mockEnv,
            _categorizer,
            NullLogger<SystemdServiceAdapter>.Instance);

        // 1. 过滤 app 类别
        var appUnits = await adapter.ListUnitsAsync(new SystemdQueryCriteria { Category = "app" });
        Assert.Single(appUnits);
        Assert.Equal("nginx.service", appUnits[0].Unit);
        Assert.Equal("app", appUnits[0].Category);

        // 2. 过滤 network 类别
        var netUnits = await adapter.ListUnitsAsync(new SystemdQueryCriteria { Category = "network" });
        Assert.Single(netUnits);
        Assert.Equal("sshd.service", netUnits[0].Unit);
        Assert.Equal("network", netUnits[0].Category);

        // 3. 过滤 storage 类别
        var storageUnits = await adapter.ListUnitsAsync(new SystemdQueryCriteria { Category = "storage" });
        Assert.Single(storageUnits);
        Assert.Equal("systemd-udevd.service", storageUnits[0].Unit);
        Assert.Equal("storage", storageUnits[0].Category);

        // 4. 全部查询
        var allUnits = await adapter.ListUnitsAsync(new SystemdQueryCriteria { Category = "all" });
        Assert.Equal(4, allUnits.Count);
    }

    private sealed class MockShellExecutor : IShellExecutor
    {
        public Func<ShellRequest, ShellResult>? Handler { get; set; }

        public Task<ShellResult> ExecuteAsync(ShellRequest request, CancellationToken cancellationToken = default)
        {
            if (Handler != null) return Task.FromResult(Handler(request));
            return Task.FromResult(new ShellResult { ExitCode = 0, StandardOutput = "" });
        }
    }

    private sealed class MockEnvDetector : IServiceEnvironmentDetector
    {
        private readonly ServiceEnvironmentInfo _info;
        public MockEnvDetector(ServiceEnvironmentInfo info) => _info = info;
        public Task<ServiceEnvironmentInfo> DetectAsync(CancellationToken cancellationToken = default) => Task.FromResult(_info);
    }
}
