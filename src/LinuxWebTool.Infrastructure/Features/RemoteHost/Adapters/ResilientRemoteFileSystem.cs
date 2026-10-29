using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Features.RemoteHost.Contracts;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;

/// <summary>
/// 具备自愈与智能降级能力的双轨远程文件系统
/// 首选 SFTP 协议；当远端环境出现协议流污染（.bashrc 打印 OSC 133 / MOTD 导致超时挂死）
/// 或未开启 SFTP 子系统时，无缝秒级降级至基于 SSH Exec 的命令行文件引擎。
/// </summary>
public sealed class ResilientRemoteFileSystem : IRemoteFileSystem
{
    private static readonly ConcurrentDictionary<string, DateTimeOffset> DegradedSftpHosts = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _hostId;
    private readonly SshClient _sshClient;
    private readonly SftpClient _sftpClient;
    private readonly ILogger? _logger;
    private IRemoteFileSystem? _activeFs;
    private bool _disposed;

    public ResilientRemoteFileSystem(
        string hostId,
        SshClient sshClient,
        SftpClient sftpClient,
        ILogger? logger = null)
    {
        _hostId = hostId;
        _sshClient = sshClient;
        _sftpClient = sftpClient;
        _logger = logger;
    }

    public string WorkingDirectory => _activeFs?.WorkingDirectory ?? "/";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 1. 检查是否为已知 SFTP 降级主机（有效期 10 分钟）
        if (DegradedSftpHosts.TryGetValue(_hostId, out var degradedUntil))
        {
            if (DateTimeOffset.UtcNow < degradedUntil)
            {
                _logger?.LogInformation("远程主机 {HostId} 处于 SFTP 降级窗口期，直接启用 SSH Exec 文件系统引擎", _hostId);
                _activeFs = new SshExecRemoteFileSystem(_sshClient, ownsClient: false, _logger);
                return;
            }
            DegradedSftpHosts.TryRemove(_hostId, out _);
        }

        // 2. 快速探测 SFTP 子系统连通性（3 秒保护）
        try
        {
            using var probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, probeCts.Token);

            await Task.Run(() => _sftpClient.Connect(), linkedCts.Token);

            if (_sftpClient.IsConnected)
            {
                _activeFs = new SshRemoteFileSystem(_sftpClient);
                return;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "远程主机 {HostId} SFTP 探测失败或超时，自动无缝降级为 SSH Exec 文件系统引擎", _hostId);
            DegradeSftp();
        }

        _activeFs = new SshExecRemoteFileSystem(_sshClient, ownsClient: false, _logger);
    }

    private void DegradeSftp()
    {
        DegradedSftpHosts[_hostId] = DateTimeOffset.UtcNow.AddMinutes(10);
        try
        {
            if (_sftpClient.IsConnected) _sftpClient.Disconnect();
            _sftpClient.Dispose();
        }
        catch { }
    }

    private async Task<T> ExecuteWithFallbackAsync<T>(
        Func<IRemoteFileSystem, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (_activeFs == null)
        {
            await InitializeAsync(cancellationToken);
        }

        try
        {
            return await operation(_activeFs!);
        }
        catch (Exception ex) when (ex is SshOperationTimeoutException or SshException && _activeFs is not SshExecRemoteFileSystem)
        {
            _logger?.LogWarning(ex, "远程主机 {HostId} SFTP 操作异常超时（检测到协议流污染），立即触发运行时降级至 SSH Exec 引擎并重试", _hostId);
            DegradeSftp();
            _activeFs = new SshExecRemoteFileSystem(_sshClient, ownsClient: false, _logger);
            return await operation(_activeFs);
        }
    }

    public Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(fs => fs.ListDirectoryAsync(path, cancellationToken), cancellationToken);
    }

    public Task<Stream> OpenReadStreamAsync(string path, CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(fs => fs.OpenReadStreamAsync(path, cancellationToken), cancellationToken);
    }

    public Task WriteFileStreamAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(async fs =>
        {
            await fs.WriteFileStreamAsync(path, content, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(fs => fs.ReadTextAsync(path, cancellationToken), cancellationToken);
    }

    public Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(async fs =>
        {
            await fs.WriteTextAsync(path, content, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(async fs =>
        {
            await fs.CreateDirectoryAsync(path, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(async fs =>
        {
            await fs.DeleteAsync(path, recursive, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        return ExecuteWithFallbackAsync(async fs =>
        {
            await fs.RenameAsync(sourcePath, destinationPath, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_activeFs != null)
        {
            try { await _activeFs.DisposeAsync(); } catch { }
        }

        try
        {
            if (_sftpClient.IsConnected) _sftpClient.Disconnect();
            _sftpClient.Dispose();
        }
        catch { }

        try
        {
            if (_sshClient.IsConnected) _sshClient.Disconnect();
            _sshClient.Dispose();
        }
        catch { }
    }
}
