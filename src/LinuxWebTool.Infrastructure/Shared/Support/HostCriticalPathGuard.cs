namespace LinuxWebTool.Infrastructure.Shared.Support;

/// <summary>
/// 宿主主机关键路径防护守卫：
/// 拦截在穿透至物理宿主机时，针对系统关键目录与虚拟伪文件系统的高危/破坏性操作（删除、覆盖写、破坏性重命名等）。
/// </summary>
public static class HostCriticalPathGuard
{
    private static readonly HashSet<string> BlacklistedRoots = new(StringComparer.Ordinal)
    {
        "/", "/bin", "/sbin", "/boot", "/dev", "/etc", "/lib", "/lib64",
        "/proc", "/sys", "/usr", "/var", "/root"
    };

    /// <summary>
    /// 判定目标宿主虚拟路径是否受保护，若受保护则禁止破坏性操作。
    /// </summary>
    /// <param name="virtualPath">规范化的虚拟宿主路径（如 /etc）</param>
    /// <param name="isDestructiveOperation">是否为破坏性操作（删除、重命名、覆盖写入等）</param>
    /// <param name="reason">拦截理由</param>
    /// <returns>true 表示受保护拦截；false 表示放行</returns>
    public static bool IsProtected(string virtualPath, bool isDestructiveOperation, out string? reason)
    {
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            reason = "路径不能为空";
            return true;
        }

        string normalized;
        try
        {
            normalized = HostPathResolver.NormalizePosixPath(virtualPath);
        }
        catch (Exception ex)
        {
            reason = $"非法路径: {ex.Message}";
            return true;
        }

        if (isDestructiveOperation)
        {
            if (normalized == "/" || BlacklistedRoots.Contains(normalized))
            {
                reason = $"宿主系统核心受保护目录 '{normalized}' 严禁执行删除或破坏性操作";
                return true;
            }

            // 禁止在 /proc、/sys、/dev 等虚拟伪文件系统或特殊设备节点下执行写入/删除/创建
            if (normalized.StartsWith("/proc/", StringComparison.Ordinal) ||
                normalized.StartsWith("/sys/", StringComparison.Ordinal) ||
                normalized.StartsWith("/dev/", StringComparison.Ordinal))
            {
                reason = $"宿主系统虚拟伪文件系统 '{normalized}' 严禁执行写或删除操作";
                return true;
            }
        }

        reason = null;
        return false;
    }
}
