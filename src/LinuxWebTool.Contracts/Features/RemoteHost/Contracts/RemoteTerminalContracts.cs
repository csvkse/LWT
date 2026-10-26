using System.Text.Json.Serialization;

namespace LinuxWebTool.Contracts.Features.RemoteHost.Contracts;

/// <summary>创建远程终端会话请求契约</summary>
public sealed record RemoteTerminalSessionRequest(
    [property: JsonPropertyName("columns")] int Columns = 80,
    [property: JsonPropertyName("rows")] int Rows = 24);

/// <summary>创建远程终端会话响应契约</summary>
public sealed record RemoteTerminalSessionResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("hostId")] string HostId,
    [property: JsonPropertyName("hostName")] string HostName);

/// <summary>调整远程终端大小请求契约</summary>
public sealed record RemoteTerminalResizeRequest(
    [property: JsonPropertyName("columns")] int Columns,
    [property: JsonPropertyName("rows")] int Rows);

/// <summary>远程单次命令执行请求契约</summary>
public sealed record RemoteCommandExecRequest(
    [property: JsonPropertyName("command")] string Command,
    [property: JsonPropertyName("timeoutSeconds")] int TimeoutSeconds = 60);

/// <summary>远程单次命令执行响应契约</summary>
public sealed record RemoteCommandExecResponse(
    [property: JsonPropertyName("exitCode")] int ExitCode,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("errorOutput")] string ErrorOutput,
    [property: JsonPropertyName("durationMs")] long DurationMs);

/// <summary>远程终端会话抽象契约</summary>
public interface IRemoteTerminalSession : IAsyncDisposable
{
    string SessionId { get; }
    string HostId { get; }
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
    Task TerminateAsync(CancellationToken cancellationToken = default);
}
