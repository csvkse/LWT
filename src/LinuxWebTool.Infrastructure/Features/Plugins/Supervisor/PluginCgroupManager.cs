using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

/// <summary>
/// Linux 原生 Cgroups v2 资源配额与故障域隔离管理器 (Kernel Cgroups v2 Quota Controller)
/// - 依托 Linux 特权容器挂载的 /sys/fs/cgroup/ 进行内核级资源限额；
/// - 提供 CPU 硬限额 (cpu.max) 防死循环与算力抢占；
/// - 提供内存硬上限 (memory.max) 建立硬 OOM 故障域隔离（内存溢出时仅杀死插件子进程，绝不波及宿主）；
/// - 提供最大进程数 (pids.max) 彻底免疫 Fork 炸弹攻击；
/// - 非 Linux 或无 cgroups 权限时自动平滑降级，保证系统健壮性。
/// </summary>
public static partial class PluginCgroupManager
{
    private const string CgroupBasePath = "/sys/fs/cgroup";
    private const string LwtCgroupRoot = "/sys/fs/cgroup/lwt/plugins";

    public static bool IsCgroupsV2Supported()
    {
        return OperatingSystem.IsLinux() &&
               Directory.Exists(CgroupBasePath) &&
               File.Exists(Path.Combine(CgroupBasePath, "cgroup.controllers"));
    }

    /// <summary>
    /// 为指定插件初始化或更新 Cgroup v2 隔离限额。
    /// </summary>
    public static bool ConfigureCgroup(string pluginId, PluginManifest manifest)
    {
        if (!IsCgroupsV2Supported()) return false;

        try
        {
            var pluginCgroupPath = Path.Combine(LwtCgroupRoot, pluginId);
            Directory.CreateDirectory(pluginCgroupPath);

            // 1. 配置 CPU 限制 (cpu.max: quota period)
            // 例如 0.5 核心 -> 50000 100000; 1.0 核心 -> 100000 100000
            var cpuMaxFile = Path.Combine(pluginCgroupPath, "cpu.max");
            var cpuValue = ParseCpuQuota(manifest);
            if (!string.IsNullOrEmpty(cpuValue))
            {
                File.WriteAllText(cpuMaxFile, cpuValue);
            }

            // 2. 配置内存上限 (memory.max: bytes)
            var memMaxFile = Path.Combine(pluginCgroupPath, "memory.max");
            var memBytes = ParseMemoryLimitBytes(manifest);
            if (memBytes > 0)
            {
                File.WriteAllText(memMaxFile, memBytes.ToString());
            }

            // 3. 配置最大进程数 (pids.max: limit)
            var pidsMaxFile = Path.Combine(pluginCgroupPath, "pids.max");
            var maxPids = ParseMaxPids(manifest);
            File.WriteAllText(pidsMaxFile, maxPids.ToString());

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 将已拉起的插件进程 PID 注入对应的 Cgroup 隔离子树中。
    /// </summary>
    public static bool AssignProcess(string pluginId, int pid)
    {
        if (!IsCgroupsV2Supported()) return false;

        try
        {
            var procsFile = Path.Combine(LwtCgroupRoot, pluginId, "cgroup.procs");
            if (File.Exists(procsFile))
            {
                File.WriteAllText(procsFile, pid.ToString());
                return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>
    /// 插件退出后清理专属 Cgroup 目录。
    /// </summary>
    public static void CleanupCgroup(string pluginId)
    {
        if (!IsCgroupsV2Supported()) return;

        try
        {
            var pluginCgroupPath = Path.Combine(LwtCgroupRoot, pluginId);
            if (Directory.Exists(pluginCgroupPath))
            {
                Directory.Delete(pluginCgroupPath, true);
            }
        }
        catch { }
    }

    private static string? ParseCpuQuota(PluginManifest manifest)
    {
        var rawCpu = manifest.Resources?.Limits?.Cpu?.Trim();
        if (!string.IsNullOrEmpty(rawCpu))
        {
            double cores = 1.0;
            if (rawCpu.EndsWith("m", StringComparison.OrdinalIgnoreCase) && double.TryParse(rawCpu[..^1], out var millis))
            {
                cores = millis / 1000.0;
            }
            else if (double.TryParse(rawCpu, out var parsedCores))
            {
                cores = parsedCores;
            }

            if (cores > 0)
            {
                var quota = (long)(cores * 100000);
                return $"{quota} 100000";
            }
        }

        // 默认若未声明，对微服务施加 2.0 核硬上限防死锁占用
        return "200000 100000";
    }

    private static long ParseMemoryLimitBytes(PluginManifest manifest)
    {
        var rawMem = manifest.Resources?.Limits?.Memory?.Trim();
        if (!string.IsNullOrEmpty(rawMem))
        {
            var multiplier = 1L;
            var numPart = rawMem;
            if (rawMem.EndsWith("Gi", StringComparison.OrdinalIgnoreCase) || rawMem.EndsWith("G", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1024L * 1024 * 1024;
                numPart = rawMem.TrimEnd('i', 'I', 'g', 'G');
            }
            else if (rawMem.EndsWith("Mi", StringComparison.OrdinalIgnoreCase) || rawMem.EndsWith("M", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1024L * 1024;
                numPart = rawMem.TrimEnd('i', 'I', 'm', 'M');
            }
            else if (rawMem.EndsWith("Ki", StringComparison.OrdinalIgnoreCase) || rawMem.EndsWith("K", StringComparison.OrdinalIgnoreCase))
            {
                multiplier = 1024L;
                numPart = rawMem.TrimEnd('i', 'I', 'k', 'K');
            }

            if (double.TryParse(numPart, out var val) && val > 0)
            {
                return (long)(val * multiplier);
            }
        }

        // 默认 512 MiB 硬限额，保护宿主 WebHost 内存空间
        return 512L * 1024 * 1024;
    }

    private static int ParseMaxPids(PluginManifest manifest)
    {
        if (manifest.Resources?.Limits?.Pids is > 0 and var pids)
        {
            return pids;
        }

        // 默认单插件最大 128 个并发子进程，彻底消除 Fork 炸弹
        return 128;
    }
}
