using LinuxWebTool.Infrastructure.Support;

namespace LinuxWebTool.Infrastructure.Logging;

public static class LoggingExtensions
{
    /// <summary>注册按天滚动文件日志（控制台日志保持默认）。相对目录锚定到数据目录（默认 &lt;data&gt;/logs）。</summary>
    public static ILoggingBuilder AddFileLogging(this ILoggingBuilder builder, IConfiguration configuration, DataPaths dataPaths)
    {
        var options = configuration.GetSection(FileLoggerOptions.SectionName).Get<FileLoggerOptions>() ?? new FileLoggerOptions();
        options.Directory = dataPaths.Resolve(options.Directory);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<LogFileService>();
        builder.AddProvider(new FileLoggerProvider(options));
        return builder;
    }
}
