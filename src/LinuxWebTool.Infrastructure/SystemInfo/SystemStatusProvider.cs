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
public sealed partial class SystemStatusProvider : ISystemStatusProvider
{
    private const int SampleWindowMs = 300;

    private readonly SystemStatusOptions _options;
    private readonly object _sync = new();
    private (DateTime Time, long Idle, long Total)? _lastCpuSample;
    private Dictionary<string, (DateTime Time, long Recv, long Sent)>? _lastNetSample;
    // 每进程磁盘 IO 差值基准（磁盘 IO 速率 = 两次采样 read/write_bytes 的差值 / 时间差）。
    private Dictionary<int, (DateTime Time, long ReadBytes, long WriteBytes)>? _lastDiskIoSample;
    // nethogs 可用性探测缓存（true=已接入可采；false=不可用永久跳过；null=未探测）。Provider 为单例。
    private bool? _nethogsAvailable;
    // 宿主 df 采集模式探测缓存（Provider 为单例）：true=nsenter 可用（--privileged --pid=host）；false=回退容器自身视图
    private static bool? _hostDfMode;

    /// <summary>
    /// 应用内 SMB 挂载点白名单（SmbMountService 挂载成功 / 启动重挂时注册）。
    /// 命中白名单的挂载点绕过虚拟文件系统过滤，状态页磁盘明细即可展示 SMB/CIFS 挂载。
    /// </summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> ManagedMountPoints =
        new(System.StringComparer.Ordinal);

    public SystemStatusProvider(SystemStatusOptions options)
    {
        _options = options;
    }

    public Task<SystemStatusResult> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(Collect, cancellationToken);

    private SystemStatusResult Collect()
    {
        lock (_sync)
        {
            var cpu = OperatingSystem.IsWindows() ? SampleCpuWindows() : SampleCpuLinux();
            var memory = OperatingSystem.IsWindows() ? SampleMemoryWindows() : SampleMemoryLinux();
            // 每进程网络速率只采一次，供 CPU / 内存两个 Top 列表共享（避免 nethogs 重复运行）。
            var netByPid = OperatingSystem.IsWindows() ? new Dictionary<int, (long Sent, long Recv)>() : SamplePerProcessNet();
            return new SystemStatusResult
            {
                Host = CollectHost(),
                Cpu = cpu,
                Memory = memory,
                Disks = OperatingSystem.IsWindows() ? CollectDisksWindows() : CollectDisksLinux(),
                Networks = OperatingSystem.IsWindows() ? [] : SampleNetworkLinux(),
                TopCpuProcesses = OperatingSystem.IsWindows() ? [] : CollectTopProcessesLinux(byCpu: true, _options.TopProcessCount, netByPid),
                TopMemProcesses = OperatingSystem.IsWindows() ? [] : CollectTopProcessesLinux(byCpu: false, _options.TopProcessCount, netByPid),
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
        // 宿主采集模式：容器以 --privileged --pid=host --user root 运行时，
        // nsenter 进入宿主 mount namespace 执行 df，自动获得宿主全部磁盘（无需逐盘挂载）。
        // 探测失败（非特权/无 nsenter/受限运行时）则永久回退到容器自身视图，直至下次重启。
        if (_hostDfMode != false)
        {
            // nsenter 位于容器 /usr/bin（alpine util-linux-misc）；df 在宿主文件系统内解析（多数发行版为 /usr/bin/df）。
            string? hostDf = RunCapture("/usr/bin/nsenter", "-t 1 -m -- /usr/bin/df -kP", 5000);
            if (string.IsNullOrWhiteSpace(hostDf) || hostDf.Split('\n').Length <= 1)
            {
                hostDf = RunCapture("nsenter", "-t 1 -m -- df -kP", 5000);
            }
            if (!string.IsNullOrWhiteSpace(hostDf) && hostDf.Split('\n').Length > 1)
            {
                _hostDfMode = true;
                return ParseDfOutput(hostDf);
            }
            _hostDfMode = false;
        }

        return ParseDfOutput(RunCapture("df", "-kP", 5000) ?? string.Empty);
    }

    private static List<DiskStatus> ParseDfOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
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
            // 应用内挂载点（SMB 管理）优先放行：cifs 的文件系统列为 //host/share，不在 /dev/ 下。
            var isManaged = ManagedMountPoints.ContainsKey(mount.TrimEnd('/'));
            // 只保留真实块设备，排除 tmpfs/overlay/loop 等虚拟文件系统。
            if (!isManaged
                && (!filesystem.StartsWith("/dev/", StringComparison.Ordinal) || filesystem.StartsWith("/dev/loop", StringComparison.Ordinal))
                && mount != "/")
            {
                continue;
            }
            // 排除容器环境注入的 /etc 单文件绑定与虚拟目录挂载；保留 /mnt（WSL 的 Windows 盘、宿主常规挂载点）。
            if (!isManaged
                && (mount.StartsWith("/etc/", StringComparison.Ordinal) || mount.StartsWith("/proc/", StringComparison.Ordinal)
                || mount.StartsWith("/sys/", StringComparison.Ordinal) || (mount.StartsWith("/dev/", StringComparison.Ordinal) && mount != "/")))
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

    private List<ProcessStatus> CollectTopProcessesLinux(bool byCpu, int topCount, Dictionary<int, (long Sent, long Recv)> netByPid)
    {
        if (topCount <= 0)
        {
            return [];
        }

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

        // 只对入选 Top 的进程补充 IO，避免对整个进程表做 /proc/<pid>/io 与 nethogs 归并。
        var sorted = (byCpu
                ? processes.OrderByDescending(p => p.CpuPercent)
                : processes.OrderByDescending(p => p.MemBytes))
            .Where(p => p.Pid > 0)
            .Take(topCount)
            .ToList();

        // 磁盘 IO（/proc/<pid>/io 差值）按 pid 补充；网络 IO 已由调用方一次采集后传入。
        for (var i = 0; i < sorted.Count; i++)
        {
            var p = sorted[i];
            var disk = SampleDiskIo(p.Pid);
            var net = netByPid.TryGetValue(p.Pid, out var n) ? n : (0L, 0L);
            sorted[i] = p with { DiskReadBps = disk.Read, DiskWriteBps = disk.Write, NetSentBps = net.Item1, NetRecvBps = net.Item2 };
        }

        return sorted;
    }

    private (long Read, long Write) SampleDiskIo(int pid)
    {
        var content = ReadFileSafe($"/proc/{pid}/io");
        if (content is null)
        {
            return (0, 0);
        }

        long Get(string key) =>
            Regex.Match(content, $@"^{key}:\s+(\d+)", RegexOptions.Multiline) is { Success: true } m
                ? ParseLong(m.Groups[1].Value)
                : 0;

        var readBytes = Get("read_bytes");
        var writeBytes = Get("write_bytes");
        var now = DateTime.Now;

        var previous = _lastDiskIoSample?.GetValueOrDefault(pid);
        if (previous is { } last)
        {
            var seconds = (now - last.Time).TotalSeconds;
            if (seconds > 0.01)
            {
                long readBps = (long)Math.Max(0, (readBytes - last.ReadBytes) / seconds);
                long writeBps = (long)Math.Max(0, (writeBytes - last.WriteBytes) / seconds);
                // 更新基准（保留当前累计值，避免累计器重复叠加导致差值负向）。
                (_lastDiskIoSample ??= new())[pid] = (now, readBytes, writeBytes);
                return (readBps, writeBps);
            }
        }
        else
        {
            // 首次采样：记录基准，本次不产生速率（无窗口差值可算）。
            (_lastDiskIoSample ??= new())[pid] = (now, readBytes, writeBytes);
        }

        return (0, 0);
    }

    /// <summary>
    /// 每进程网络速率（nethogs tracemode）。能力探测：nethogs 不可用（未安装 / 非特权无法抓包）
    /// 时返回空，永久跳过，不影响其余采集。tracemode 输出 &lt;name&gt;/&lt;pid&gt;/&lt;uid&gt;\t&lt;sent KB/s&gt;\t&lt;recv KB/s&gt;。
    /// </summary>
    private Dictionary<int, (long Sent, long Recv)> SamplePerProcessNet()
    {
        if (_nethogsAvailable == false)
        {
            return new();
        }

        // 首次探测是否已安装（用 sh 规避 busybox command -v 差异）。
        if (_nethogsAvailable is null)
        {
            var which = RunCapture("sh", "-c \"command -v nethogs\"", 2000);
            if (string.IsNullOrWhiteSpace(which))
            {
                _nethogsAvailable = false;
                return new();
            }
        }

        // -t tracemode；-d 1 刷新间隔 1 秒（产生速率差值）；-c 2 采 2 次后退出（取末次有效速率）。
        var output = RunCapture("nethogs", "-t -d 1 -c 2", 8000);
        if (string.IsNullOrWhiteSpace(output))
        {
            _nethogsAvailable = false;
            return new();
        }

        var result = ParseNetghosOutput(output);
        _nethogsAvailable = result.Count > 0;
        return result;
    }

    private static Dictionary<int, (long Sent, long Recv)> ParseNetghosOutput(string output)
    {
        var result = new Dictionary<int, (long Sent, long Recv)>();
        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Refreshing", StringComparison.Ordinal)
                || line.StartsWith("Unknown connection", StringComparison.Ordinal)
                || line.StartsWith("Ethernet link detected", StringComparison.Ordinal)
                || line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // 每个进程一行：name/pid/uid<TAB>sent<TAB>recv
            var cols = line.Split('\t');
            if (cols.Length < 3)
            {
                continue;
            }
            var meta = cols[0];
            var uidIdx = meta.LastIndexOf('/');
            if (uidIdx <= 0)
            {
                continue;
            }
            var pidIdx = meta.LastIndexOf('/', uidIdx - 1);
            if (pidIdx < 0)
            {
                continue;
            }
            if (!int.TryParse(meta[(pidIdx + 1)..uidIdx], out var pid))
            {
                continue;
            }
            if (pid <= 0)
            {
                continue;
            }
            var sentKb = ParseDouble(cols[1]);
            var recvKb = ParseDouble(cols[2]);
            if (sentKb < 0 || recvKb < 0)
            {
                continue;
            }
            // tracemode 默认 VIEWMODE_KBPS，数值为 KB/s，转字节/秒。
            result[pid] = ((long)(sentKb * 1024), (long)(recvKb * 1024));
        }
        return result;
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
            // 并行读取两个输出流，避免 stderr 填满管道缓冲导致进程阻塞（nethogs 会向 stderr 打 WARNING 等）。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            Task.WaitAll(stdoutTask, stderrTask);
            return stdoutTask.Result;
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
