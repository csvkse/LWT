using LinuxWebTool.WebHost.Middleware;
using LinuxWebTool.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace LinuxWebTool.WebHost.Composition;

public static class ServiceCollectionExtensions
{
    /// <summary>功能装配：数据库、仓储、Shell、日志、认证、调度、MVC 与 OpenAPI。</summary>
    public static WebApplicationBuilder AddApplicationServices(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var contentRoot = builder.Environment.ContentRootPath;

        // 持久化数据根目录（SQLite / admin.json / jwt 密钥 / logs 聚合于此，Data:Directory 可改）
        var dataPaths = new DataPaths(configuration, builder.Environment);
        builder.Services.AddSingleton(dataPaths);

        // 文件日志（程序日志 + 调试日志），相对目录锚定到数据目录
        builder.Logging.AddFileLogging(configuration, dataPaths);

        // SQLite（SqlSugar CodeFirst 建表）：未显式配置连接串时落在数据目录
        var rawConnectionString = configuration.GetConnectionString("SQLite");
        var connectionString = string.IsNullOrWhiteSpace(rawConnectionString)
            ? $"Data Source={dataPaths.PathFor("linuxweb.db")}"
            : DbSetup.ResolveConnectionString(rawConnectionString, contentRoot);
        var db = DbSetup.Create(connectionString);
        DbSetup.Initialize(db);
        builder.Services.AddSingleton(db);

        // 仓储与操作日志
        builder.Services.AddSingleton<CommandStore>();
        builder.Services.AddSingleton<GroupStore>();
        builder.Services.AddSingleton<ScheduleStore>();
        builder.Services.AddSingleton<ExecutionStore>();
        builder.Services.AddSingleton<OperationLogStore>();
        builder.Services.AddSingleton<IOperationLogger, OperationLogger>();

        // SMB 挂载管理
        builder.Services.AddSingleton<SmbMountStore>();
        builder.Services.AddSingleton<SmbMountService>();
        builder.Services.AddHostedService<SmbMountStartupService>();

        // FFmpeg 转码：预设 / 队列执行 / 监听自动转码
        TranscodePresetSeeder.Seed(db); // 内置预设播种（表为空时）
        builder.Services.AddSingleton<TranscodePresetStore>();
        builder.Services.AddSingleton<TranscodeJobStore>();
        builder.Services.AddSingleton<WatchRuleStore>();
        var transcodeOptions = configuration.GetSection(TranscodeOptions.SectionName).Get<TranscodeOptions>() ?? new TranscodeOptions();
        builder.Services.AddSingleton(transcodeOptions);
        builder.Services.AddSingleton<FfmpegLocator>();
        builder.Services.AddSingleton<TranscodeQueueService>(); // WatchFolderService 依赖具体类型，须注册（同时作为 HostedService 启动）
        builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscodeQueueService>());
        builder.Services.AddHostedService<WatchFolderService>();

        // Shell 执行器
        var shellOptions = configuration.GetSection(ShellOptions.SectionName).Get<ShellOptions>() ?? new ShellOptions();
        builder.Services.AddSingleton(shellOptions);
        builder.Services.AddSingleton<IShellExecutor, ShellExecutor>();

        // 系统状态采集与历史采样
        var systemStatusOptions = configuration.GetSection(SystemStatusOptions.SectionName).Get<SystemStatusOptions>() ?? new SystemStatusOptions();
        builder.Services.AddSingleton(systemStatusOptions);
        builder.Services.AddSingleton<ISystemStatusProvider, SystemStatusProvider>();
        builder.Services.AddSingleton<SystemStatusStore>();
        builder.Services.AddSingleton<SystemStatusDiskStore>();
        builder.Services.AddSingleton<SystemStatusNetStore>();
        builder.Services.AddSingleton<SystemStatusProcessStore>();
        builder.Services.AddHostedService<SystemStatusSampleService>();

        // 认证：单管理员 + JWT（凭据在启动阶段即初始化，见 StartupInitializerService）
        var jwtIssuer = new JwtIssuer(configuration, dataPaths);
        builder.Services.AddSingleton(jwtIssuer);
        builder.Services.AddSingleton<AdminCredentialService>();
        builder.Services.AddHostedService<StartupInitializerService>();
        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = jwtIssuer.BuildValidationParameters();
            });
        builder.Services.AddAuthorization();

        // Quartz 定时调度
        builder.Services.AddScheduling();

        // MVC + OpenAPI（AOT 兼容，替代 Swashbuckle）
        builder.Services.AddControllers();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, context, ct) =>
            {
                document.Info.Title = "LinuxWebTool API";
                document.Info.Version = "v1";
                return Task.CompletedTask;
            });
            // JWT Bearer 安全方案
            options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
        });

        return builder;
    }
}

/// <summary>为 OpenAPI 文档注入 JWT Bearer 安全方案。</summary>
internal sealed class BearerSecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes ??= new Dictionary<string, OpenApiSecurityScheme>();
        document.Components.SecuritySchemes["Bearer"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            Description = "粘贴 /api/Auth/Login 返回的 Token",
        };
        return Task.CompletedTask;
    }
}
