using System.IO.Compression;
using System.Security;
using System.Text.RegularExpressions;

namespace LinuxWebTool.Contracts.Shared.Support;

/// <summary>
/// 插件平台统一安全守卫：
/// - 插件 ID 与动作名称严格单段校验（防路径穿越、Windows 保留设备名、ADS 数据流与控制字符）；
/// - 插件安装目录与目标路径包含性检查；
/// - 产物路径防越界逃逸；
/// - ZIP 包防 Zip Slip、展开炸弹与大小/文件数硬上限校验。
/// </summary>
public static partial class PluginSecurityGuard
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.]{1,64}$")]
    private static partial Regex SafeIdRegex();

    [GeneratedRegex(@"^[a-zA-Z0-9_\-]{1,64}$")]
    private static partial Regex SafeActionRegex();

    /// <summary>
    /// 验证插件 ID 是否合法有效。
    /// 必须为 1-64 位的安全英文字符、数字、下划线、中划线或单点，禁止路径分隔符、双点穿越及设备保留名。
    /// </summary>
    public static bool IsValidPluginId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        var trimmed = id.Trim();
        if (trimmed == "." || trimmed == ".." || trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains(':'))
            return false;

        if (ReservedNames.Contains(trimmed)) return false;

        // 禁止末尾有点或空格（Windows 容易产生脱水或不可删文件）
        if (trimmed.EndsWith('.') || trimmed.EndsWith(' ')) return false;

        return SafeIdRegex().IsMatch(trimmed);
    }

    /// <summary>
    /// 验证动作名称是否合法有效。
    /// </summary>
    public static bool IsValidActionName(string? action)
    {
        if (string.IsNullOrWhiteSpace(action) || action.Length > 64) return false;
        var trimmed = action.Trim();
        if (trimmed.Contains('/') || trimmed.Contains('\\') || trimmed.Contains(':') || trimmed.Contains(".."))
            return false;

        if (ReservedNames.Contains(trimmed)) return false;

        return SafeActionRegex().IsMatch(trimmed);
    }

    /// <summary>
    /// 获取受信任且位于基准根目录内的插件绝对物理路径，若非法则抛出 SecurityException。
    /// </summary>
    public static string EnsureSafePluginDirectory(string baseDir, string pluginId)
    {
        if (!IsValidPluginId(pluginId))
        {
            throw new SecurityException($"拒绝非法插件 ID: '{pluginId}'，必须由 1-64 位字母、数字、下划线或连字符组成。");
        }

        var fullBase = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullTarget = Path.GetFullPath(Path.Combine(fullBase, pluginId)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!fullTarget.StartsWith(fullBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !fullTarget.Equals(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"路径逃逸阻断: 插件目录 '{fullTarget}' 越界超出基础目录 '{fullBase}'");
        }

        return fullTarget;
    }

    /// <summary>
    /// 验证产物 ID 是否合法有效。
    /// </summary>
    public static bool IsValidArtifactId(string? artifactId)
    {
        return IsValidActionName(artifactId);
    }

    /// <summary>
    /// 获取受信任且位于 operation 目录内的安全产物物理路径，防止路径逃逸与穿越。
    /// </summary>
    public static string EnsureSafeArtifactPath(string operationArtifactDir, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new SecurityException("产物相对路径不能为空。");

        if (Path.IsPathRooted(relativePath) || relativePath.StartsWith('/') || relativePath.StartsWith('\\'))
            throw new SecurityException($"产物路径必须为相对路径: '{relativePath}'");

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.Contains("..") || normalized.Contains(':'))
            throw new SecurityException($"非法产物路径穿越: '{relativePath}'");

        var segs = normalized.Split('/');
        foreach (var seg in segs)
        {
            var nameWithoutExt = Path.GetFileNameWithoutExtension(seg);
            if (ReservedNames.Contains(nameWithoutExt) || ReservedNames.Contains(seg))
            {
                throw new SecurityException($"非法设备保留名: '{relativePath}'");
            }
        }

        var fullBase = Path.GetFullPath(operationArtifactDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullTarget = Path.GetFullPath(Path.Combine(fullBase, normalized)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!fullTarget.StartsWith(fullBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !fullTarget.Equals(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"产物路径逃逸阻断: '{fullTarget}' 越界超出操作目录 '{fullBase}'");
        }

        return fullTarget;
    }

    /// <summary>
    /// 校验压缩包安全性（Zip Slip、总解压尺寸炸弹、单文件尺寸、文件项总数及路径超长）。
    /// </summary>
    public static void ValidateZipArchive(
        ZipArchive archive,
        int maxEntries = 10000,
        long maxTotalBytes = 512L * 1024 * 1024,
        long maxSingleBytes = 256L * 1024 * 1024,
        int maxPathLength = 1024)
    {
        if (archive.Entries.Count > maxEntries)
        {
            throw new SecurityException($"非法压缩包：文件条目数量 ({archive.Entries.Count}) 超过单包上限 ({maxEntries})。");
        }

        long cumulativeBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            if (string.IsNullOrEmpty(name)) continue;

            if (name.Length > maxPathLength)
            {
                throw new SecurityException($"非法压缩包：条目路径过长 ({name.Length} > {maxPathLength})。");
            }

            var normalized = name.Replace('\\', '/');
            if (normalized.Contains("..") || normalized.StartsWith('/') || normalized.Contains(':'))
            {
                throw new SecurityException($"非法压缩包：检测到潜在的 Zip Slip 路径穿越或非法字符: '{name}'");
            }

            if (entry.Length > maxSingleBytes)
            {
                throw new SecurityException($"非法压缩包：单文件条目解压尺寸 ({entry.Length} bytes) 超过单文件上限 ({maxSingleBytes} bytes)。");
            }

            cumulativeBytes += entry.Length;
            if (cumulativeBytes > maxTotalBytes)
            {
                throw new SecurityException($"非法压缩包：展开后总尺寸超过配额上限 ({maxTotalBytes} bytes)，疑似解压炸弹。");
            }
        }
    }
}
