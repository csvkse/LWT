using LinuxWebTool.WebHost.MinimalApi;
using LinuxWebTool.WebHost.Features.RemoteHost.Endpoints;
using Microsoft.Extensions.FileProviders;
using System.Diagnostics;
using System.Reflection;

namespace LinuxWebTool.WebHost.Composition;

public static class PipelineExtensions
{
    /// <summary>中间件管线：追踪/度量 → 异常 → 静态前端 → 认证 → APIKey → 授权 → API / 终端 / MCP / 网关 → SPA 回退。</summary>
    public static WebApplication UseApplicationPipeline(this WebApplication app)
    {
        app.UseMiddleware<TraceContextMiddleware>();
        app.UseMiddleware<ExceptionHandlingMiddleware>();

        app.MapGet("/health", async (DbConnectionFactory factory) =>
        {
            var database = false;
            try
            {
                using var connection = factory.CreateConnection();
                await connection.OpenAsync();
                database = connection.State == System.Data.ConnectionState.Open;
            }
            catch
            {
                // 健康检查必须返回可消费的 503，而不是抛出未处理异常。
            }

            var response = new HealthResponse(
                database ? "healthy" : "unhealthy",
                Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
                Environment.Version.ToString(),
                OperatingSystem.IsLinux() ? "Linux" : Environment.OSVersion.Platform.ToString(),
                System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
                (long)Environment.TickCount64 / 1000,
                database);
            return database
                ? Results.Ok(response)
                : Results.Json(response, Composition.AppJsonSerializerContext.Default.HealthResponse, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).AllowAnonymous();

        app.MapGet("/api/Metrics", (IAppMetricsService metricsService) =>
        {
            var snapshot = metricsService.GetSnapshot();
            return Results.Json(snapshot, Composition.AppJsonSerializerContext.Default.AppMetricsSnapshot);
        }).AllowAnonymous();

        // 根路径跳转到前端入口
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/")
            {
                context.Response.Redirect("/app/");
                return;
            }
            await next();
        });

        var webRoot = Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        IFileProvider fileProvider = Directory.Exists(webRoot)
            ? new PhysicalFileProvider(webRoot)
            : new EmbeddedFileProvider(typeof(PipelineExtensions).Assembly, "LinuxWebTool.WebHost.wwwroot");

        // DefaultFiles、StaticFiles 和 SPA fallback 必须共享同一个 provider。
        app.Environment.WebRootFileProvider = fileProvider;
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            // 前端资源全部 no-cache：内网工具性能足够，保证版本更新后浏览器立即拿到新文件
            OnPrepareResponse = context =>
            {
                var path = context.Context.Request.Path;
                if (path.StartsWithSegments("/app"))
                {
                    context.Context.Response.Headers.CacheControl = "no-cache";
                }
            },
        });

        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseAuthentication();
        app.UseMiddleware<ApiKeyMiddleware>();
        app.UseAuthorization();
        app.UseMiddleware<JwtRenewalMiddleware>();
        app.UseAntiforgery();
        app.UseMiddleware<LinuxWebTool.WebHost.Features.Plugins.Ingress.PluginIngressMiddleware>();
        app.MapAutoControllers();
        app.MapTerminalEndpoints();
        app.MapRemoteTerminalEndpoints();
        app.MapMcpEndpoints();
        app.MapReverseProxy();

        // 统一插件平台 (Plugins) HTTP 网关反向代理转发 (/plugins/{pluginId}/*)
        app.Map("/plugins/{pluginId}/{**catch-all}", async (
            string pluginId,
            HttpContext context,
            LinuxWebTool.Contracts.Features.Plugins.Contracts.IPluginManager pluginManager,
            LinuxWebTool.WebHost.Features.Plugins.Auth.PluginAuthGatewayEvaluator authEvaluator,
            IHttpClientFactory httpClientFactory) =>
        {
            var ct = context.RequestAborted;
            var plugin = await pluginManager.GetByIdAsync(pluginId, ct);
            if (plugin == null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(
                    new MessageResponse($"未找到插件: '{pluginId}'"),
                    AppJsonSerializerContext.Default.MessageResponse);
                return;
            }

            // 1. 策略模式评估微服务鉴权决断
            var authDecision = await authEvaluator.EvaluateAsync(context, plugin.Manifest);
            if (!authDecision.IsAllowed)
            {
                context.Response.StatusCode = authDecision.StatusCode;
                context.Response.ContentType = "application/json; charset=utf-8";
                if (!string.IsNullOrEmpty(authDecision.ChallengeHeader))
                {
                    context.Response.Headers.WWWAuthenticate = authDecision.ChallengeHeader;
                }
                await context.Response.WriteAsJsonAsync(
                    new MessageResponse(authDecision.FailureReason ?? "微服务访问拒绝：未授权"),
                    AppJsonSerializerContext.Default.MessageResponse);
                return;
            }

            // 2. 状态机模式联动微服务生命周期
            if (plugin.State == LinuxWebTool.Contracts.Features.Plugins.Contracts.PluginState.Disabled)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(
                    new MessageResponse($"插件 '{pluginId}' 已被管理员显式禁用"),
                    AppJsonSerializerContext.Default.MessageResponse);
                return;
            }

            if (plugin.State == LinuxWebTool.Contracts.Features.Plugins.Contracts.PluginState.Starting)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers.RetryAfter = "3";
                await context.Response.WriteAsJsonAsync(
                    new MessageResponse($"插件 '{pluginId}' 正在启动初始化中，请稍候重试"),
                    AppJsonSerializerContext.Default.MessageResponse);
                return;
            }

            if (plugin.State == LinuxWebTool.Contracts.Features.Plugins.Contracts.PluginState.Backoff)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(
                    new MessageResponse($"插件 '{pluginId}' 异常崩溃触发熔断保护 (Backoff)，暂停服务"),
                    AppJsonSerializerContext.Default.MessageResponse);
                return;
            }

            if (!plugin.AllocatedPort.HasValue || plugin.AllocatedPort.Value <= 0)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(
                    new MessageResponse($"插件 '{pluginId}' 未处于运行状态或未声明 HTTP 端口"),
                    AppJsonSerializerContext.Default.MessageResponse);
                return;
            }

            var client = httpClientFactory.CreateClient();
            var port = plugin.AllocatedPort.Value;
            var subPath = context.Request.RouteValues["catch-all"]?.ToString() ?? "";
            var queryString = context.Request.QueryString.Value ?? "";
            var targetUri = $"http://127.0.0.1:{port}/{subPath}{queryString}";

            using var reqMsg = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

            foreach (var h in context.Request.Headers)
            {
                if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (h.Key.StartsWith("X-LWT-Internal-", StringComparison.OrdinalIgnoreCase)) continue; // 剥离外部伪造的内部头
                reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            }

            reqMsg.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1");
            reqMsg.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
            reqMsg.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", $"/plugins/{pluginId}");
            reqMsg.Headers.TryAddWithoutValidation("X-LWT-Plugin-Id", pluginId);
            reqMsg.Headers.TryAddWithoutValidation("X-LWT-Auth-User", authDecision.IdentityName ?? "anonymous");
            reqMsg.Headers.TryAddWithoutValidation("X-LWT-Auth-Type", authDecision.AuthType ?? "Anonymous");

            if (!string.IsNullOrEmpty(plugin.InternalSecret))
            {
                reqMsg.Headers.TryAddWithoutValidation("X-LWT-Internal-Secret", plugin.InternalSecret);
            }

            if (HttpMethods.IsPost(context.Request.Method) ||
                HttpMethods.IsPut(context.Request.Method) ||
                HttpMethods.IsPatch(context.Request.Method))
            {
                reqMsg.Content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType != null)
                {
                    reqMsg.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
                }
            }

            try
            {
                using var respMsg = await client.SendAsync(reqMsg, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
                context.Response.StatusCode = (int)respMsg.StatusCode;
                foreach (var h in respMsg.Headers)
                {
                    if (IsHopByHopHeader(h.Key)) continue;
                    context.Response.Headers[h.Key] = h.Value.ToArray();
                }
                foreach (var h in respMsg.Content.Headers)
                {
                    if (IsHopByHopHeader(h.Key)) continue;
                    context.Response.Headers[h.Key] = h.Value.ToArray();
                }
                await respMsg.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(
                    new MessageResponse($"插件后端服务访问失败: {ex.Message}"),
                    AppJsonSerializerContext.Default.MessageResponse);
            }
        });

        // 未匹配的 API 必须返回 JSON 404，不能被 SPA fallback 返回 index.html（否则前端会把 HTML 当业务数据）。
        // 以 /api/{*path} 作为较具体的 fallback endpoint：正常 Controller 路由优先，未知 API 才落到这里。
        app.MapFallback("/api/{*path}", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new MessageResponse("API endpoint not found"), AppJsonSerializerContext.Default.MessageResponse);
        });

        // 静默处理浏览器自动发起的 /favicon.ico 请求，避免无必要的控制台 404 错误
        app.MapGet("/favicon.ico", () => Results.NoContent());

        app.MapFallbackToFile("app/index.html");
        return app;
    }

    private static bool IsHopByHopHeader(string header) =>
        header.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Trailer", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
        header.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);
}
