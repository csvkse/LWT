namespace LinuxWebTool.WebHost.Extensions;

public static class HttpContextExtensions
{
    /// <summary>取客户端 IP（优先反代头 X-Real-IP）。</summary>
    public static string GetClientIp(this HttpContext context)
    {
        var forwarded = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwarded))
        {
            return forwarded;
        }
        return context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
    }
}
