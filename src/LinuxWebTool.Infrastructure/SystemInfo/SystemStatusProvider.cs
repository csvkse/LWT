using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Interfaces;
using LinuxWebTool.Contracts.Models;

namespace LinuxWebTool.Infrastructure.SystemInfo;

/// <summary>
/// 系统状态采集器：
/// - Linux 主路径：/proc（stat/meminfo/loadavg/net/dev/cpuinfo/uptime/os-release）+ df -kP + ps；
/// - Windows 开发降级：GetSystemTimes / GlobalMemoryStatusEx / DriveInfo（网卡与进程列表不采集）。
/// CPU 与网卡速率基于采样窗口差值，内部持有上次基准；即时 API 与后台采样共用同一基准。
/// </summary>
public sealed partial class SystemStatusProvider(ILogger<SystemStatusProvider> logger) : ISystemStatusProvider
{
    private const int SampleWindowMs = 300;

    private readonly object _sync = new();
    private (DateTime Time, long Idle, long Total)? _lastCpuSample;
    private Dictionary<string, (DateTime Time, long Recv, long Sent)>? _lastNetSample;

    public Task<SystemStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Collect, cancellationToken);

    private SystemStatusResult Collect()
    {
        lock (_sync)
        {
            var cpu = OperatingSystem.IsWindows() ? SampleCpuWindows() : SampleCpuLinux();
            var memory = OperatingSystem.IsWindows() ? SampleMemoryWindows() : SampleMemoryLinux();
            return new SystemStatusResult
            {
                Host = CollectHost(),
                Cpu = cpu,
                Memory = memory,
                Disks = OperatingSystem.IsWindows() ? CollectDisksWindows() : CollectDisksLinux(),
                Networks = OperatingSystem.IsWindows() ? [] : SampleNetworkLinux(),
                TopCpuProcesses = OperatingSystem.IsWindows() ? [] : CollectTopProcessesLinux(byCpu: true),
                TopMemProcesses = OperatingSystem.IsWindows() ? [] : CollectTopProcessesLinux(byCpu: false),
                SampledAt = DateTime.Now,
            };
        }
    }

    // ---------- 主机 ----------

    private static HostInfo CollectHost()
    {
        var osName = OperatingSystem.IsWindows()
            ? Environment.OSVersion.VersionString
            : ReadOsPrettyName();
        var kernel = OperatingSystem.IsWindows()
            ? $"Windows NT {Environment.OSVersion.Version}"
            : ReadFileSafe("/proc/sys/kernel/osrelease") ?? "unknown";
        var uptimeSeconds = OperatingSystem.IsWindows()
            ? (long)(NativeMethods.GetTickCount64() / 1000)
            : (long)ParseDouble(ReadFileSafe("/proc/uptime")?.Split(' ')[0]);

        return new HostInfo
        {
            HostName = Environment.MachineName,
            OsName = osName,
            KernelVersion = kernel,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            UptimeSeconds = uptimeSeconds,
        };
    }

    private static string ReadOsPrettyName()
    {
        var content = ReadFileSafe("/etc/os-release");
        if (content is null)
        {
            return "Linux";
        }
        return Regex.Match(content, @"^PRETTY_NAME=""?(?<name>[^""]+)""?", RegexOptions.Multiline) is { Success: true } match
            ? match.Groups["name"].Value
            : "Linux";
    }

    // ---------- CPU ----------

    private CpuStatus SampleCpuLinux()
    {
        var current = ReadCpuTimesLinux();
        if (_lastCpuSample is null || (DateTime.Now - _lastCpuSample.Value.Time).TotalMilliseconds < 250)
        {
            Thread.Sleep(SampleWindowMs);
            current = ReadCpuTimesLinux();
        }

        var usage = 0.0;
        if (_lastCpuSample is { } last && current.Total > last.Total)
        {
            var idleDelta = current.Idle - last.Idle;
            var totalDelta = current.Total - last.Total;
            usage = Math.Clamp(100.0 * (1 - (double)idleDelta / totalDelta), 0, 100);
        }
        _lastCpuSample = current;

        var loadLine = ReadFileSafe("/proc/loadavg") ?? "0 0 0";
        var loads = loadLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cores = CountCpuCoresLinux();

        return new CpuStatus
        {
            ModelName = ReadCpuModelLinux(),
            CoreCount = cores,
            UsagePercent = usage,
            Load1 = loads.Length > 0 ? ParseDouble(loads[0]) : 0,
            Load5 = loads.Length > 1 ? ParseDouble(loads[1]) : 0,
            Load15 = loads.Length > 2 ? ParseDouble(loads[2]) : 0,
        };
    }

    private static (DateTime Time, long Idle, long Total) ReadCpuTimesLinux()
    {
        var firstLine = ReadFileSafe("/proc/stat")?.Split('\n').FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal));
        // cpu  user nice system idle iowait irq softirq steal ...
        var parts = firstLine?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        long idle = 0, total = 0;
        if (parts.Length >= 5)
        {
            var values = parts.Skip(1).Select(long.Parse).ToArray();
            idle = values[3] + (values.Length > 4 ? values[4] : 0);
            total = values.Take(Math.Min(values.Length, 8)).Sum();
        }
        return (DateTime.Now, idle, total);
    }

    private static int CountCpuCoresLinux()
    {
        var stat = ReadFileSafe("/proc/stat");
        if (stat is not null)
        {
            var cores = stat.Split('\n').Count(line => Regex.IsMatch(line, @"^cpu\d+\s"));
            if (cores > 0)
            {
                return cores;
            }
        }
        return Environment.ProcessorCount;
    }

    private static string ReadCpuModelLinux()
    {
        var cpuinfo = ReadFileSafe("/proc/cpuinfo");
        if (cpuinfo is null)
        {
            return "unknown";
        }
        var match = Regex.Match(cpuinfo, @"^model name\s*:\s*(.+)$", RegexOptions.Multiline)
            is { Success: true } m ? m : Regex.Match(cpuinfo, @"^Model\s*:\s*(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : "unknown";
    }

    [SupportedOSPlatform("windows")]
    private CpuStatus SampleCpuWindows()
    {
        var current = ReadCpuTimesWindows();
        if (_lastCpuSample is null || (DateTime.Now - _lastCpuSample.Value.Time).TotalMilliseconds < 250)
        {
            Thread.Sleep(SampleWindowMs);
            current = ReadCpuTimesWindows();
        }

        var usage = 0.0;
        if (_lastCpuSample is { } last && current.Total > last.Total)
        {
            var idleDelta = current.Idle - last.Idle;
            var totalDelta = current.Total - last.Total;
            usage = Math.Clamp(100.0 * (1 - (double)idleDelta / totalDelta), 0, 100);
        }
        _lastCpuSample = current;

        return new CpuStatus
        {
            ModelName = "unknown",
            CoreCount = Environment.ProcessorCount,
            UsagePercent = usage,
        };
    }

    [SupportedOSPlatform("windows")]
    private static (DateTime Time, long Idle, long Total) ReadCpuTimesWindows()
    {
        NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user);
        return (DateTime.Now, idle, kernel + user);
    }

    // ---------- 内存 ----------

    private static MemoryStatus SampleMemoryLinux()
    {
        var content = ReadFileSafe("/proc/meminfo") ?? string.Empty;
        long GetKb(string key) =>
            Regex.Match(content, $@"^{key}:\s+(\d+)\s*kB", RegexOptions.Multiline) is { Success: true } m
                ? long.Parse(m.Groups[1].Value) * 1024
                : 0;

        var total = GetKb("MemTotal");
        var available = GetKb("MemAvailable");
        var swapTotal = GetKb("SwapTotal");
        var swapFree = GetKb("SwapFree");
        var used = Math.Max(0, total - available);

        return new MemoryStatus
        {
            TotalBytes = total,
            UsedBytes = used,
            AvailableBytes = available,
            UsagePercent = total > 0 ? Math.Round(100.0 * used / total, 1) : 0,
            SwapTotalBytes = swapTotal,
            SwapUsedBytes = Math.Max(0, swapTotal - swapFree),
        };
    }

    [SupportedOSPlatform("windows")]
    private static MemoryStatus SampleMemoryWindows()
    {
        var buffer = new NativeMethods.MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<NativeMethods.MemoryStatusEx>() };
        NativeMethods.GlobalMemoryStatusEx(ref buffer);
        var used = buffer.ullTotalPhys - buffer.ullAvailPhys;
        return new MemoryStatus
        {
            TotalBytes = (long)buffer.ullTotalPhys,
            UsedBytes = (long)used,
            AvailableBytes = (long)buffer.ullAvailPhys,
            UsagePercent = buffer.ullTotalPhys > 0 ? Math.Round(100.0 * used / buffer.ullTotalPhys, 1) : 0,
            SwapTotalBytes = (long)buffer.ullTotalPageFile,
            SwapUsedBytes = (long)(buffer.ullTotalPageFile - buffer.ullAvailPageFile),
        };
    }

    // ---------- 磁盘 ----------

    private static List<DiskStatus> CollectDisksLinux()
    {
        var output = RunCapture("df", "-kP", 5000);
        if (output is null)
        {
            return [];
        }

        var disks = new List<DiskStatus>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Filesystem 1024-blocks Used Available Capacity Mounted-on
            if (parts.Length < 6)
            {
                continue;
            }
            var filesystem = parts[0];
            var mount = parts[5];
            // 只保留真实块设备，排除 tmpfs/overlay/loop 等虚拟文件系统。
            if ((!filesystem.StartsWith("/dev/", StringComparison.Ordinal) || filesystem.StartsWith("/dev/loop", StringComparison.Ordinal))
                && mount != "/")
            {
                continue;
            }
            // 排除容器环境注入的 /etc 单文件绑定与虚拟目录挂载。
            if (mount.StartsWith("/etc/", StringComparison.Ordinal) || mount.StartsWith("/proc/", StringComparison.Ordinal)
                || mount.StartsWith("/sys/", StringComparison.Ordinal) || mount.StartsWith("/dev/", StringComparison.Ordinal))
            {
                continue;
            }

            var totalKb = ParseLong(parts[1]);
            var usedKb = ParseLong(parts[2]);
            var freeKb = ParseLong(parts[3]);
            if (totalKb <= 0)
            {
                continue;
            }
            disks.Add(new DiskStatus
            {
                Mount = mount,
                FileSystem = filesystem,
                TotalBytes = totalKb * 1024,
                UsedBytes = usedKb * 1024,
                FreeBytes = freeKb * 1024,
                UsagePercent = Math.Round(100.0 * usedKb / totalKb, 1),
            });
        }
        return disks;
    }

    private static List<DiskStatus> CollectDisksWindows()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d =>
            {
                var total = d.TotalSize;
                var free = d.AvailableFreeSpace;
                return new DiskStatus
                {
                    Mount = d.Name,
                    FileSystem = d.DriveFormat,
                    TotalBytes = total,
                    FreeBytes = free,
                    UsedBytes = total - free,
                    UsagePercent = total > 0 ? Math.Round(100.0 * (total - free) / total, 1) : 0,
                };
            })
            .ToList();
    }

    // ---------- 网络 ----------

    private List<NetworkStatus> SampleNetworkLinux()
    {
        var content = ReadFileSafe("/proc/net/dev");
        if (content is null)
        {
            return [];
        }

        var now = DateTime.Now;
        var current = new Dictionary<string, (long Recv, long Sent)>();
        foreach (var line in content.Split('\n').Skip(2))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }
            var name = line[..separator].Trim();
            if (name == "lo")
            {
                continue;
            }
            var values = line[(separator + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (values.Length < 9)
            {
                continue;
            }
            current[name] = (ParseLong(values[0]), ParseLong(values[8]));
        }

        var networks = new List<NetworkStatus>();
        lock (_sync)
        {
            foreach (var (name, (recv, sent)) in current)
            {
                double sentBps = 0, recvBps = 0;
                if (_lastNetSample is { } last && last.TryGetValue(name, out var previous))
                {
                    var seconds = (now - previous.Time).TotalSeconds;
                    if (seconds > 0.2)
                    {
                        recvBps = Math.Max(0, (recv - previous.Recv) / seconds);
                        sentBps = Math.Max(0, (sent - previous.Sent) / seconds);
                    }
                }
                networks.Add(new NetworkStatus
                {
                    Name = name,
                    RecvBytesPerSec = (long)recvBps,
                    SentBytesPerSec = (long)sentBps,
                    TotalRecvBytes = recv,
                    TotalSentBytes = sent,
                });
            }
            _lastNetSample = current.ToDictionary(kv => kv.Key, kv => (now, kv.Value.Recv, kv.Value.Sent));
        }
        return networks;
    }

    // ---------- 进程 ----------

    private static List<ProcessStatus> CollectTopProcessesLinux(bool byCpu)
    {
        // comm 放最后一列，按限次拆分避免进程名含空格时解析错位。
        var output = RunCapture("ps", "-eo pid=,pcpu=,pmem=,rss=,comm=", 5000);
        if (output is null)
        {
            return [];
        }

        var processes = new List<ProcessStatus>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5)
            {
                continue;
            }
            processes.Add(new ProcessStatus
            {
                Pid = (int)ParseLong(parts[0]),
                Name = parts[4].Trim(),
                CpuPercent = ParseDouble(parts[1]),
                MemPercent = ParseDouble(parts[2]),
                MemBytes = ParseLong(parts[3]) * 1024,
            });
        }

        return (byCpu
                ? processes.OrderByDescending(p => p.CpuPercent)
                : processes.OrderByDescending(p => p.MemBytes))
            .Where(p => p.Pid > 0)
            .Take(5)
            .ToList();
    }

    // ---------- 工具 ----------

    private static string? ReadFileSafe(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>内部系统采集（df/ps），不经 IShellExecutor：不占用户执行槽位、不留执行历史。</summary>
    private static string? RunCapture(string fileName, string arguments, int timeoutMs)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            return process.StandardOutput.ReadToEnd();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : 0;

    private static long ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : 0;
}
