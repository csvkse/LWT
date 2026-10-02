namespace LinuxWebTool.Infrastructure.Terminal;

public sealed record TerminalCapabilities(
    [property: System.Text.Json.Serialization.JsonPropertyName("platform")] string Platform,
    [property: System.Text.Json.Serialization.JsonPropertyName("mode")] string Mode,
    [property: System.Text.Json.Serialization.JsonPropertyName("nativePty")] bool NativePty,
    [property: System.Text.Json.Serialization.JsonPropertyName("container")] bool Container,
    [property: System.Text.Json.Serialization.JsonPropertyName("message")] string Message);

public sealed class TerminalDependencyService
{
    public TerminalCapabilities Detect()
    {
        var linux = OperatingSystem.IsLinux();
        var container = linux && (File.Exists("/.dockerenv") || File.Exists("/run/.containerenv")
            || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true");
        var native = linux ? LinuxNativePty.IsSupported : OperatingSystem.IsWindows() && WindowsConPtyFactory.IsSupported;
        var message = native ? "支持原生 PTY 和浏览器断线续接，无需安装额外后台终端工具。"
            : "当前使用管道回退，窗口缩放和全屏交互能力受限。后台会话仍可在浏览器断线后保留。";
        message += " 软件退出或容器停止会结束当前会话。";
        return new(linux ? "Linux" : OperatingSystem.IsWindows() ? "Windows" : "Unsupported", "Application", native, container, message);
    }
}
