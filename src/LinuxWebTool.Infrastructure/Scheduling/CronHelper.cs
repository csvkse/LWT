using System.Globalization;

namespace LinuxWebTool.Infrastructure.Scheduling;

/// <summary>
/// Cron 表达式归一化：同时接受 Unix 5 段（分 时 日 月 周）与 Quartz 6/7 段格式，
/// 统一转换为 Quartz 格式后再交给调度器校验。
/// </summary>
public static class CronHelper
{
    private static readonly Dictionary<string, int> WeekdayNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SUN"] = 0, ["MON"] = 1, ["TUE"] = 2, ["WED"] = 3, ["THU"] = 4, ["FRI"] = 5, ["SAT"] = 6,
    };

    public static bool TryNormalize(string? expression, out string normalized, out string error)
    {
        normalized = expression?.Trim() ?? string.Empty;
        error = string.Empty;

        if (normalized.Length == 0)
        {
            error = "Cron 表达式不能为空";
            return false;
        }

        var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        switch (parts.Length)
        {
            case 5:
            {
                // Unix：分 时 日 月 周 → Quartz：秒 分 时 日 月 周
                var dayOfMonth = parts[2];
                var dayOfWeek = parts[4];

                // Quartz 要求“日”与“周”互斥：至少一方为 ?。
                if (IsWildcard(dayOfWeek))
                {
                    dayOfWeek = "?";
                }
                else if (IsWildcard(dayOfMonth))
                {
                    dayOfMonth = "?";
                }
                else
                {
                    // 两者都指定（Unix 语义为“或”）：Quartz 不支持，按“日”处理。
                    dayOfWeek = "?";
                }

                normalized = $"0 {parts[0]} {parts[1]} {dayOfMonth} {parts[3]} {ConvertDow(dayOfWeek)}";
                return true;
            }
            case 6 or 7:
                return true; // 已是 Quartz 格式
            default:
                error = "Cron 需为 5 段（Unix：分 时 日 月 周）或 6/7 段（Quartz）格式";
                return false;
        }
    }

    private static bool IsWildcard(string field) => field is "*" or "?";

    /// <summary>Unix 星期 0=SUN..6=SAT → Quartz 1=SUN..7=SAT；支持名称、列表、区间、步进。</summary>
    private static string ConvertDow(string field)
    {
        if (IsWildcard(field))
        {
            return "?";
        }

        return string.Join(",", field
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(ConvertDowToken));
    }

    private static string ConvertDowToken(string token)
    {
        var stepIndex = token.IndexOf('/');
        if (stepIndex >= 0)
        {
            var basePart = token[..stepIndex];
            var convertedBase = IsWildcard(basePart) ? "*" : ConvertDowToken(basePart);
            return $"{convertedBase}/{token[(stepIndex + 1)..]}";
        }

        var rangeIndex = token.IndexOf('-');
        if (rangeIndex > 0)
        {
            var from = ResolveDowNumber(token[..rangeIndex]);
            var to = ResolveDowNumber(token[(rangeIndex + 1)..]);
            if (from.HasValue && to.HasValue)
            {
                return $"{from.Value + 1}-{to.Value + 1}";
            }
        }

        var single = ResolveDowNumber(token);
        return single.HasValue ? (single.Value + 1).ToString(CultureInfo.InvariantCulture) : token;
    }

    private static int? ResolveDowNumber(string token)
    {
        if (int.TryParse(token, CultureInfo.InvariantCulture, out var number))
        {
            return Math.Clamp(number, 0, 7) % 7;
        }
        return WeekdayNames.TryGetValue(token.Trim(), out var weekday) ? weekday : null;
    }
}
