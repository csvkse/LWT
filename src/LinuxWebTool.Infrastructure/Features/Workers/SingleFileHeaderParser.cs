using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Workers.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Workers;

/// <summary>
/// 单文件 Worker / Extension 头部元数据解析器（Self-Contained Header Frontmatter）。
/// 支持 JSDoc 格式与 # / // 行注释格式。预编译正则纳秒级解析，0 外部 IO 开销。
/// </summary>
public static partial class SingleFileHeaderParser
{
    [GeneratedRegex(@"@(?<key>[a-zA-Z0-9_\-]+)[:\s]+(?<val>[^\r\n]+)", RegexOptions.Compiled)]
    private static partial Regex AnnotationRegex();

    public sealed record ParsedMetadata
    {
        public required string Name { get; init; }
        public string Description { get; init; } = string.Empty;
        public string Runtime { get; init; } = "node";
        public WorkerServiceMode Mode { get; init; } = WorkerServiceMode.Daemon;
        public string Target { get; init; } = "container";
        public int? Port { get; init; }
        public string Version { get; init; } = "1.0.0";
        public string? Schedule { get; init; }
        public IReadOnlyDictionary<string, string> RawTags { get; init; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// 从单文件内容扫描解析元数据。仅扫描前 2048 字符，保障瞬时完成。
    /// </summary>
    public static ParsedMetadata Parse(string content, string fileName)
    {
        var scanLength = Math.Min(content.Length, 2048);
        var headerSection = content[..scanLength];

        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var matches = AnnotationRegex().Matches(headerSection);

        foreach (Match match in matches)
        {
            if (match.Success)
            {
                var key = match.Groups["key"].Value.Trim().ToLowerInvariant();
                var val = match.Groups["val"].Value.Trim();
                // 剔除可能存在的末尾注释闭合符号如 */
                if (val.EndsWith("*/", StringComparison.Ordinal))
                {
                    val = val[..^2].TrimEnd();
                }
                if (!string.IsNullOrEmpty(key) && !tags.ContainsKey(key))
                {
                    tags[key] = val;
                }
            }
        }

        // 推导 Runtime
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var defaultRuntime = ext switch
        {
            ".js" or ".mjs" or ".cjs" => "node",
            ".py" => "python",
            ".sh" => "bash",
            ".ps1" => "powershell",
            ".bat" or ".cmd" => "batch",
            _ => "node"
        };

        var runtime = tags.TryGetValue("runtime", out var r) ? r.ToLowerInvariant() : defaultRuntime;

        // 推导 Mode
        var mode = WorkerServiceMode.Daemon;
        if (tags.TryGetValue("mode", out var m))
        {
            mode = m.Equals("service", StringComparison.OrdinalIgnoreCase) || m.Equals("http", StringComparison.OrdinalIgnoreCase)
                ? WorkerServiceMode.Service
                : WorkerServiceMode.Daemon;
        }
        else if (tags.ContainsKey("port") || content.Contains(".listen(") || content.Contains("app.run("))
        {
            mode = WorkerServiceMode.Service;
        }

        // 端口解析
        int? port = null;
        if (tags.TryGetValue("port", out var pStr) && int.TryParse(pStr, out var pVal) && pVal is > 0 and < 65536)
        {
            port = pVal;
        }

        // 名称推导
        var name = tags.TryGetValue("name", out var n) ? n : (tags.TryGetValue("title", out var t) ? t : Path.GetFileNameWithoutExtension(fileName));
        var desc = tags.TryGetValue("description", out var d) ? d : (tags.TryGetValue("desc", out var d2) ? d2 : string.Empty);
        var target = tags.TryGetValue("target", out var tgt) ? tgt.ToLowerInvariant() : "container";
        var version = tags.TryGetValue("version", out var v) ? v : "1.0.0";
        var schedule = tags.TryGetValue("schedule", out var s) ? s : (tags.TryGetValue("cron", out var c) ? c : null);

        return new ParsedMetadata
        {
            Name = name,
            Description = desc,
            Runtime = runtime,
            Mode = mode,
            Target = target,
            Port = port,
            Version = version,
            Schedule = schedule,
            RawTags = tags
        };
    }
}
