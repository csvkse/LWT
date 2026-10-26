using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Contracts.Features.Terminal.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 终端与 Shell 命令行 MCP 工具提供者
/// </summary>
public sealed class TerminalMcpToolProvider(
    IShellExecutor shellExecutor,
    IPtySessionManager ptySessionManager,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly JsonElement EmptyObjectSchema = Schema("""{"type":"object","properties":{}}""");

    private static readonly JsonElement TerminalExecuteSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "command": { "type": "string", "description": "待执行的命令或脚本" },
            "working_directory": { "type": "string", "description": "执行工作目录（可选绝对路径）" },
            "timeout_seconds": { "type": "integer", "description": "执行超时秒数，默认 60，上限 300" }
        },
        "required": ["command"]
    }
    """);

    private static readonly JsonElement TerminalCreateSessionSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "executable": { "type": "string", "description": "终端程序，如 bash 或 sh" },
            "working_directory": { "type": "string", "description": "初始工作目录" }
        }
    }
    """);

    private static readonly JsonElement TerminalCloseSessionSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "session_id": { "type": "string", "description": "会话 ID" }
        },
        "required": ["session_id"]
    }
    """);

    public string ModuleName => "terminal";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowTerminal;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition(
                "terminal_execute_command",
                "执行 Linux Shell 命令行或 Bash 脚本，获取 stdout/stderr 及耗时",
                TerminalExecuteSchema),

            new McpToolDefinition(
                "terminal_list_sessions",
                "列出当前活跃的交互式 PTY 虚拟终端会话",
                EmptyObjectSchema),

            new McpToolDefinition(
                "terminal_create_session",
                "新建一个后台运行的 PTY 虚拟终端会话",
                TerminalCreateSessionSchema),

            new McpToolDefinition(
                "terminal_close_session",
                "关闭并回收指定的 PTY 终端会话",
                TerminalCloseSessionSchema)
        ];
    }

    public async ValueTask<McpToolExecutionResult?> TryExecuteAsync(
        string toolName,
        JsonElement? arguments,
        McpRequestContext context)
    {
        var args = arguments ?? default;

        switch (toolName)
        {
            case "terminal_execute_command":
            {
                var command = args.TryGetProperty("command", out var cP) ? cP.GetString() ?? string.Empty : string.Empty;
                var cwd = args.TryGetProperty("working_directory", out var cwdP) ? cwdP.GetString() : null;
                var timeout = args.TryGetProperty("timeout_seconds", out var tP) ? tP.GetInt32() : 60;
                timeout = Math.Clamp(timeout, 1, 300);

                if (!string.IsNullOrWhiteSpace(context.CallerKey?.AllowedPaths))
                {
                    if (!string.IsNullOrWhiteSpace(cwd))
                    {
                        if (!PathScopeGuard.IsAllowed(cwd, context.CallerKey.AllowedPaths, out var cwdErr))
                            return McpToolExecutionResult.Error($"Access Denied: {cwdErr}");
                    }
                    else
                    {
                        cwd = PathScopeGuard.GetAllowedRoots(context.CallerKey.AllowedPaths).FirstOrDefault();
                    }
                }

                var shellReq = new ShellRequest
                {
                    CommandText = command,
                    WorkingDirectory = cwd,
                    TimeoutSeconds = timeout
                };
                var shellRes = await shellExecutor.ExecuteAsync(shellReq, context.CancellationToken);
                await operationLogger.LogAsync("MCP执行指令", "终端", command[..Math.Min(command.Length, 60)], $"ExitCode={shellRes.ExitCode}", clientIp: context.ClientIp);

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    if (shellRes.ExitCode.HasValue) writer.WriteNumber("exit_code", shellRes.ExitCode.Value);
                    else writer.WriteNull("exit_code");
                    writer.WriteString("stdout", shellRes.StandardOutput);
                    writer.WriteString("stderr", shellRes.ErrorOutput);
                    writer.WriteNumber("duration_ms", shellRes.DurationMs);
                    writer.WriteBoolean("timed_out", shellRes.TimedOut);
                    writer.WriteBoolean("truncated", shellRes.Truncated);
                    writer.WriteEndObject();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "terminal_list_sessions":
            {
                var sessions = ptySessionManager.ListSessions();
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartArray();
                    foreach (var s in sessions)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("session_id", s.SessionId);
                        writer.WriteNumber("process_id", s.ProcessId);
                        writer.WriteString("working_dir", s.WorkingDirectory);
                        writer.WriteBoolean("has_exited", s.ExitCode.HasValue);
                        writer.WriteString("created_at", s.CreatedAt.ToString("O"));
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "terminal_create_session":
            {
                var exe = args.TryGetProperty("executable", out var eP) ? eP.GetString() : null;
                var cwd = args.TryGetProperty("working_directory", out var cwdP) ? cwdP.GetString() : null;
                if (!string.IsNullOrWhiteSpace(context.CallerKey?.AllowedPaths))
                {
                    if (!string.IsNullOrWhiteSpace(cwd))
                    {
                        if (!PathScopeGuard.IsAllowed(cwd, context.CallerKey.AllowedPaths, out var cwdErr))
                            return McpToolExecutionResult.Error($"Access Denied: {cwdErr}");
                    }
                    else
                    {
                        cwd = PathScopeGuard.GetAllowedRoots(context.CallerKey.AllowedPaths).FirstOrDefault();
                    }
                }
                var options = new PtyStartOptions(Executable: exe, WorkingDirectory: cwd);
                var session = await ptySessionManager.CreateSessionAsync(options, context.CancellationToken);
                await operationLogger.LogAsync("MCP新建终端", "终端", session.SessionId, cwd ?? "default", clientIp: context.ClientIp);
                return McpToolExecutionResult.Success($"{{\"session_id\":\"{session.SessionId}\", \"process_id\":{session.ProcessId}, \"working_dir\":\"{cwd}\"}}");
            }

            case "terminal_close_session":
            {
                if (!args.TryGetProperty("session_id", out var sIdProp))
                    return McpToolExecutionResult.Error("Missing session_id parameter");

                var sessionId = sIdProp.GetString()!;
                var closed = await ptySessionManager.CloseSessionAsync(sessionId, context.CancellationToken);
                return McpToolExecutionResult.Success($"{{\"closed\":{closed.ToString().ToLower()}}}");
            }

            default:
                return null;
        }
    }
}
