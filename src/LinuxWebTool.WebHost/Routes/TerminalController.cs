using LinuxWebTool.Contracts.Terminal;
using LinuxWebTool.Infrastructure.Terminal;
using LinuxWebTool.WebHost.Composition;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>终端会话管理控制器。</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TerminalController(IPtySessionManager pty, TerminalDependencyService dependencies) : MinimalApi.ControllerBase
{
    [HttpGet("Support")]
    public IResult Support() => Results.Json(dependencies.Detect(), AppJsonSerializerContext.Default.TerminalCapabilities);

    [HttpGet("Dependencies/Installation")]
    public IResult Installation() => Results.Json(dependencies.Status(), AppJsonSerializerContext.Default.TerminalInstallStatus);

    [HttpPost("Dependencies/Install")]
    public IResult Install([FromBody] TerminalDependencyInstallRequest request)
    {
        if (!request.Confirm) return BadRequest(new MessageResponse("请确认安装系统依赖"));
        if (!dependencies.StartInstall()) return StatusCode(StatusCodes.Status409Conflict, new MessageResponse("当前环境无法安装、依赖已存在或安装正在进行"));
        return Results.Json(dependencies.Status(), AppJsonSerializerContext.Default.TerminalInstallStatus, statusCode: StatusCodes.Status202Accepted);
    }
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
        var cwd = Directory.GetCurrentDirectory();
        if (request.WorkingDirectory is not null)
        {
            var requested = request.WorkingDirectory;
            if (string.IsNullOrWhiteSpace(requested) || requested.Length > 4096 || requested.Any(char.IsControl)
                || !Path.IsPathFullyQualified(requested))
                return BadRequest(new MessageResponse("工作目录必须是有效的绝对路径"));
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

        var options = new PtyStartOptions(
            Executable: request.Executable,
            WorkingDirectory: cwd,
            Columns: request.Columns > 0 ? request.Columns : 80,
            Rows: request.Rows > 0 ? request.Rows : 24);

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

public sealed record TerminalDependencyInstallRequest([property: System.Text.Json.Serialization.JsonPropertyName("confirm")] bool Confirm);
