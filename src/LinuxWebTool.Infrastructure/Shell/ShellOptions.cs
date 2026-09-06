namespace LinuxWebTool.Infrastructure.Shell;

/// <summary>Shell 执行配置（appsettings 的 Shell 节）。</summary>
public sealed class ShellOptions
{
    public const string SectionName = "Shell";

    /// <summary>指令未配置超时时使用的默认超时（秒）。</summary>
    public int DefaultTimeoutSeconds { get; set; } = 60;

    /// <summary>单次执行允许的最大超时（秒），防止长期占用执行槽位。</summary>
    public int MaxTimeoutSeconds { get; set; } = 86400;

    /// <summary>stdout / stderr 各自的落库与返回上限（字节），超出截断。</summary>
    public int MaxOutputBytes { get; set; } = 64 * 1024;

    /// <summary>最大并发执行数，超出排队等待。</summary>
    public int MaxConcurrent { get; set; } = 4;

    /// <summary>默认工作目录；为空时继承服务进程当前目录。</summary>
    public string WorkingDirectory { get; set; } = string.Empty;
}
