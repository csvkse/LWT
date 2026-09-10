using System.Collections.Concurrent;
using LinuxWebTool.WebHost.Extensions;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>认证门禁：单管理员登录签发 JWT。含按 IP 的失败锁定（10 次失败锁 5 分钟）。</summary>
[ApiController]
[Route("api/[controller]")]
public class AuthController(
    AdminCredentialService adminCredential,
    JwtIssuer jwtIssuer,
    IOperationLogger operationLogger) : ControllerBase
{
    private const int MaxFailures = 10;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, (int Count, DateTime LockUntil)> Failures = new();

    [AllowAnonymous]
    [HttpPost("Login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var ip = HttpContext.GetClientIp();
        var userName = request.UserName?.Trim() ?? string.Empty;

        if (Failures.TryGetValue(ip, out var state) && state.LockUntil > DateTime.Now)
        {
            var waitMinutes = Math.Ceiling((state.LockUntil - DateTime.Now).TotalMinutes);
            return StatusCode(StatusCodes.Status429TooManyRequests,
                new MessageResponse($"失败次数过多，请约 {waitMinutes} 分钟后再试"));
        }

        if (userName.Length == 0 || !adminCredential.Validate(userName, request.Password ?? string.Empty))
        {
            RecordFailure(ip);
            await operationLogger.LogAsync("登录", "认证", userName, "用户名或密码错误", success: false, clientIp: ip);
            return Unauthorized(new MessageResponse("用户名或密码错误"));
        }

        Failures.TryRemove(ip, out _);
        var (token, expiresAt) = jwtIssuer.Issue(userName);
        await operationLogger.LogAsync("登录", "认证", userName, "登录成功", clientIp: ip);
        return Ok(new LoginResult(token, expiresAt, userName));
    }

    [HttpGet("Check")]
    [Authorize]
    public IActionResult Check()
    {
        return Ok(new UserInfoResponse(User.Identity?.Name ?? string.Empty));
    }

    /// <summary>
    /// 修改管理员用户名 / 密码（需验证当前密码）。改用户名后旧 Token 中的身份失效，前端应引导重新登录。
    /// </summary>
    [HttpPost("ChangeCredential")]
    [Authorize]
    public async Task<IActionResult> ChangeCredential([FromBody] ChangeCredentialRequest request)
    {
        var newUserName = request.NewUserName?.Trim();
        var newPassword = request.NewPassword;
        if (string.IsNullOrWhiteSpace(newUserName) && string.IsNullOrWhiteSpace(newPassword))
        {
            return BadRequest(new MessageResponse("新用户名与新密码至少填写一项"));
        }
        if (newPassword is { Length: < 6 })
        {
            return BadRequest(new MessageResponse("新密码至少 6 位"));
        }
        if (newUserName is { Length: > 100 })
        {
            return BadRequest(new MessageResponse("用户名不能超过 100 个字符"));
        }
        if (!adminCredential.Validate(User.Identity?.Name ?? string.Empty, request.CurrentPassword ?? string.Empty))
        {
            await operationLogger.LogAsync("修改凭据", "认证", User.Identity?.Name ?? string.Empty, "当前密码验证失败", success: false, clientIp: HttpContext.GetClientIp());
            return BadRequest(new MessageResponse("当前密码错误"));
        }

        adminCredential.UpdateCredential(newUserName, newPassword);
        await operationLogger.LogAsync("修改凭据", "认证", adminCredential.Account.UserName,
            (newUserName is { Length: > 0 } ? "修改用户名 " : string.Empty) + (newPassword is { Length: > 0 } ? "修改密码" : string.Empty),
            clientIp: HttpContext.GetClientIp());
        return Ok(new ChangeCredentialResponse("凭据已更新，请使用新凭据重新登录", true));
    }

    private static void RecordFailure(string ip)
    {
        Failures.AddOrUpdate(
            ip,
            _ => (1, DateTime.MinValue),
            (_, s) => s.Count + 1 >= MaxFailures ? (0, DateTime.Now.Add(LockDuration)) : (s.Count + 1, s.LockUntil));
    }
}
