namespace LinuxWebTool.Infrastructure.Logging;

/// <summary>文件日志配置（appsettings 的 FileLog 节）。</summary>
public sealed class FileLoggerOptions
{
    public const string SectionName = "FileLog";

    /// <summary>日志目录（相对路径基于应用工作目录）。</summary>
    public string Directory { get; set; } = "logs";

    public string AppFilePrefix { get; set; } = "app";

    public string DebugFilePrefix { get; set; } = "debug";
}
