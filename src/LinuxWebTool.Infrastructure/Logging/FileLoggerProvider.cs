using System.Text;

namespace LinuxWebTool.Infrastructure.Logging;

/// <summary>
/// 轻量按天滚动文件日志：
/// - Information 及以上写入 app-yyyyMMdd.txt（程序日志）；
/// - LinuxWebTool.* 命名空间的 Debug 级别写入 debug-yyyyMMdd.txt（调试日志，由 Logging:LogFile:LinuxWebTool=Debug 开启）。
/// </summary>
[ProviderAlias("LogFile")]
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLoggerOptions _options;
    private readonly object _sync = new();

    public FileLoggerProvider(FileLoggerOptions options)
    {
        _options = options;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        var now = DateTime.Now;
        var line = FormatLine(now, categoryName, level, message, exception);

        lock (_sync)
        {
            Directory.CreateDirectory(_options.Directory);
            if (level >= LogLevel.Information)
            {
                File.AppendAllText(AppFilePath(now), line);
            }
            if (categoryName.StartsWith("LinuxWebTool", StringComparison.Ordinal) && level <= LogLevel.Debug)
            {
                File.AppendAllText(DebugFilePath(now), line);
            }
        }
    }

    internal string AppFilePath(DateTime now) =>
        Path.Combine(_options.Directory, $"{_options.AppFilePrefix}-{now:yyyyMMdd}.txt");

    internal string DebugFilePath(DateTime now) =>
        Path.Combine(_options.Directory, $"{_options.DebugFilePrefix}-{now:yyyyMMdd}.txt");

    private static string FormatLine(DateTime now, string category, LogLevel level, string message, Exception? exception)
    {
        var builder = new StringBuilder();
        builder.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(ToLevelCode(level)).Append("] ")
            .Append(category)
            .Append(" - ")
            .AppendLine(message);
        if (exception is not null)
        {
            builder.AppendLine(exception.ToString());
        }
        return builder.ToString();
    }

    private static string ToLevelCode(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRCE",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        _ => "CRIT",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
