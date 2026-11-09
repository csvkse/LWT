using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using LinuxWebTool.WebHost.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace LinuxWebTool.WebHost.Features.Terminal.Routes;

/// <summary>终端会话管理控制器。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TerminalController(IPtySessionManager pty, TerminalDependencyService dependencies, IDockerEngineAdapter? dockerAdapter = null) : MinimalApi.ControllerBase
{
    [HttpGet("Support")]
    public IResult Support() => Results.Json(dependencies.Detect(), AppJsonSerializerContext.Default.TerminalCapabilities);

    [HttpGet("Sessions")]
    public IResult ListSessions() => Results.Json(pty.ListSessions().ToArray(), AppJsonSerializerContext.Default.TerminalSessionInfoArray);

    [HttpGet("Sessions/{sessionId}")]
    public IResult SessionDetails(string sessionId) => pty.GetSessionInfo(sessionId) is { } session
        ? Results.Json(session, AppJsonSerializerContext.Default.TerminalSessionInfo)
        : NotFound(new MessageResponse("会话不存在或服务已重启"));

    [HttpPatch("Sessions/{sessionId}")]
    public IResult UpdateSession(string sessionId, [FromBody] TerminalSessionUpdateRequest request)
    {
        if (request.Name is not null && (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80 || request.Name.Any(char.IsControl)))
            return BadRequest(new MessageResponse("名称必须为 1 至 80 个可显示字符"));
        return pty.UpdateSession(sessionId, request) ? Results.NoContent() : NotFound(new MessageResponse("会话不存在"));
    }

    [HttpPost("Sessions/{sessionId}/Attachment")]
    public IResult AttachmentTicket(string sessionId) => pty.IssueAttachmentTicket(sessionId) is { } ticket
        ? Results.Json(new TerminalAttachmentTicket(ticket), AppJsonSerializerContext.Default.TerminalAttachmentTicket)
        : NotFound(new MessageResponse("会话不存在或附着请求过多"));

    [HttpPost("Sessions")]
    public async Task<IResult> CreateSession([FromBody] TerminalSessionCreateRequest request)
    {
        var targetStr = request.Target?.Trim() ?? "container";
        var isHost = string.Equals(targetStr, "host", StringComparison.OrdinalIgnoreCase);
        var isDocker = targetStr.StartsWith("docker:", StringComparison.OrdinalIgnoreCase);

        var resolvedTarget = targetStr;
        var cwd = isHost ? "/root" : (isDocker ? "/" : Directory.GetCurrentDirectory());

        if (isDocker && dockerAdapter != null)
        {
            var rawId = targetStr["docker:".Length..].Trim();
            var inspect = await dockerAdapter.InspectContainerAsync(rawId, HttpContext.RequestAborted);
            if (inspect == null)
            {
                return NotFound(new MessageResponse($"容器 [{rawId}] 不存在"));
            }
            if (!inspect.Running || inspect.Pid <= 0)
            {
                return BadRequest(new MessageResponse($"容器 [{inspect.Name}] 未处于运行状态，无法打开交互终端"));
            }
            resolvedTarget = $"docker:{inspect.Id}:{inspect.Pid}";
            cwd = !string.IsNullOrWhiteSpace(request.WorkingDirectory) ? request.WorkingDirectory.Trim() :
                  (!string.IsNullOrWhiteSpace(inspect.WorkingDir) ? inspect.WorkingDir : "/");
        }
        else if (request.WorkingDirectory is not null)
        {
            var requested = request.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(requested) || requested.Length > 4096 || requested.Any(char.IsControl)
                || !Path.IsPathFullyQualified(requested))
                return BadRequest(new MessageResponse("工作目录必须是有效的绝对路径"));
            if (isHost)
            {
                cwd = requested.Trim();
            }
            else
            {
                try
                {
                    cwd = Path.GetFullPath(requested);
                    if (!Directory.Exists(cwd))
                        return BadRequest(new MessageResponse("工作目录不存在或不是目录"));
                    using var entries = Directory.EnumerateFileSystemEntries(cwd).GetEnumerator();
                    entries.MoveNext();
                }
                catch (UnauthorizedAccessException)
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new MessageResponse("无权限访问工作目录"));
                }
                catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
                {
                    return BadRequest(new MessageResponse("无法访问工作目录"));
                }
            }
        }

        var apiKey = HttpContext.Items["CurrentApiKey"] as ApiKeyEntity;
        if (!string.IsNullOrWhiteSpace(apiKey?.AllowedPaths) && !isDocker)
        {
            if (request.WorkingDirectory is null)
            {
                var roots = PathScopeGuard.GetAllowedRoots(apiKey.AllowedPaths);
                if (roots.Count > 0)
                {
                    cwd = roots[0];
                }
            }
            else
            {
                if (!PathScopeGuard.IsAllowed(cwd, apiKey.AllowedPaths, out var failureReason))
                {
                    return StatusCode(StatusCodes.Status403Forbidden, new MessageResponse(failureReason ?? "工作目录超出 API Key 授权范围"));
                }
            }
        }

        var options = new PtyStartOptions(
            Executable: request.Executable,
            WorkingDirectory: cwd,
            Columns: request.Columns > 0 ? request.Columns : 80,
            Rows: request.Rows > 0 ? request.Rows : 24,
            Target: resolvedTarget);

        try
        {
            var session = await pty.CreateSessionAsync(options, HttpContext.RequestAborted).ConfigureAwait(false);
            return Results.Json(new TerminalSessionCreateResponse(session.SessionId, session.ProcessId, cwd), AppJsonSerializerContext.Default.TerminalSessionCreateResponse);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, new MessageResponse(ex.Message));
        }
    }

    [HttpDelete("Sessions/{sessionId}")]
    public async Task<IResult> CloseSession(string sessionId)
    {
        var closed = await pty.CloseSessionAsync(sessionId, HttpContext.RequestAborted).ConfigureAwait(false);
        return closed ? Results.NoContent() : NotFound(new MessageResponse("会话不存在"));
    }
}
