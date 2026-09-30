using System.Text.Json.Serialization;

namespace LinuxWebTool.Contracts.Terminal;

/// <summary>PTY 会话启动参数。</summary>
public sealed record PtyStartOptions(
    string? Executable = null,
    IReadOnlyList<string>? Arguments = null,
    string? WorkingDirectory = null,
    int Columns = 80,
    int Rows = 24,
    IReadOnlyDictionary<string, string>? EnvironmentVariables = null);

/// <summary>PTY 会话抽象。</summary>
public interface IPtySession : IAsyncDisposable
{
    string SessionId { get; }
    int ProcessId { get; }
    bool HasExited { get; }
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
    Task TerminateAsync(CancellationToken cancellationToken = default);
}

/// <summary>PTY 引擎抽象。</summary>
public interface IPtyEngine
{
    bool IsSupported { get; }
    Task<IPtySession> StartSessionAsync(PtyStartOptions options, CancellationToken cancellationToken = default);
}

/// <summary>PTY 会话管理器抽象。</summary>
public interface IPtySessionManager
{
    Task<IPtySession> CreateSessionAsync(PtyStartOptions options, CancellationToken cancellationToken = default);
    IPtySession? GetSession(string sessionId);
    Task<bool> CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>终端会话创建请求。</summary>
public sealed record TerminalSessionCreateRequest(
    [property: JsonPropertyName("executable")] string? Executable = null,
    [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory = null,
    [property: JsonPropertyName("columns")] int Columns = 80,
    [property: JsonPropertyName("rows")] int Rows = 24);

/// <summary>终端会话创建响应。</summary>
public sealed record TerminalSessionCreateResponse(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory);
