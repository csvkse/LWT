namespace LinuxWebTool.WebHost.Shared.Middleware;

/// <summary>
/// JWT 滑动窗口自动无感续期中间件。
/// 当已认证请求的 Token 剩余寿命小于配置阈值时，自动签发新 Token 并在响应头挂载 X-Renewed-Token。
/// </summary>
public sealed class JwtRenewalMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        JwtIssuer jwtIssuer,
        AdminCredentialService adminCredential)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var rawToken = authHeader["Bearer ".Length..].Trim();
                if (jwtIssuer.ShouldRenew(rawToken, out var userName, out var stamp)
                    && string.Equals(userName, adminCredential.Account.UserName, StringComparison.Ordinal)
                    && adminCredential.ValidateSecurityStamp(stamp))
                {
                    var (newToken, newExpiresAt) = jwtIssuer.Issue(userName, adminCredential.Account.SecurityStamp);
                    context.Response.OnStarting(() =>
                    {
                        context.Response.Headers["X-Renewed-Token"] = newToken;
                        context.Response.Headers.Append("Access-Control-Expose-Headers", "X-Renewed-Token");
                        context.Response.Headers["X-Token-Expires"] = newExpiresAt.ToString("o");
                        return Task.CompletedTask;
                    });
                }
            }
        }

        await next(context);
    }
}
