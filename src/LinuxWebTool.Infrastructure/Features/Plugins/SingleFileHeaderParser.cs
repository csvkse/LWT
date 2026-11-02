using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

/// <summary>
/// 单文件插件头部元数据解析器（Self-Contained Header Frontmatter）。
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
        public PluginRuntime Runtime { get; init; } = PluginRuntime.Node;
        public PluginProfile Profile { get; init; } = PluginProfile.Resident;
        public int? Port { get; init; }
        public string Version { get; init; } = "1.0.0";
        public IReadOnlyDictionary<string, string> RawTags { get; init; } = new Dictionary<string, string>();
    }

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

        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var defaultRuntime = ext switch
        {
            ".py" => PluginRuntime.Python,
            ".cs" or ".csproj" => PluginRuntime.DotNet,
            ".sh" => PluginRuntime.Bash,
            ".ps1" => PluginRuntime.PowerShell,
            ".go" => PluginRuntime.Go,
            _ => PluginRuntime.Node
        };

        var runtime = defaultRuntime;
        if (tags.TryGetValue("runtime", out var rStr))
        {
            runtime = rStr.ToLowerInvariant() switch
            {
                "python" or "py" => PluginRuntime.Python,
                "dotnet" or "csharp" or "cs" => PluginRuntime.DotNet,
                "bash" or "sh" or "shell" => PluginRuntime.Bash,
                "powershell" or "pwsh" or "ps1" => PluginRuntime.PowerShell,
                "native" or "binary" => PluginRuntime.Native,
                "go" or "golang" => PluginRuntime.Go,
                _ => PluginRuntime.Node
            };
        }

        var profile = PluginProfile.Resident;
        if (tags.TryGetValue("profile", out var pStr))
        {
            profile = pStr.ToLowerInvariant() switch
            {
                "oneshot" or "script" or "hook" => PluginProfile.OneShot,
                _ => PluginProfile.Resident
            };
        }

        int? port = null;
        if (tags.TryGetValue("port", out var portStr) && int.TryParse(portStr, out var parsedPort))
        {
            port = parsedPort;
        }

        tags.TryGetValue("name", out var parsedName);
        tags.TryGetValue("desc", out var parsedDesc);
        if (string.IsNullOrEmpty(parsedDesc))
        {
            tags.TryGetValue("description", out parsedDesc);
        }

        tags.TryGetValue("version", out var parsedVersion);

        return new ParsedMetadata
        {
            Name = string.IsNullOrWhiteSpace(parsedName) ? Path.GetFileNameWithoutExtension(fileName) : parsedName,
            Description = parsedDesc ?? string.Empty,
            Runtime = runtime,
            Profile = profile,
            Port = port,
            Version = string.IsNullOrWhiteSpace(parsedVersion) ? "1.0.0" : parsedVersion,
            RawTags = tags
        };
    }
}
