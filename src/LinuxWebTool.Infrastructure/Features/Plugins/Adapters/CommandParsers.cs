using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// 命令执行器进度与结果解析器。
/// </summary>
public static partial class CommandParsers
{
    [GeneratedRegex(@"time=(\d{2}):(\d{2}):(\d{2}\.\d+)")]
    private static partial Regex FfmpegTimeRegex();

    [GeneratedRegex(@"frame=\s*(\d+)")]
    private static partial Regex FfmpegFrameRegex();

    /// <summary>
    /// 解析单行标准输出并提取进度信息。
    /// </summary>
    public static (bool HasProgress, double? Percent, string? Message) ParseProgressLine(string parserType, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return (false, null, null);

        var trimmed = line.Trim();

        switch (parserType.ToLowerInvariant())
        {
            case "json-lines":
            {
                if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(trimmed);
                        var root = doc.RootElement;
                        double? pct = null;
                        if (root.TryGetProperty("percent", out var pPct) && pPct.TryGetDouble(out var d1)) pct = d1;
                        else if (root.TryGetProperty("progress", out var pProg) && pProg.TryGetDouble(out var d2)) pct = d2;

                        string? msg = null;
                        if (root.TryGetProperty("message", out var pMsg)) msg = pMsg.GetString();

                        if (pct.HasValue || msg != null)
                        {
                            return (true, pct, msg);
                        }
                    }
                    catch
                    {
                        // 忽略非格式化行
                    }
                }
                return (false, null, null);
            }

            case "ffmpeg-key-value":
            {
                var timeMatch = FfmpegTimeRegex().Match(trimmed);
                var frameMatch = FfmpegFrameRegex().Match(trimmed);

                if (timeMatch.Success || frameMatch.Success)
                {
                    var msg = trimmed;
                    if (trimmed.Length > 120) msg = trimmed[..120] + "...";
                    return (true, null, msg);
                }
                return (false, null, null);
            }

            default:
                return (false, null, null);
        }
    }
}
