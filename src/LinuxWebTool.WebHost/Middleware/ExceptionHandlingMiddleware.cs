namespace LinuxWebTool.WebHost.Middleware;

/// <summary>全局异常兜底：记录程序日志并返回统一 JSON 错误体。</summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端主动中断（如取消长命令执行），无需处理。
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "未处理异常：{Method} {Path}", context.Request.Method, context.Request.Path);
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json; charset=utf-8";
                await context.Response.WriteAsJsonAsync(new { message = "服务器内部错误：" + ex.Message });
            }
        }
    }
}
