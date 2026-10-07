using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
namespace LinuxWebTool.Infrastructure.Features.Terminal.Platform;

public sealed class PtySessionManager : IPtySessionManager, IAsyncDisposable
{
    private readonly IPtyEngine engine;
    private readonly ConcurrentDictionary<string, BackgroundPtySession> activeSessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string SessionId, DateTimeOffset Expires)> tickets = new();
    private readonly SemaphoreSlim creationGate = new(1, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task cleanupTask;
    private readonly int maxSessions;
    private readonly int bufferBytes;
    private readonly TimeSpan unusedGrace;
    private readonly TimeSpan exitRetention;
    private int disposed;

    public PtySessionManager(IPtyEngine engine, IConfiguration? configuration = null)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        maxSessions = ReadSetting(configuration, "MaxSessions", 16, 1, 128);
        bufferBytes = ReadSetting(configuration, "BufferBytes", 1048576, 4096, 8388608);
        unusedGrace = TimeSpan.FromSeconds(ReadSetting(configuration, "UnusedGraceSeconds", 120, 1, 86400));
        exitRetention = TimeSpan.FromSeconds(ReadSetting(configuration, "ExitedRetentionSeconds", 300, 1, 86400));
        cleanupTask = CleanupAsync();
    }

    private static int ReadSetting(IConfiguration? configuration, string key, int fallback, int min, int max) =>
        Math.Clamp(int.TryParse(configuration?["Terminal:" + key], out var value) ? value : fallback, min, max);

    public async Task<IPtySession> CreateSessionAsync(PtyStartOptions options, CancellationToken cancellationToken = default)
    {
        await creationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            if (activeSessions.Count >= maxSessions) throw new InvalidOperationException("终端会话数量已达上限，请结束不再使用的会话");
            var session = await engine.StartSessionAsync(options, cancellationToken).ConfigureAwait(false);
            var background = new BackgroundPtySession(session, options.WorkingDirectory, bufferBytes);
            activeSessions[session.SessionId] = background;
            background.Start();
            return session;
        }
        finally { creationGate.Release(); }
    }

    public IPtySession? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return activeSessions.TryGetValue(sessionId, out var session) ? session.Pty : null;
    }

    public async Task<bool> CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (activeSessions.TryRemove(sessionId, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public IReadOnlyList<TerminalSessionInfo> ListSessions() => activeSessions.Values.Select(s => s.Snapshot()).OrderByDescending(s => s.CreatedAt).ToArray();
    public TerminalSessionInfo? GetSessionInfo(string sessionId) => activeSessions.TryGetValue(sessionId, out var session) ? session.Snapshot() : null;
    public IPtyAttachment? Attach(string sessionId, long afterSequence = 0) => activeSessions.TryGetValue(sessionId, out var session) ? session.Attach(afterSequence) : null;
    public Task WriteInputAsync(string sessionId, ReadOnlyMemory<byte> input, CancellationToken cancellationToken = default) =>
        activeSessions.TryGetValue(sessionId, out var session) ? session.WriteInputAsync(input, cancellationToken) : throw new KeyNotFoundException("会话不存在");
    public bool UpdateSession(string sessionId, TerminalSessionUpdateRequest request) => activeSessions.TryGetValue(sessionId, out var session) && session.Update(request);

    public string? IssueAttachmentTicket(string sessionId)
    {
        if (!activeSessions.ContainsKey(sessionId) || tickets.Count >= 1024) return null;
        var ticket = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        tickets[ticket] = (sessionId, DateTimeOffset.UtcNow.AddSeconds(30));
        return ticket;
    }

    public bool ConsumeAttachmentTicket(string sessionId, string ticket) =>
        tickets.TryRemove(ticket, out var value) && value.SessionId == sessionId && value.Expires > DateTimeOffset.UtcNow;

    private async Task CleanupAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(shutdown.Token).ConfigureAwait(false))
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var item in tickets)
                    if (item.Value.Expires <= now) tickets.TryRemove(item.Key, out _);
                foreach (var item in activeSessions)
                    if (item.Value.TryMarkForCleanup(now, unusedGrace, exitRetention))
                        await CloseSessionAsync(item.Key).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        shutdown.Cancel();
        await cleanupTask.ConfigureAwait(false);
        await creationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var id in activeSessions.Keys) await CloseSessionAsync(id).ConfigureAwait(false);
            tickets.Clear();
        }
        finally { creationGate.Release(); shutdown.Dispose(); }
    }
}
