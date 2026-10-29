using System.Text.RegularExpressions;

namespace LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;

/// <summary>
/// 终端 ANSI / OSC 控制序列净化器，消除终端语义标记（如 OSC 133）与转义乱码
/// </summary>
public static partial class AnsiOutputSanitizer
{
    // 匹配标准闭合的 OSC 序列 (Operating System Command: ESC ] ... BEL 或 ESC ] ... ESC \)
    // 内部只允许非控制字符 [^\x07\x1b]，绝不跨 ESC/BEL 贪婪匹配正文
    [GeneratedRegex(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)")]
    private static partial Regex OscPattern();

    // 匹配 CSI 序列 (Control Sequence Introducer: ESC [ ... 命令字符)
    [GeneratedRegex(@"\x1b\[[0-?]*[ -/]*[@-~]")]
    private static partial Regex CsiPattern();

    // 匹配由于首字节 ESC 被剥离或缺失终止符残留的裸露 ]133;[A-Z] 语义标记 (单字符指令 A/B/C/D，防止贪婪匹配正文词汇)
    [GeneratedRegex(@"(?:\x1b)?\]133;[A-Za-z](?:;[0-9]+)?(?:\x07|\x1b\\)?")]
    private static partial Regex NakedOsc133Pattern();

    // 匹配不可见 C0 控制字符（保留 \r, \n, \t）
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex ControlCharsPattern();

    /// <summary>
    /// 净化终端输出文本
    /// </summary>
    public static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        // 1. 去除完整 OSC 序列
        var text = OscPattern().Replace(input, string.Empty);

        // 2. 去除 ANSI CSI 颜色与光标控制码
        text = CsiPattern().Replace(text, string.Empty);

        // 3. 去除残留裸露的 ]133;C 等语义标记
        text = NakedOsc133Pattern().Replace(text, string.Empty);

        // 4. 清理残余非法控制字符（保留换行与制表符）
        text = ControlCharsPattern().Replace(text, string.Empty);

        return text.Trim();
    }
}
