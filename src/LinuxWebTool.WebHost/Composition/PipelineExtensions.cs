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
        app.MapAutoControllers();
        app.MapTerminalEndpoints();
        app.MapRemoteTerminalEndpoints();
        app.MapMcpEndpoints();
        app.MapReverseProxy();

        // SaaS 函数微服务 (Workers) 动态路径反向代理转发
        app.Map("/w/{workerId}/{**catch-all}", async (
            string workerId,
            HttpContext context,
            LinuxWebTool.Contracts.Features.Workers.Contracts.IWorkersManager workersManager,
            IHttpClientFactory httpClientFactory) =>
        {
            var worker = await workersManager.GetByIdAsync(workerId, context.RequestAborted);
            if (worker != null && !worker.IsEnabled)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync($"{{\"code\":503,\"message\":\"Service Disabled: Worker '{workerId}' is explicitly disabled.\"}}");
                return;
            }
            if (worker == null || !worker.BoundPort.HasValue || worker.State != LinuxWebTool.Contracts.Features.Workers.Contracts.WorkerState.Running)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync($"{{\"code\":503,\"message\":\"Worker '{workerId}' is not running or has no listening port.\"}}");
                return;
            }

            var client = httpClientFactory.CreateClient();
            var port = worker.BoundPort.Value;
            var subPath = context.Request.RouteValues["catch-all"]?.ToString() ?? "";
            var queryString = context.Request.QueryString.Value ?? "";
            var targetUri = $"http://127.0.0.1:{port}/{subPath}{queryString}";

            using var reqMsg = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

            foreach (var h in context.Request.Headers)
            {
                if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value.ToArray());
            }

            reqMsg.Headers.TryAddWithoutValidation("X-Forwarded-For", context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1");
            reqMsg.Headers.TryAddWithoutValidation("X-Forwarded-Host", context.Request.Host.Value);
            reqMsg.Headers.TryAddWithoutValidation("X-Forwarded-Prefix", $"/w/{workerId}");
            reqMsg.Headers.TryAddWithoutValidation("X-LWT-Worker-Id", workerId);

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
                    context.Response.Headers[h.Key] = h.Value.ToArray();
                }
                foreach (var h in respMsg.Content.Headers)
                {
                    context.Response.Headers[h.Key] = h.Value.ToArray();
                }
                await respMsg.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsync($"{{\"code\":502,\"message\":\"Failed to reach worker backend: {ex.Message}\"}}");
            }
        }).AllowAnonymous();

        // 未匹配的 API 必须返回 JSON 404，不能被 SPA fallback 返回 index.html（否则前端会把 HTML 当业务数据）。
        // 以 /api/{*path} 作为较具体的 fallback endpoint：正常 Controller 路由优先，未知 API 才落到这里。
        app.MapFallback("/api/{*path}", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new MessageResponse("API endpoint not found"), AppJsonSerializerContext.Default.MessageResponse);
        });

        app.MapFallbackToFile("app/index.html");
        return app;
    }
}
