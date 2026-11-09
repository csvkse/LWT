using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

/// <summary>
/// 插件环境变量清洗与凭据防火墙 (Credential Cleansing & Sensitive Env Filtering)
/// - 阻断父进程敏感系统凭据（ADMIN_PASSWORD, JWT, SSH, API Key 等）隐式泄漏；
/// - 提供基础宿主环境白名单继承（PATH, LANG, TMPDIR, SystemRoot 等）；
/// - 保护宿主主进程安全性，杜绝子进程凭据嗅探。
/// </summary>
public static partial class PluginEnvironmentSanitizer
{
    private static readonly HashSet<string> WindowsSafeEnvWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE",
        "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "PROGRAMFILES",
        "PROGRAMFILES(X86)", "COMMONPROGRAMFILES", "COMMONPROGRAMFILES(X86)",
        "COMSPEC", "PATHEXT", "DOTNET_ROOT"
    };

    private static readonly HashSet<string> LinuxSafeEnvWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "USER", "HOME", "LOGNAME", "SHELL", "LANG", "LC_ALL", "LC_CTYPE",
        "TMPDIR", "TERM", "LD_LIBRARY_PATH", "DOTNET_ROOT", "HOSTNAME"
    };

    [GeneratedRegex(@"(^|_)(PASSWORD|SECRET|TOKEN|KEY|JWT|CREDENTIAL|PRIVATE|AUTH)($|_)", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveKeyPattern();

    /// <summary>
    /// 判断某个环境变量名是否具有高危凭据特征。
    /// </summary>
    public static bool IsSensitiveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;

        // 特例：LWT 插件自身的标准元数据环境变量受控放行
        if (key.Equals("LWT_INTERNAL_SECRET", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("LWT_PLUGIN_ID", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return SensitiveKeyPattern().IsMatch(key);
    }

    /// <summary>
    /// 将清洗后的安全环境应用至指定的 ProcessStartInfo 中（清空隐式继承，注入白名单与受控变量）。
    /// </summary>
    public static void ApplySafeEnvironment(
        ProcessStartInfo psi,
        IReadOnlyDictionary<string, string>? customEnv,
        IReadOnlySet<string>? explicitAllowedKeys = null)
    {
        psi.Environment.Clear();

        // 1. 继承操作系统核心白名单基础变量
        var safeWhitelist = OperatingSystem.IsWindows() ? WindowsSafeEnvWhitelist : LinuxSafeEnvWhitelist;
        foreach (var safeKey in safeWhitelist)
        {
            var val = Environment.GetEnvironmentVariable(safeKey);
            if (!string.IsNullOrEmpty(val))
            {
                psi.Environment[safeKey] = val;
            }
        }

        // 2. 注入受控的自定义环境变量（跳过未明确授权的敏感高危变量）
        if (customEnv != null)
        {
            foreach (var (k, v) in customEnv)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;

                // 若带有敏感关键词且未显式在插件配置白名单中，强制过滤阻断
                if (IsSensitiveKey(k) && (explicitAllowedKeys == null || !explicitAllowedKeys.Contains(k)))
                {
                    continue;
                }

                psi.Environment[k] = v;
            }
        }
    }
}
