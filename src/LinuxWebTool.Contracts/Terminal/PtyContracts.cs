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
    bool IsNativePty => false;
    bool HasExited { get; }
    int? ExitCode => null;
    bool? IsShellIdle => null;
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
    IReadOnlyList<TerminalSessionInfo> ListSessions();
    TerminalSessionInfo? GetSessionInfo(string sessionId);
    IPtyAttachment? Attach(string sessionId, long afterSequence = 0);
    Task WriteInputAsync(string sessionId, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default);
    bool UpdateSession(string sessionId, TerminalSessionUpdateRequest request);
    string? IssueAttachmentTicket(string sessionId);
    bool ConsumeAttachmentTicket(string sessionId, string ticket);
}

public sealed record TerminalSessionInfo(
    [property: JsonPropertyName("sessionId")] string SessionId,
    [property: JsonPropertyName("processId")] int ProcessId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("initialWorkingDirectory")] string? InitialWorkingDirectory,
    [property: JsonPropertyName("workingDirectory")] string? WorkingDirectory,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("lastInputAt")] DateTimeOffset? LastInputAt,
    [property: JsonPropertyName("detachedAt")] DateTimeOffset? DetachedAt,
    [property: JsonPropertyName("hasUserInput")] bool HasUserInput,
    [property: JsonPropertyName("keepAlive")] bool KeepAlive,
    [property: JsonPropertyName("exitCode")] int? ExitCode,
    [property: JsonPropertyName("bufferTruncated")] bool BufferTruncated,
    [property: JsonPropertyName("lastSequence")] long LastSequence,
    [property: JsonPropertyName("nativePty")] bool NativePty = false);
public sealed record TerminalSessionUpdateRequest(
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("keepAlive")] bool? KeepAlive = null);
public sealed record TerminalAttachmentTicket([property: JsonPropertyName("ticket")] string Ticket);
public sealed record TerminalOutputFrame(
    [property: JsonPropertyName("sequence")] long Sequence,
    [property: JsonPropertyName("data")] string Data,
    [property: JsonPropertyName("truncated")] bool Truncated = false);
public interface IPtyAttachment : IAsyncDisposable
{
    IAsyncEnumerable<TerminalOutputFrame> ReadAllAsync(CancellationToken cancellationToken = default);
    Task WriteInputAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default);
    Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default);
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
