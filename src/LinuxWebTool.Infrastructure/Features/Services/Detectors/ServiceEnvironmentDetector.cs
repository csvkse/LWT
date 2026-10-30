using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Detectors;

/// <summary>
/// 宿主与运行环境探测器实现：
/// 精准检测物理裸机 / 虚机 / Docker 特权 / Docker 普通容器，以及 Docker Socket、Systemd、SSH、Samba、网络与脚本运行时能力。
/// </summary>
public sealed class ServiceEnvironmentDetector : IServiceEnvironmentDetector
{
    private readonly IShellExecutor _shellExecutor;
    private readonly ILogger<ServiceEnvironmentDetector> _logger;

    private ServiceEnvironmentInfo? _cachedInfo;
    private DateTime _lastDetectTime = DateTime.MinValue;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ServiceEnvironmentDetector(IShellExecutor shellExecutor, ILogger<ServiceEnvironmentDetector> logger)
    {
        _shellExecutor = shellExecutor;
        _logger = logger;
    }

    public async Task<ServiceEnvironmentInfo> DetectAsync(CancellationToken cancellationToken = default)
    {
        // 缓存 10 秒，避免并发请求时重复高频探测底层文件系统
        if (_cachedInfo != null && DateTime.UtcNow - _lastDetectTime < TimeSpan.FromSeconds(10))
        {
            return _cachedInfo;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cachedInfo != null && DateTime.UtcNow - _lastDetectTime < TimeSpan.FromSeconds(10))
            {
                return _cachedInfo;
            }

            _cachedInfo = await PerformDetectionAsync(cancellationToken);
            _lastDetectTime = DateTime.UtcNow;
            return _cachedInfo;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ServiceEnvironmentInfo> PerformDetectionAsync(CancellationToken cancellationToken)
    {
        var isWindows = OperatingSystem.IsWindows();
        var isLinux = OperatingSystem.IsLinux();
        var isMacOS = OperatingSystem.IsMacOS();

        var osName = isWindows ? "Windows" : (isLinux ? "Linux" : (isMacOS ? "macOS" : "Unix"));
        var platformDesc = RuntimeInformation.OSDescription;
        var hostname = Environment.MachineName;
        var is64Bit = Environment.Is64BitOperatingSystem;

        // 1. 容器环境探测
        var isDockerContainer = false;
        var isPrivilegedContainer = false;
        var systemdPierced = false;
        var hasSystemd = false;

        if (isLinux)
        {
            isDockerContainer = File.Exists("/.dockerenv") || CheckCgroupForContainer();

            if (isDockerContainer)
            {
                // 探测特权与宿主 PID 1 穿透能力（具备 /proc/1/ns/mnt 且有 nsenter）
                var nsenterExists = File.Exists("/usr/bin/nsenter") || File.Exists("/bin/nsenter");
                var canAccessHostPid1 = Directory.Exists("/proc/1/ns");

                if (nsenterExists && canAccessHostPid1)
                {
                    // 实际尝试 nsenter 探测宿主 systemctl
                    var testRes = await _shellExecutor.ExecuteAsync(new ShellRequest
                    {
                        CommandText = "nsenter -t 1 -m -u -i -n -- systemctl is-system-running 2>/dev/null",
                        TimeoutSeconds = 3
                    }, cancellationToken);

                    if (testRes.ExitCode == 0 || testRes.StandardOutput.Contains("running") || testRes.StandardOutput.Contains("degraded"))
                    {
                        isPrivilegedContainer = true;
                        systemdPierced = true;
                        hasSystemd = true;
                    }
                }
            }
            else
            {
                // 本地原生 Linux
                var sysTest = await _shellExecutor.ExecuteAsync(new ShellRequest
                {
                    CommandText = "systemctl is-system-running 2>/dev/null",
                    TimeoutSeconds = 3
                }, cancellationToken);

                hasSystemd = sysTest.ExitCode == 0 || sysTest.StandardOutput.Contains("running") || sysTest.StandardOutput.Contains("degraded");
            }
        }
        else if (isWindows)
        {
            // Windows 下标记为可用（走 Windows Services 降级通道）
            hasSystemd = true;
            systemdPierced = false;
        }

        // 2. Docker Daemon Socket 探测
        var hasDockerSocket = false;
        string? dockerVersion = null;

        if (isLinux)
        {
            const string socketPath = "/var/run/docker.sock";
            if (File.Exists(socketPath))
            {
                try
                {
                    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var endpoint = new UnixDomainSocketEndPoint(socketPath);
                    await socket.ConnectAsync(endpoint, cancellationToken);
                    hasDockerSocket = true;
                    dockerVersion = "Docker Daemon (Unix Socket)";
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to connect to local docker unix socket");
                }
            }
        }
        else if (isWindows)
        {
            // Windows 命名管道探测
            const string pipePath = @"\\.\pipe\docker_engine";
            if (File.Exists(pipePath))
            {
                hasDockerSocket = true;
                dockerVersion = "Docker Desktop (Named Pipe)";
            }
        }

        // 如果 socket 没通，尝试 cli 探测
        if (!hasDockerSocket)
        {
            var dockerCliTest = await _shellExecutor.ExecuteAsync(new ShellRequest
            {
                CommandText = isWindows ? "docker version --format \"{{.Server.Version}}\"" : "docker version --format '{{.Server.Version}}' 2>/dev/null",
                TimeoutSeconds = 3
            }, cancellationToken);

            if (dockerCliTest.ExitCode == 0 && !string.IsNullOrWhiteSpace(dockerCliTest.StandardOutput))
            {
                hasDockerSocket = true;
                dockerVersion = "Docker CLI v" + dockerCliTest.StandardOutput.Trim();
            }
        }

        // 3. OpenSSH 服务探测
        var hasSshd = isLinux && (File.Exists("/etc/ssh/sshd_config") || File.Exists("/usr/sbin/sshd"));

        // 4. Samba 服务探测
        var hasSamba = isLinux && (File.Exists("/etc/samba/smb.conf") || File.Exists("/usr/sbin/smbd"));

        // 5. 网络栈管理模式
        string? networkStack = null;
        if (isLinux)
        {
            if (Directory.Exists("/etc/netplan")) networkStack = "Netplan";
            else if (File.Exists("/usr/bin/nmcli")) networkStack = "NetworkManager";
            else if (Directory.Exists("/run/systemd/netif")) networkStack = "systemd-networkd";
            else networkStack = "Linux-Native (iproute2)";
        }
        else if (isWindows)
        {
            networkStack = "Windows-Net";
        }

        // 6. 防火墙探测
        string? firewallType = null;
        if (isLinux)
        {
            if (File.Exists("/usr/sbin/ufw")) firewallType = "ufw";
            else if (File.Exists("/usr/bin/firewall-cmd")) firewallType = "firewalld";
            else if (File.Exists("/usr/sbin/nft")) firewallType = "nftables";
            else if (File.Exists("/usr/sbin/iptables")) firewallType = "iptables";
            else firewallType = "none";
        }
        else if (isWindows)
        {
            firewallType = "windows-firewall";
        }

        // 7. 脚本运行时探测
        var installedRunners = new List<string>();
        if (isLinux)
        {
            if (File.Exists("/bin/bash") || File.Exists("/usr/bin/bash")) installedRunners.Add("bash");
            if (File.Exists("/bin/sh") || File.Exists("/usr/bin/sh")) installedRunners.Add("sh");
            if (File.Exists("/usr/bin/python3") || File.Exists("/bin/python3")) installedRunners.Add("python3");
            if (File.Exists("/usr/bin/node") || File.Exists("/bin/node")) installedRunners.Add("node");
            if (File.Exists("/usr/bin/pwsh")) installedRunners.Add("pwsh");
        }
        else if (isWindows)
        {
            installedRunners.Add("powershell");
            installedRunners.Add("cmd");
            if (ProbeCommandWindows("node -v")) installedRunners.Add("node");
            if (ProbeCommandWindows("python --version")) installedRunners.Add("python3");
            if (ProbeCommandWindows("bash --version")) installedRunners.Add("bash");
        }

        return new ServiceEnvironmentInfo
        {
            OperatingSystem = osName,
            Platform = platformDesc,
            Is64Bit = is64Bit,
            Hostname = hostname,
            KernelVersion = Environment.OSVersion.VersionString,
            IsDockerContainer = isDockerContainer,
            IsPrivilegedContainer = isPrivilegedContainer,
            HasSystemd = hasSystemd,
            SystemdPierced = systemdPierced,
            HasDockerSocket = hasDockerSocket,
            DockerVersion = dockerVersion,
            HasSshd = hasSshd,
            HasSamba = hasSamba,
            NetworkStack = networkStack,
            FirewallType = firewallType,
            InstalledRunners = installedRunners
        };
    }

    private static bool CheckCgroupForContainer()
    {
        try
        {
            if (!File.Exists("/proc/1/cgroup")) return false;
            var text = File.ReadAllText("/proc/1/cgroup");
            return text.Contains("docker") || text.Contains("containerd") || text.Contains("kubepods");
        }
        catch
        {
            return false;
        }
    }

    private static bool ProbeCommandWindows(string cmd)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo("cmd.exe", $"/c {cmd}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            return proc.WaitForExit(1000) && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
