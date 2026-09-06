using System.Text.RegularExpressions;

namespace LinuxWebTool.Infrastructure.Logging;

/// <summary>程序 / 调试日志文件的查询服务（供 LogsController 查看）。</summary>
public partial class LogFileService
{
    private readonly FileLoggerOptions _options;

    public LogFileService(FileLoggerOptions options)
    {
        _options = options;
    }

    public sealed record LogFileInfo(string Name, long LengthBytes, DateTime LastWriteTime);

    public List<LogFileInfo> List()
    {
        var directory = _options.Directory;
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory, "*.txt")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .Select(f => new LogFileInfo(f.Name, f.Length, f.LastWriteTime))
            .ToList();
    }

    /// <summary>读取指定日志文件末尾 N 行；文件名必须为合法日志文件名，防目录穿越。</summary>
    public string? ReadTail(string fileName, int lines)
    {
        if (!FileNameRegex().IsMatch(fileName))
        {
            return null;
        }

        var path = Path.Combine(_options.Directory, fileName);
        if (!File.Exists(path))
        {
            return null;
        }

        lines = Math.Clamp(lines, 1, 5000);
        var tail = new Queue<string>(lines);
        foreach (var line in File.ReadLines(path))
        {
            if (tail.Count == lines)
            {
                tail.Dequeue();
            }
            tail.Enqueue(line);
        }

        var text = string.Join(Environment.NewLine, tail);
        if (text.Length > 256 * 1024)
        {
            text = text[^ (256 * 1024)..] + "\n…[内容已截断]…";
        }
        return text;
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_]+-\d{8}\.txt$")]
    private static partial Regex FileNameRegex();
}
