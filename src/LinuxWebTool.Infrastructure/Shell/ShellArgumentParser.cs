using System.Text;

namespace LinuxWebTool.Infrastructure.Shell;

/// <summary>
/// 位置参数拆分器（POSIX 风格简化实现）：
/// 空格/Tab 分隔；单引号内全字面；双引号内 \" \\ \$ \` 反斜杠转义；引号可嵌在词中（a"b c"d → ab cd）。
/// 拆分结果经 ProcessStartInfo.ArgumentList 原样传给 bash（$1 $2...），不经二次 shell 解释。
/// </summary>
public static class ShellArgumentParser
{
    public static List<string> Split(string? line)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(line))
        {
            return args;
        }

        var current = new StringBuilder();
        var hasToken = false;
        char? quote = null;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote == '\'')
            {
                if (ch == '\'')
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }
                continue;
            }
            if (quote == '"')
            {
                if (ch == '"' )
                {
                    quote = null;
                }
                else if (ch == '\\' && i + 1 < line.Length && line[i + 1] is '"' or '\\' or '$' or '`')
                {
                    current.Append(line[++i]);
                }
                else
                {
                    current.Append(ch);
                }
                continue;
            }

            switch (ch)
            {
                case ' ' or '\t':
                    if (hasToken)
                    {
                        args.Add(current.ToString());
                        current.Clear();
                        hasToken = false;
                    }
                    break;
                case '\'' or '"':
                    quote = ch;
                    hasToken = true;
                    break;
                case '\\' when i + 1 < line.Length:
                    current.Append(line[++i]);
                    hasToken = true;
                    break;
                default:
                    current.Append(ch);
                    hasToken = true;
                    break;
            }
        }

        // 未闭合引号容错：剩余内容作为最后一个参数。
        if (hasToken || current.Length > 0)
        {
            args.Add(current.ToString());
        }
        return args;
    }
}
