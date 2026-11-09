namespace LinuxWebTool.Infrastructure.Shared.Support;

/// <summary>
/// 宿主文件系统路径解析与穿透策略：
/// 负责将前端提交的宿主虚拟路径映射至特权容器的物理挂载视图（/proc/1/root），
/// 并提供宿主绝对符号链接解构、伪文件系统规避与特权环境探测能力。
/// </summary>
public static class HostPathResolver
{
    public const string HostProcRoot = "/proc/1/root";

    /// <summary>
    /// 探测当前运行环境是否具备特权穿透至宿主主机 PID 1 挂载空间的能力。
    /// </summary>
    public static bool CanPierceHost()
    {
        if (!OperatingSystem.IsLinux()) return false;
        return Directory.Exists("/proc/1/ns") && File.Exists("/proc/1/ns/mnt");
    }

    /// <summary>
    /// 跨平台规范化 Linux 宿主绝对虚拟路径（消除 .、..、重复斜杠，严格以 / 开头）。
    /// </summary>
    public static string NormalizePosixPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 4096 || input.Any(char.IsControl))
            throw new ArgumentException("路径不能为空、过长或包含控制字符");

        var path = input.Replace('\\', '/');
        if (!path.StartsWith('/'))
            throw new ArgumentException("宿主虚拟路径必须是以 / 开头的绝对路径");

        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else
            {
                segments.Add(segment);
            }
        }
        return "/" + string.Join('/', segments);
    }

    /// <summary>
    /// 将前端虚拟宿主路径（如 /etc/fstab）解析为容器内的物理穿透路径（如 /proc/1/root/etc/fstab），
    /// 并针对绝对符号链接进行智能解构重映射，杜绝跳回容器根目录的逃逸缺陷。
    /// </summary>
    public static string ResolvePhysicalPath(string virtualPath)
    {
        var normalized = NormalizePosixPath(virtualPath);
        var directPhysical = normalized == "/" ? HostProcRoot : $"{HostProcRoot}{normalized}";

        // 仅在 Linux 环境下执行符号链接物理检测与重映射
        if (OperatingSystem.IsLinux() && (File.Exists(directPhysical) || Directory.Exists(directPhysical)))
        {
            try
            {
                FileSystemInfo fsi = Directory.Exists(directPhysical) 
                    ? new DirectoryInfo(directPhysical) 
                    : new FileInfo(directPhysical);

                if (fsi.LinkTarget is not null && fsi.LinkTarget.StartsWith('/'))
                {
                    // 发现绝对软链接，重写为宿主真实根前缀下的物理目标
                    var remapped = $"{HostProcRoot}{NormalizePosixPath(fsi.LinkTarget)}";
                    if (File.Exists(remapped) || Directory.Exists(remapped))
                    {
                        return remapped;
                    }
                }
            }
            catch
            {
                // 忽略系统受限文件属性异常，回退至直接物理路径
            }
        }

        return directPhysical;
    }

    /// <summary>
    /// 判断是否属于 Linux 宿主的特殊伪文件系统（如 /proc, /sys, /dev），
    /// 该类系统在打包、深度递归或流式全量读取时必须施加防死锁与防溢出限制。
    /// </summary>
    public static bool IsPseudoOrSpecialFileSystem(string virtualPath)
    {
        var normalized = NormalizePosixPath(virtualPath);
        return normalized == "/proc" || normalized.StartsWith("/proc/", StringComparison.Ordinal)
            || normalized == "/sys" || normalized.StartsWith("/sys/", StringComparison.Ordinal)
            || normalized == "/dev" || normalized.StartsWith("/dev/", StringComparison.Ordinal);
    }
}
