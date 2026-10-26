namespace LinuxWebTool.Contracts.Features.RemoteHost.Contracts;

/// <summary>多协议远程主机适配器抽象契约</summary>
public interface IRemoteHostAdapter : IAsyncDisposable
{
    string Protocol { get; }
    string HostId { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default);
    Task<IRemoteTerminalSession> OpenTerminalAsync(int columns, int rows, CancellationToken cancellationToken = default);
    Task<IRemoteFileSystem> OpenFileSystemAsync(CancellationToken cancellationToken = default);
    Task<RemoteCommandExecResponse> ExecuteCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default);
}
