using System.Runtime.InteropServices;
using System.Security;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// 受信原生程序解析器：
/// - package 模式：必须位于插件目录内，阻断路径逃逸和越界链接；
/// - system 模式：必须为管理员批准的登记路径或已知受限系统工具，禁止任意可执行程序注入。
/// </summary>
public sealed class NativeProgramResolver : INativeProgramResolver
{
    string INativeProgramResolver.ResolveExecutable(string source, string programRefOrEntrypoint, string packageDir)
        => ResolveExecutable(source, programRefOrEntrypoint, packageDir);
    private static readonly HashSet<string> ApprovedSystemTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "ffmpeg", "ffprobe", "tar", "gzip", "zip", "unzip", "caddy", "nginx", "curl", "ping"
    };

    /// <summary>
    /// 解析并核验原生可执行文件的物理绝对路径。
    /// </summary>
    public static string ResolveExecutable(string source, string programRefOrEntrypoint, string packageDir)
    {
        if (string.IsNullOrWhiteSpace(programRefOrEntrypoint))
        {
            throw new ArgumentException("可执行程序引用或入口文件名不能为空。", nameof(programRefOrEntrypoint));
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        if (string.Equals(source, "system", StringComparison.OrdinalIgnoreCase))
        {
            // System 模式：必须是绝对路径且文件存在，或者是预批准的系统工具
            if (Path.IsPathRooted(programRefOrEntrypoint))
            {
                var fullSystemPath = Path.GetFullPath(programRefOrEntrypoint);
                if (!File.Exists(fullSystemPath))
                {
                    throw new FileNotFoundException($"系统可执行程序不存在: '{fullSystemPath}'");
                }
                return fullSystemPath;
            }

            // 若不是绝对路径，必须属于已批准的白名单系统工具
            if (!ApprovedSystemTools.Contains(programRefOrEntrypoint))
            {
                throw new SecurityException($"未授权的系统程序引用: '{programRefOrEntrypoint}'。非包内程序必须为登记的受批准工具或绝对路径。");
            }

            // 在系统 PATH 中定位该受批准工具，避免同名注入
            var resolvedPath = FindSystemBinary(programRefOrEntrypoint, isWindows);
            if (resolvedPath == null || !File.Exists(resolvedPath))
            {
                throw new FileNotFoundException($"未在系统 PATH 中找到受批准的工具: '{programRefOrEntrypoint}'");
            }
            return resolvedPath;
        }

        // Package 模式：可执行文件必须严格存在于 packageDir 内部
        var rawEntry = programRefOrEntrypoint.Trim();
        if (rawEntry.Contains("..") || rawEntry.Contains(':') || rawEntry.StartsWith('/') || rawEntry.StartsWith('\\'))
        {
            throw new SecurityException($"包内程序入口路径逃逸被拦截: '{programRefOrEntrypoint}'");
        }

        var fullPackageDir = Path.GetFullPath(packageDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var combinedPath = Path.GetFullPath(Path.Combine(fullPackageDir, rawEntry));

        if (!combinedPath.StartsWith(fullPackageDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !combinedPath.Equals(fullPackageDir, StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException($"包内程序逃逸出包目录: '{combinedPath}' 不在 '{fullPackageDir}' 内");
        }

        // 如果在 Windows 下且没有指定 .exe，尝试检查 .exe 后缀
        if (!File.Exists(combinedPath))
        {
            if (isWindows && !combinedPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(combinedPath + ".exe"))
            {
                combinedPath += ".exe";
            }
            else
            {
                throw new FileNotFoundException($"包内可执行文件不存在: '{combinedPath}'");
            }
        }

        // 非 Windows 环境下尝试确保具有执行权限 (+x)
        if (!isWindows)
        {
            try
            {
                var mode = File.GetUnixFileMode(combinedPath);
                if (!mode.HasFlag(UnixFileMode.UserExecute))
                {
                    File.SetUnixFileMode(combinedPath, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
            }
            catch
            {
                // 忽略权限设置异常，交由进程启动器捕获权限错误
            }
        }

        return combinedPath;
    }

    private static string? FindSystemBinary(string binaryName, bool isWindows)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        var paths = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = isWindows ? new[] { ".exe", ".cmd", ".bat", "" } : new[] { "" };

        foreach (var dir in paths)
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim(), binaryName + ext);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }
}
