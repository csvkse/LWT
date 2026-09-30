using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Terminal;

namespace LinuxWebTool.Infrastructure.Terminal;

public sealed class PtySessionManager : IPtySessionManager, IAsyncDisposable
{
    private readonly IPtyEngine engine;
    private readonly ConcurrentDictionary<string, IPtySession> activeSessions = new(StringComparer.Ordinal);
    private int disposed;

    public PtySessionManager(IPtyEngine engine)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    public async Task<IPtySession> CreateSessionAsync(PtyStartOptions options, CancellationToken cancellationToken = default)
    {
        var session = await engine.StartSessionAsync(options, cancellationToken).ConfigureAwait(false);
        activeSessions[session.SessionId] = session;
        return session;
    }

    public IPtySession? GetSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return activeSessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    public async Task<bool> CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (activeSessions.TryRemove(sessionId, out var session))
        {
            await session.TerminateAsync(cancellationToken).ConfigureAwait(false);
            await session.DisposeAsync().ConfigureAwait(false);
            return true;
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var (id, session) in activeSessions)
        {
            try
            {
                await session.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch { }
        }
        activeSessions.Clear();
    }
}
