namespace LinuxWebTool.Infrastructure.Mount;

internal sealed record MountMetadata(string MountPoint, string FileSystem, string Source, bool IsLocal);

/// <summary>解析 /proc/[1|self]/mountinfo。读取文件本身不触发文件系统 stat，失效挂载不会阻塞。</summary>
internal static class LinuxMountInfoParser
{
    private static readonly HashSet<string> VirtualFileSystems = new(StringComparer.OrdinalIgnoreCase)
    {
        "proc", "sysfs", "devtmpfs", "devpts", "tmpfs", "mqueue", "cgroup", "cgroup2", "overlay",
        "squashfs", "securityfs", "pstore", "bpf", "tracefs", "debugfs", "configfs", "fusectl", "hugetlbfs", "ramfs",
    };

    public static IReadOnlyList<MountMetadata> Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        var result = new List<MountMetadata>();
        foreach (var line in content.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var prefix = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var suffix = line[(separator + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (prefix.Length < 5 || suffix.Length < 2)
            {
                continue;
            }

            var fileSystem = suffix[0];
            if (VirtualFileSystems.Contains(fileSystem))
            {
                continue;
            }

            var mountPoint = Decode(prefix[4]);
            var source = Decode(suffix[1]);
            var isLocal = source.StartsWith("/dev/", StringComparison.Ordinal)
                && !source.StartsWith("/dev/loop", StringComparison.Ordinal);
            result.Add(new MountMetadata(mountPoint, fileSystem, source, isLocal));
        }

        return result;
    }

    private static string Decode(string value) => value
        .Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal)
        .Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
}
