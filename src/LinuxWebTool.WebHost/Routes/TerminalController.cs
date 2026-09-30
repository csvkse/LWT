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
public class TerminalController(IPtySessionManager pty) : MinimalApi.ControllerBase
{
    [HttpPost("Sessions")]
    public async Task<IResult> CreateSession([FromBody] TerminalSessionCreateRequest request)
    {
        var cwd = !string.IsNullOrWhiteSpace(request.WorkingDirectory) && Directory.Exists(request.WorkingDirectory)
            ? request.WorkingDirectory
            : Directory.GetCurrentDirectory();

        var options = new PtyStartOptions(
            Executable: request.Executable,
            WorkingDirectory: cwd,
            Columns: request.Columns > 0 ? request.Columns : 80,
            Rows: request.Rows > 0 ? request.Rows : 24);

        var session = await pty.CreateSessionAsync(options, HttpContext.RequestAborted).ConfigureAwait(false);
        return Results.Json(new TerminalSessionCreateResponse(session.SessionId, session.ProcessId, cwd), AppJsonSerializerContext.Default.TerminalSessionCreateResponse);
    }

    [HttpDelete("Sessions/{sessionId}")]
    public async Task<IResult> CloseSession(string sessionId)
    {
        var closed = await pty.CloseSessionAsync(sessionId, HttpContext.RequestAborted).ConfigureAwait(false);
        return closed ? Results.NoContent() : NotFound(new MessageResponse("会话不存在"));
    }
}
