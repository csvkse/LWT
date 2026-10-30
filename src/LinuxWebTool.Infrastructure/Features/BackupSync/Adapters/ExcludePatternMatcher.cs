namespace LinuxWebTool.Infrastructure.Features.BackupSync.Adapters;

/// <summary>
/// 高性能文件与目录排除模式匹配器与提前剪枝枚举器（无锁、零反射、Native AOT 友好）。
/// 支持目录级提前剪枝（Directory Pruning），避免对被排除目录的数十万子项执行无谓 I/O 遍历。
/// </summary>
public static class ExcludePatternMatcher
{
    /// <summary>
    /// 标准化排除模式列表：统一正斜杠、剥离首尾斜杠与空白项
    /// </summary>
    public static List<string> NormalizePatterns(IEnumerable<string>? patterns)
    {
        if (patterns is null) return [];
        var result = new List<string>();
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            var normalized = p.Trim().Replace('\\', '/').Trim('/');
            if (!string.IsNullOrEmpty(normalized) && !result.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(normalized);
            }
        }
        return result;
    }

    /// <summary>
    /// 判断指定目录是否应被排除（支持目录名精准匹配、路径前缀包含与通配符匹配）
    /// </summary>
    public static bool IsDirectoryExcluded(string dirName, string relativePath, IReadOnlyList<string> excludes)
    {
        if (excludes.Count == 0) return false;
        var normRel = relativePath.Replace('\\', '/').Trim('/');

        foreach (var p in excludes)
        {
            // 1. 目录名直接匹配（如 node_modules, .git, bin, obj, .cache）
            if (string.Equals(dirName, p, StringComparison.OrdinalIgnoreCase))
                return true;

            // 2. 相对路径完全匹配或前缀匹配（如 profiles/node_modules）
            if (string.Equals(normRel, p, StringComparison.OrdinalIgnoreCase))
                return true;

            if (normRel.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase) ||
                normRel.EndsWith("/" + p, StringComparison.OrdinalIgnoreCase) ||
                normRel.Contains("/" + p + "/", StringComparison.OrdinalIgnoreCase))
                return true;

            // 3. 通配符前缀或后缀匹配（如 .Trash*, *.cache）
            if (p.StartsWith("*.") && (dirName.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase) || normRel.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase)))
                return true;

            if (p.EndsWith('*') && (dirName.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) || normRel.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 判断指定文件是否应被排除（支持文件扩展名通配、精确文件名、相对路径、路径通配及父目录继承）
    /// </summary>
    public static bool IsFileExcluded(string fileName, string relativePath, IReadOnlyList<string> excludes)
    {
        if (excludes.Count == 0) return false;
        var normRel = relativePath.Replace('\\', '/').Trim('/');

        foreach (var p in excludes)
        {
            // 1. 包含路径前缀与通配符（如 logs/*.log, src/temp/*）
            if (p.Contains('/'))
            {
                var slashIdx = p.LastIndexOf('/');
                var dirPart = p[..slashIdx];
                var filePart = p[(slashIdx + 1)..];

                if (normRel.StartsWith(dirPart + "/", StringComparison.OrdinalIgnoreCase))
                {
                    if (filePart == "*" || filePart == "*.*")
                        return true;
                    if (filePart.StartsWith("*.") && fileName.EndsWith(filePart[1..], StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (filePart.EndsWith('*') && fileName.StartsWith(filePart[..^1], StringComparison.OrdinalIgnoreCase))
                        return true;
                    if (string.Equals(fileName, filePart, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            // 2. 后缀通配符（如 *.log, *.tmp, *.pyc）
            if (p.StartsWith("*."))
            {
                var ext = p[1..];
                if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) || normRel.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 3. 文件名直接匹配（如 Thumbs.db, .DS_Store, server.log）
            if (string.Equals(fileName, p, StringComparison.OrdinalIgnoreCase))
                return true;

            // 4. 相对路径完全匹配或父目录命中
            if (string.Equals(normRel, p, StringComparison.OrdinalIgnoreCase))
                return true;

            if (normRel.Contains("/" + p + "/", StringComparison.OrdinalIgnoreCase) ||
                normRel.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
                return true;

            // 5. 前缀通配符（如 temp*, npm-debug.log*）
            if (p.EndsWith('*') && (fileName.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase) || normRel.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 具备目录级提前剪枝（Directory Pruning）特性的文件枚举器。
    /// 一旦命中排除目录，整棵子树立即停止递归入队，从物理上节约数十万次系统调用开销。
    /// </summary>
    public static List<FileInfo> EnumerateFilesWithPruning(
        DirectoryInfo rootDir,
        string rootPath,
        IReadOnlyList<string> normalizedExcludes,
        CancellationToken ct = default)
    {
        var result = new List<FileInfo>();
        var queue = new Queue<DirectoryInfo>();
        queue.Enqueue(rootDir);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = queue.Dequeue();

            // 1. 枚举子目录并执行剪枝：命中排除规则者直接不入队
            try
            {
                foreach (var subDir in current.EnumerateDirectories())
                {
                    ct.ThrowIfCancellationRequested();
                    var relPath = Path.GetRelativePath(rootPath, subDir.FullName).Replace('\\', '/').Trim('/');
                    if (!IsDirectoryExcluded(subDir.Name, relPath, normalizedExcludes))
                    {
                        queue.Enqueue(subDir);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }

            // 2. 枚举当前目录下的文件并过滤
            try
            {
                foreach (var file in current.EnumerateFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    var relPath = Path.GetRelativePath(rootPath, file.FullName).Replace('\\', '/').Trim('/');
                    if (!IsFileExcluded(file.Name, relPath, normalizedExcludes))
                    {
                        result.Add(file);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { }
        }

        return result;
    }
}
