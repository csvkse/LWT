using LinuxWebTool.Contracts.Features.RemoteHost.Contracts;
using Renci.SshNet;

namespace LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;

/// <summary>基于 SSH.NET ShellStream 的远程交互终端会话</summary>
public sealed class SshRemoteTerminalSession : IRemoteTerminalSession
{
    private readonly SshClient _client;
    private readonly ShellStream _stream;
    private bool _disposed;

    public string SessionId { get; }
    public string HostId { get; }
    public Stream StandardInput => _stream;
    public Stream StandardOutput => _stream;

    public SshRemoteTerminalSession(string sessionId, string hostId, SshClient client, ShellStream stream)
    {
        SessionId = sessionId;
        HostId = hostId;
        _client = client;
        _stream = stream;
    }

    public Task ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        // ShellStream 在部分实现中支持通过扩展或反射，或在不支持时做无操作保活
        return Task.CompletedTask;
    }

    public Task TerminateAsync(CancellationToken cancellationToken = default)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _stream.Dispose(); } catch { }
        try
        {
            if (_client.IsConnected) _client.Disconnect();
            _client.Dispose();
        }
        catch { }
    }
}
