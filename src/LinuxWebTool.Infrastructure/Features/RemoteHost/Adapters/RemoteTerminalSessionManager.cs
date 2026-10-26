using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Features.RemoteHost.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;

/// <summary>活动远程交互终端会话管理器</summary>
public sealed class RemoteTerminalSessionManager(ILogger<RemoteTerminalSessionManager> logger) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, IRemoteTerminalSession> _sessions = new();

    public void RegisterSession(IRemoteTerminalSession session)
    {
        _sessions[session.SessionId] = session;
    }

    public IRemoteTerminalSession? GetSession(string sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    public async Task<bool> CloseSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            try
            {
                await session.TerminateAsync(cancellationToken);
                await session.DisposeAsync();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "关闭远程终端会话异常: {SessionId}", sessionId);
            }
        }
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var (id, session) in _sessions)
        {
            try
            {
                await session.TerminateAsync();
                await session.DisposeAsync();
            }
            catch { }
        }
        _sessions.Clear();
    }
}
