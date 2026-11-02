using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager
{
    private static string? _cachedDistroKey;

    public static string DetectCurrentDistroKey()
    {
        if (_cachedDistroKey != null) return _cachedDistroKey;

        if (OperatingSystem.IsWindows())
        {
            return _cachedDistroKey = "windows";
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (File.Exists("/etc/os-release"))
                {
                    var content = File.ReadAllText("/etc/os-release");
                    if (Regex.Match(content, @"^ID=(?:""?)(?<id>[^""\r\n]+)(?:""?)", RegexOptions.Multiline) is { Success: true } m)
                    {
                        var id = m.Groups["id"].Value.ToLowerInvariant();
                        if (id.Contains("alpine")) return _cachedDistroKey = "alpine";
                        if (id.Contains("debian") || id.Contains("ubuntu")) return _cachedDistroKey = "debian";
                        if (id.Contains("rhel") || id.Contains("centos") || id.Contains("fedora") || id.Contains("rocky") || id.Contains("almalinux")) return _cachedDistroKey = "rhel";
                        if (id.Contains("arch")) return _cachedDistroKey = "arch";
                    }
                }

                if (File.Exists("/sbin/apk") || File.Exists("/usr/bin/apk")) return _cachedDistroKey = "alpine";
                if (File.Exists("/usr/bin/apt") || File.Exists("/usr/bin/apt-get")) return _cachedDistroKey = "debian";
                if (File.Exists("/usr/bin/dnf") || File.Exists("/usr/bin/yum")) return _cachedDistroKey = "rhel";
                if (File.Exists("/usr/bin/pacman")) return _cachedDistroKey = "arch";
            }
            catch
            {
                // Fallback
            }
            return _cachedDistroKey = "linux";
        }

        if (OperatingSystem.IsMacOS())
        {
            return _cachedDistroKey = "macos";
        }

        return _cachedDistroKey = "linux";
    }

    private static readonly ConcurrentDictionary<string, (bool isInstalled, DateTime checkedAt)> _probeCache = new(StringComparer.OrdinalIgnoreCase);

    public static bool CheckBinaryAvailable(string command, IReadOnlyList<string>? args = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        var cacheKey = command + " " + string.Join(" ", args ?? []);
        if (_probeCache.TryGetValue(cacheKey, out var entry) && (DateTime.UtcNow - entry.checkedAt).TotalSeconds < 10)
        {
            return entry.isInstalled;
        }

        var installed = ProbeBinary(command, args);
        _probeCache[cacheKey] = (installed, DateTime.UtcNow);
        return installed;
    }

    private static bool ProbeBinary(string command, IReadOnlyList<string>? args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = command,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            if (args != null && args.Count > 0)
            {
                foreach (var a in args) psi.ArgumentList.Add(a);
            }
            else
            {
                psi.Arguments = "--version";
            }
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            if (!proc.WaitForExit(2000))
            {
                try { proc.Kill(); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
