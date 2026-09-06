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
            OnPrepareResponse = context =>
            {
                var path = context.Context.Request.Path;
                if (path.StartsWithSegments("/app") && path.Value?.EndsWith("index.html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
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
        app.MapFallbackToFile("app/index.html");
        return app;
    }
}
