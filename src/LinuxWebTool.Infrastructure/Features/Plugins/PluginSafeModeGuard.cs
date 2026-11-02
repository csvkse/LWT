using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

/// <summary>
/// 插件纯净安全模式 (Safe Mode) 与开机自启动控制门禁。
/// 支持通过 Docker 等环境变量（LWT_SAFE_MODE / LWT_PLUGINS_SAFE_MODE 等）无侵入旁路插件自启与崩溃恢复，保障宿主灾难恢复。
/// </summary>
public class PluginSafeModeGuard
{
    /// <summary>
    /// 是否激活了纯净安全排查模式 (Safe Mode)。
    /// 激活时：100% 阻断所有插件开机自启，并暂停后台 Supervisor 的自动拉起，留出安全排查窗口。
    /// </summary>
    public static bool IsSafeModeEnabled()
    {
        var raw = Environment.GetEnvironmentVariable("LWT_SAFE_MODE")
               ?? Environment.GetEnvironmentVariable("LWT_PLUGINS_SAFE_MODE")
               ?? Environment.GetEnvironmentVariable("SAFE_MODE");
        return ParseBool(raw);
    }

    /// <summary>
    /// 是否被环境变量显式禁用了开机自启动。
    /// </summary>
    public static bool IsAutoStartDisabled()
    {
        if (IsSafeModeEnabled()) return true;
        var raw = Environment.GetEnvironmentVariable("LWT_PLUGINS_DISABLE_AUTO_START")
               ?? Environment.GetEnvironmentVariable("DISABLE_AUTO_START");
        return ParseBool(raw);
    }

    /// <summary>
    /// 获取当前插件治理模式状态 DTO（静态方法）。
    /// </summary>
    public static PluginSystemModeDto GetSystemMode()
    {
        var isSafeMode = IsSafeModeEnabled();
        var isAutoStartDisabled = IsAutoStartDisabled();
        var mode = isSafeMode ? "SafeMode" : (isAutoStartDisabled ? "NoAutoStart" : "Normal");
        var message = isSafeMode
            ? "系统当前运行于安全排查模式 (Safe Mode)。所有插件开机自启与后台自动恢复已暂停，便于故障排查与定位。"
            : (isAutoStartDisabled ? "系统当前已禁用插件开机自启 (No AutoStart)。" : "系统运行正常。");

        return new PluginSystemModeDto
        {
            IsSafeMode = isSafeMode,
            IsAutoStartDisabled = isAutoStartDisabled,
            Mode = mode,
            Message = message
        };
    }

    public static bool ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "enabled" => true,
            _ => false
        };
    }
}
