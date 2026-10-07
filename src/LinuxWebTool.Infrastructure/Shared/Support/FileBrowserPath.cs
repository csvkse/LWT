namespace LinuxWebTool.Infrastructure.Shared.Support;

public static class FileBrowserPath
{
    private static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 4096 || input.Any(char.IsControl))
            throw new ArgumentException("路径不能为空、过长或包含控制字符");
        var path = input.Replace('\\', '/');
        if (OperatingSystem.IsWindows())
        {
            if (path == "/") return path;
            if (path.StartsWith("//?", StringComparison.Ordinal) || path.StartsWith("//.", StringComparison.Ordinal)
                || path.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0
                || !Path.IsPathFullyQualified(path)
                || path[(path.Length > 1 && path[1] == ':' ? 2 : 0)..].Contains(':'))
                throw new ArgumentException("必须使用盘符绝对路径或 UNC 共享路径，不支持设备路径和备用数据流");
            var names = path.Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(path.StartsWith("//", StringComparison.Ordinal) ? 2 : 1);
            foreach (var name in names)
            {
                if (name is "." or "..") continue;
                var stem = name.Split('.')[0].ToUpperInvariant();
                if (name.EndsWith(' ') || name.EndsWith('.') || stem is "CON" or "PRN" or "AUX" or "NUL"
                    || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3]))
                    throw new ArgumentException("路径包含 Windows 保留名称或末尾空格、句点");
            }
            var full = Path.GetFullPath(path).Replace('\\', '/');
            var root = Path.GetPathRoot(full)!.Replace('\\', '/').TrimEnd('/') + "/";
            return full.TrimEnd('/').Equals(root.TrimEnd('/'), Comparison) ? root : full.TrimEnd('/');
        }
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    public static bool IsRoot(string path) => path == "/" || OperatingSystem.IsWindows()
        && path.TrimEnd('/').Equals(Path.GetPathRoot(path)?.Replace('\\', '/').TrimEnd('/'), Comparison);

    public static string? Parent(string path)
    {
        if (path == "/") return null;
        if (IsRoot(path)) return "/";
        if (OperatingSystem.IsWindows()) return Normalize(Path.GetDirectoryName(path)!);
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    public static string Name(string path)
    {
        path = path.Replace('\\', '/');
        if (IsRoot(path)) return path;
        return path.TrimEnd('/').Split('/')[^1];
    }

    public static string Join(string directory, string name) => directory.TrimEnd('/') + "/" + name;

    public static bool IsWithin(string path, string root) => path.TrimEnd('/').Equals(root.TrimEnd('/'), Comparison)
        || path.StartsWith(root.TrimEnd('/') + "/", Comparison);

    public static bool HasReparsePoint(string path)
    {
        if (!OperatingSystem.IsWindows() || path == "/") return false;
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }
}
