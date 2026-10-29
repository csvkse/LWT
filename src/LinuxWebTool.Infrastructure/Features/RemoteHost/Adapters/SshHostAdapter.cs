using System.Diagnostics;
using System.Text;
using LinuxWebTool.Contracts.Features.RemoteHost.Contracts;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;

/// <summary>基于 SSH.NET 的通用 SSH/SFTP 远程主机适配器</summary>
public sealed class SshHostAdapter : IRemoteHostAdapter
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _username;
    private readonly string _authType;
    private readonly string? _credential;
    private readonly string? _passphrase;
    private readonly ILogger _logger;
    private SshClient? _mainClient;
    private bool _disposed;

    public string Protocol => "ssh";
    public string HostId { get; }

    public SshHostAdapter(
        string hostId,
        string host,
        int port,
        string username,
        string authType,
        string? credential,
        string? passphrase,
        ILogger logger)
    {
        HostId = hostId;
        _host = host;
        _port = port;
        _username = username;
        _authType = authType;
        _credential = credential;
        _passphrase = passphrase;
        _logger = logger;
    }

    private ConnectionInfo CreateConnectionInfo(TimeSpan? timeout = null)
    {
        AuthenticationMethod authMethod;
        if (string.Equals(_authType, "PrivateKey", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_authType, "Passphrase", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_credential))
            {
                throw new InvalidOperationException("未提供有效的 SSH 私钥");
            }
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(_credential));
            var privateKeyFile = string.IsNullOrWhiteSpace(_passphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, _passphrase);
            authMethod = new PrivateKeyAuthenticationMethod(_username, privateKeyFile);
        }
        else
        {
            authMethod = new PasswordAuthenticationMethod(_username, _credential ?? string.Empty);
        }

        var connectionInfo = new ConnectionInfo(_host, _port, _username, authMethod)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10)
        };
        return connectionInfo;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (_mainClient?.IsConnected == true) return;

        var connInfo = CreateConnectionInfo();
        _mainClient = new SshClient(connInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        };
        await Task.Run(() => _mainClient.Connect(), cancellationToken);
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var connInfo = CreateConnectionInfo(TimeSpan.FromSeconds(5));
            using var client = new SshClient(connInfo);
            await Task.Run(() => client.Connect(), cancellationToken);
            var isConnected = client.IsConnected;
            if (isConnected) client.Disconnect();
            return isConnected;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "远程 SSH 连接测试失败: {Host}:{Port}", _host, _port);
            return false;
        }
    }

    public async Task<IRemoteTerminalSession> OpenTerminalAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        var connInfo = CreateConnectionInfo();
        var client = new SshClient(connInfo)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        };
        await Task.Run(() => client.Connect(), cancellationToken);

        var terminalModes = new Dictionary<TerminalModes, uint>();
        var stream = client.CreateShellStream("xterm-256color", (uint)columns, (uint)rows, 0, 0, 4096, terminalModes);
        var sessionId = Guid.NewGuid().ToString("N");

        return new SshRemoteTerminalSession(sessionId, HostId, client, stream);
    }

    public async Task<IRemoteFileSystem> OpenFileSystemAsync(CancellationToken cancellationToken = default)
    {
        var connInfo = CreateConnectionInfo();
        var sftp = new SftpClient(connInfo)
        {
            OperationTimeout = TimeSpan.FromSeconds(15),
            KeepAliveInterval = TimeSpan.FromSeconds(20)
        };
        await Task.Run(() => sftp.Connect(), cancellationToken);
        return new SshRemoteFileSystem(sftp);
    }

    public async Task<RemoteCommandExecResponse> ExecuteCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var connInfo = CreateConnectionInfo(timeout);
        using var client = new SshClient(connInfo);
        await Task.Run(() => client.Connect(), cancellationToken);

        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = timeout;

        await Task.Run(() => cmd.Execute(), cancellationToken);
        sw.Stop();

        return new RemoteCommandExecResponse(
            ExitCode: cmd.ExitStatus ?? -1,
            Output: cmd.Result ?? string.Empty,
            ErrorOutput: cmd.Error ?? string.Empty,
            DurationMs: sw.ElapsedMilliseconds
        );
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try
        {
            if (_mainClient?.IsConnected == true) _mainClient.Disconnect();
            _mainClient?.Dispose();
        }
        catch { }
        return ValueTask.CompletedTask;
    }
}
