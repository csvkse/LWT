namespace LinuxWebTool.Infrastructure.Features.Security.Policies;

/// <summary>
/// AOT 友好的高性能零分配 Shell 命令行分词状态机（抗引号转义、抗连接符多语句注入）
/// </summary>
public static class ShellCommandLexer
{
    /// <summary>
    /// 检测是否包含非法子 Shell 展开与反引号注入（`...` 或 $(...)）
    /// 单引号包裹字面量与反斜杠转义字符除外
    /// </summary>
    public static bool HasSubshellInjection(ReadOnlySpan<char> span)
    {
        bool inSingleQuote = false;
        bool isEscaped = false;

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (isEscaped)
            {
                isEscaped = false;
                continue;
            }

            if (c == '\\')
            {
                isEscaped = true;
                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = !inSingleQuote;
                continue;
            }

            if (!inSingleQuote)
            {
                if (c == '`') return true;
                if (c == '$' && i + 1 < span.Length && span[i + 1] == '(') return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 词法状态机：将复合命令行拆解为独立指令段，抗 ;, &&, ||, | 拼接逃逸
    /// </summary>
    public static List<string> SplitCommandSegments(string rawCommandLine)
    {
        var segments = new List<string>();
        if (string.IsNullOrWhiteSpace(rawCommandLine)) return segments;

        var span = rawCommandLine.AsSpan();
        int start = 0;
        bool inSingleQuote = false;
        bool inDoubleQuote = false;

        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\'' && !inDoubleQuote)
            {
                inSingleQuote = !inSingleQuote;
            }
            else if (c == '"' && !inSingleQuote)
            {
                inDoubleQuote = !inDoubleQuote;
            }
            else if (!inSingleQuote && !inDoubleQuote)
            {
                if (c is ';' or '\n' or '|' || (c == '&' && i + 1 < span.Length && span[i + 1] == '&'))
                {
                    var seg = span[start..i].Trim();
                    if (!seg.IsEmpty)
                    {
                        segments.Add(seg.ToString());
                    }
                    if (c == '&' || (c == '|' && i + 1 < span.Length && span[i + 1] == '|'))
                    {
                        i++;
                    }
                    start = i + 1;
                }
            }
        }

        var last = span[start..].Trim();
        if (!last.IsEmpty)
        {
            segments.Add(last.ToString());
        }

        return segments;
    }

    /// <summary>
    /// 提取单一指令段的二进制基名与一级子命令（如 "/usr/bin/docker  run" -> binary="docker", sub="run"）
    /// </summary>
    public static (string Binary, string SubCommand) ExtractBinaryAndSubCommand(string commandSegment)
    {
        var parts = commandSegment.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return (string.Empty, string.Empty);

        int binIndex = 0;
        if (parts.Length > 1 && parts[0] is "sudo" or "doas" or "nohup")
        {
            binIndex = 1;
            while (binIndex < parts.Length && parts[binIndex].StartsWith('-'))
            {
                binIndex++;
            }
            if (binIndex >= parts.Length) binIndex = 0;
        }

        var rawBin = parts[binIndex];
        var lastSlash = rawBin.LastIndexOfAny(['/', '\\']);
        var binary = lastSlash >= 0 ? rawBin[(lastSlash + 1)..] : rawBin;

        var subCommand = string.Empty;
        for (int i = binIndex + 1; i < parts.Length; i++)
        {
            if (!parts[i].StartsWith('-'))
            {
                subCommand = parts[i];
                break;
            }
        }

        return (binary.ToLowerInvariant(), subCommand.ToLowerInvariant());
    }
}
