using LinuxWebTool.WebHost.Middleware;
using LinuxWebTool.Infrastructure.Support;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

namespace LinuxWebTool.WebHost.Composition;

public static class ServiceCollectionExtensions
{
    /// <summary>功能装配：数据库、仓储、Shell、日志、认证、调度、MVC 与 Swagger。</summary>
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

        // Shell 执行器
        var shellOptions = configuration.GetSection(ShellOptions.SectionName).Get<ShellOptions>() ?? new ShellOptions();
        builder.Services.AddSingleton(shellOptions);
        builder.Services.AddSingleton<IShellExecutor, ShellExecutor>();

        // 系统状态采集与历史采样
        builder.Services.AddSingleton<ISystemStatusProvider, SystemStatusProvider>();
        builder.Services.AddSingleton<SystemStatusStore>();
        builder.Services.AddHostedService<SystemStatusSampleService>();

        // 认证：单管理员 + JWT
        var jwtIssuer = new JwtIssuer(configuration, dataPaths);
        builder.Services.AddSingleton(jwtIssuer);
        builder.Services.AddSingleton<AdminCredentialService>();
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

        // MVC + Swagger
        builder.Services.AddControllers();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo { Title = "LinuxWebTool API", Version = "v1" });
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "粘贴 /api/Auth/Login 返回的 Token",
            });
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = [],
            });
        });

        return builder;
    }
}
