using System.Text;
using LinuxWebTool.Contracts.Features.RemoteHost.Contracts;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;

/// <summary>基于 SFTP 的远程文件系统操作实现</summary>
public sealed class SshRemoteFileSystem : IRemoteFileSystem
{
    private readonly SftpClient _sftp;
    private bool _disposed;

    public SshRemoteFileSystem(SftpClient sftp)
    {
        _sftp = sftp;
    }

    public string WorkingDirectory => _sftp.WorkingDirectory ?? "/";

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var targetPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = _sftp.ListDirectory(targetPath);
            var entries = new List<RemoteFileEntry>();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Name is "." or "..") continue;

                long size = 0;
                try { size = file.Length; } catch { }

                DateTimeOffset? lastModified = null;
                try { lastModified = file.LastWriteTimeUtc; } catch { }

                bool isDir = false;
                try { isDir = file.IsDirectory; } catch { }

                string perms;
                try { perms = GetPermissionsString(file); } catch { perms = isDir ? "drwxr-xr-x" : "-rw-r--r--"; }

                entries.Add(new RemoteFileEntry(
                    Name: file.Name,
                    Path: file.FullName,
                    Size: size,
                    IsDirectory: isDir,
                    LastModified: lastModified,
                    Permissions: perms
                ));
            }

            // 排序：目录优先，名称字母序
            return (IReadOnlyList<RemoteFileEntry>)entries
                .OrderByDescending(e => e.IsDirectory)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, cancellationToken);
    }

    public Task<Stream> OpenReadStreamAsync(string path, CancellationToken cancellationToken = default)
    {
        var stream = _sftp.OpenRead(path);
        return Task.FromResult<Stream>(stream);
    }

    public async Task WriteFileStreamAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        await using var sftpStream = _sftp.Open(path, FileMode.Create, FileAccess.Write);
        await content.CopyToAsync(sftpStream, 65536, cancellationToken);
    }

    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = _sftp.OpenRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var mem = new MemoryStream(bytes);
        await WriteFileStreamAsync(path, mem, cancellationToken);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sftp.CreateDirectory(path);
        }, cancellationToken);
    }

    public Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sftp.Exists(path))
            {
                var attrs = _sftp.GetAttributes(path);
                if (attrs.IsDirectory)
                {
                    if (recursive)
                    {
                        DeleteDirectoryRecursive(path, cancellationToken);
                    }
                    else
                    {
                        _sftp.DeleteDirectory(path);
                    }
                }
                else
                {
                    _sftp.DeleteFile(path);
                }
            }
        }, cancellationToken);
    }

    public Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            _sftp.RenameFile(sourcePath, destinationPath);
        }, cancellationToken);
    }

    private void DeleteDirectoryRecursive(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = _sftp.ListDirectory(path);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Name is "." or "..") continue;
            if (item.IsDirectory)
            {
                DeleteDirectoryRecursive(item.FullName, cancellationToken);
            }
            else
            {
                _sftp.DeleteFile(item.FullName);
            }
        }
        _sftp.DeleteDirectory(path);
    }

    private static string GetPermissionsString(ISftpFile file)
    {
        try
        {
            var isDir = file.IsDirectory;
            var sb = new StringBuilder(10);
            sb.Append(isDir ? 'd' : '-');
            sb.Append(file.OwnerCanRead ? 'r' : '-');
            sb.Append(file.OwnerCanWrite ? 'w' : '-');
            sb.Append(file.OwnerCanExecute ? 'x' : '-');
            sb.Append(file.GroupCanRead ? 'r' : '-');
            sb.Append(file.GroupCanWrite ? 'w' : '-');
            sb.Append(file.GroupCanExecute ? 'x' : '-');
            sb.Append(file.OthersCanRead ? 'r' : '-');
            sb.Append(file.OthersCanWrite ? 'w' : '-');
            sb.Append(file.OthersCanExecute ? 'x' : '-');
            return sb.ToString();
        }
        catch
        {
            return "-rw-r--r--";
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        try
        {
            if (_sftp.IsConnected) _sftp.Disconnect();
            _sftp.Dispose();
        }
        catch { }
        return ValueTask.CompletedTask;
    }
}
