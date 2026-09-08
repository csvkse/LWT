using LinuxWebTool.Infrastructure.Support;
using LinuxWebTool.WebHost.Middleware;

namespace LinuxWebTool.WebHost.Composition;

public static class PipelineExtensions
{
    /// <summary>中间件管线：异常 → 静态前端 → Swagger → 认证 → API → SPA 回退。</summary>
    public static WebApplication UseApplicationPipeline(this WebApplication app)
    {
        app.UseMiddleware<ExceptionHandlingMiddleware>();

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

        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
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

        var swaggerEnabled = app.Configuration.GetValue("Swagger:Enabled", true);
        if (swaggerEnabled)
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        // 未匹配的 API 必须返回 JSON 404，不能被 SPA fallback 返回 index.html（否则前端会把 HTML 当业务数据）。
        // 以 /api/{*path} 作为较具体的 fallback endpoint：正常 Controller 路由优先，未知 API 才落到这里。
        app.MapFallback("/api/{*path}", async context =>
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "application/json; charset=utf-8";
            await context.Response.WriteAsJsonAsync(new { message = "API endpoint not found" });
        });

        app.MapFallbackToFile("app/index.html");
        return app;
    }
}
