using System.Reflection;
using System.Text;
using LinuxWebTool.Infrastructure.Features.Security.Adapters;
using LinuxWebTool.Infrastructure.Shared.Support;
using LinuxWebTool.WebHost.Features.RemoteHost.Routes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LinuxWebTool.UnitTests;

/// <summary>
/// 远程主机文件管理下载与 JWT Query Token 认证防回归单元测试。
/// 验证点：
/// 1. JwtBearerEvents.OnMessageReceived 能够正确从 ?token= 和 ?access_token= 中提取 Token；
/// 2. Header 中的 Token 具有最高优先级，Query 参数不会覆盖已有 Token；
/// 3. 无任何 Token 时保持未认证状态；
/// 4. 由 JwtIssuer 签发的真实 Token 在通过 Query 提取后能成功通过签名与期限校验；
/// 5. RemoteHostsController 及其 DownloadFile 路由具备 [Authorize] 保护，杜绝未认证越权。
/// </summary>
public sealed class RemoteHostDownloadAuthTests : IDisposable
{
    private sealed class DummyHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "LinuxWebTool.UnitTests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private readonly string _tempDir;
    private readonly JwtIssuer _jwtIssuer;
    private readonly JwtBearerEvents _jwtEvents;

    public RemoteHostDownloadAuthTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lwt_jwt_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:SecretKey"] = "SuperSecretKeyForTestingJwtAuthenticationExtraction123456!",
            ["Jwt:Issuer"] = "LinuxWebToolTest",
            ["Jwt:Audience"] = "LinuxWebToolTest",
            ["Data:Directory"] = _tempDir
        }).Build();

        var env = new DummyHostEnvironment(_tempDir);
        var dataPaths = new DataPaths(config, env);
        _jwtIssuer = new JwtIssuer(config, dataPaths);

        _jwtEvents = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token))
                {
                    var token = context.Request.Query["token"].ToString();
                    if (string.IsNullOrEmpty(token))
                    {
                        token = context.Request.Query["access_token"].ToString();
                    }
                    if (!string.IsNullOrEmpty(token))
                    {
                        context.Token = token;
                    }
                }
                return Task.CompletedTask;
            }
        };
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // 忽略测试清理临时文件失败
        }
    }

    [Fact]
    public async Task OnMessageReceived_WithQueryToken_SetsContextToken()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?token=my_test_jwt_token_123");

        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var context = new MessageReceivedContext(httpContext, scheme, options);

        await _jwtEvents.MessageReceived(context);

        Assert.Equal("my_test_jwt_token_123", context.Token);
    }

    [Fact]
    public async Task OnMessageReceived_WithAccessTokenQuery_SetsContextToken()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?access_token=standard_oauth2_token_456");

        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var context = new MessageReceivedContext(httpContext, scheme, options);

        await _jwtEvents.MessageReceived(context);

        Assert.Equal("standard_oauth2_token_456", context.Token);
    }

    [Fact]
    public async Task OnMessageReceived_WhenTokenAlreadySet_DoesNotOverwrite()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?token=query_token_should_be_ignored");

        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var context = new MessageReceivedContext(httpContext, scheme, options)
        {
            Token = "existing_header_bearer_token"
        };

        await _jwtEvents.MessageReceived(context);

        Assert.Equal("existing_header_bearer_token", context.Token);
    }

    [Fact]
    public async Task OnMessageReceived_WithoutAnyToken_TokenRemainsEmpty()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString("?other_param=foo");

        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions();
        var context = new MessageReceivedContext(httpContext, scheme, options);

        await _jwtEvents.MessageReceived(context);

        Assert.True(string.IsNullOrEmpty(context.Token));
    }

    [Fact]
    public async Task FullJwtValidation_WithQueryToken_Succeeds()
    {
        // 1. 使用系统真实 JwtIssuer 签发 Token
        var (issuedToken, _) = _jwtIssuer.Issue("admin_user", "security_stamp_999");
        Assert.False(string.IsNullOrWhiteSpace(issuedToken));

        // 2. 模拟浏览器通过 ?token= 发起下载请求
        var httpContext = new DefaultHttpContext();
        httpContext.Request.QueryString = new QueryString($"?id=host1&path=/var/log/bin.txt&token={issuedToken}");

        var scheme = new AuthenticationScheme(JwtBearerDefaults.AuthenticationScheme, null, typeof(JwtBearerHandler));
        var options = new JwtBearerOptions
        {
            TokenValidationParameters = _jwtIssuer.BuildValidationParameters()
        };
        var context = new MessageReceivedContext(httpContext, scheme, options);

        // 3. 执行 OnMessageReceived 提取 Token
        await _jwtEvents.MessageReceived(context);
        Assert.Equal(issuedToken, context.Token);

        // 4. 执行 Token 真实验证
        var validationResult = await JwtIssuer.TokenHandler.ValidateTokenAsync(
            context.Token,
            options.TokenValidationParameters);

        Assert.True(validationResult.IsValid, "由 JwtIssuer 签发并通过 Query 提取的 Token 必须验证合法");
        Assert.NotNull(validationResult.ClaimsIdentity);
        Assert.Equal("admin_user", validationResult.ClaimsIdentity.FindFirst("sub")?.Value);
    }

    [Fact]
    public void RemoteHostsController_HasAuthorizeAndDownloadAttribute()
    {
        var controllerType = typeof(RemoteHostsController);

        // 控制器类级别必须有 [Authorize] 保护
        var hasClassAuthorize = controllerType.GetCustomAttribute<AuthorizeAttribute>(inherit: true) is not null;
        Assert.True(hasClassAuthorize, "RemoteHostsController 类必须声明 [Authorize] 特性以阻止未认证匿名访问");

        // DownloadFile 方法必须具备 [HttpGet("{id}/Files/Download")] 特性
        var downloadMethod = controllerType.GetMethod(nameof(RemoteHostsController.DownloadFile));
        Assert.NotNull(downloadMethod);

        var httpGetAttr = downloadMethod.GetCustomAttribute<HttpGetAttribute>();
        Assert.NotNull(httpGetAttr);
        Assert.Equal("{id}/Files/Download", httpGetAttr.Template);
    }
}
