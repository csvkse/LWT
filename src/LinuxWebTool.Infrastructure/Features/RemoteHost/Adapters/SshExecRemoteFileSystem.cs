using System.Globalization;
using System.Text;
using LinuxWebTool.Contracts.Features.RemoteHost.Contracts;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;

/// <summary>
/// 基于 SSH Exec 命令行通道的 POSIX 远程文件系统实现（SFTP 降级备用引擎）
/// 适用于 SFTP 子系统缺失、禁用或被终端 Shell 启动脚本 (.bashrc / OSC 133) 污染的环境
/// </summary>
public sealed class SshExecRemoteFileSystem : IRemoteFileSystem
{
    private readonly SshClient _client;
    private readonly bool _ownsClient;
    private readonly ILogger? _logger;
    private string? _workingDirectory;
    private bool _disposed;

    public SshExecRemoteFileSystem(SshClient client, bool ownsClient = false, ILogger? logger = null)
    {
        _client = client;
        _ownsClient = ownsClient;
        _logger = logger;
    }

    public string WorkingDirectory
    {
        get
        {
            if (_workingDirectory != null) return _workingDirectory;
            try
            {
                var res = ExecuteRawCommand("pwd");
                _workingDirectory = string.IsNullOrWhiteSpace(res) ? "/" : res.Trim();
            }
            catch
            {
                _workingDirectory = "/";
            }
            return _workingDirectory;
        }
    }

    public async Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        var targetPath = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var safePath = EscapeBashArg(targetPath);
            // 采用双重探测：优先使用 GNU find 格式化输出；若无 find 则降级至 stat
            var script = $@"
P={safePath}
[ -z ""$P"" ] && P=""/""
if [ ! -e ""$P"" ]; then
  echo ""ERR:NOT_FOUND"" >&2
  exit 1
fi
if [ ! -d ""$P"" ]; then
  echo ""ERR:NOT_DIR"" >&2
  exit 2
fi
if command -v find >/dev/null 2>&1; then
  LC_ALL=C find ""$P"" -maxdepth 1 -mindepth 1 -printf ""%y|%s|%T@|%m|%f\n"" 2>/dev/null
else
  LC_ALL=C stat -c ""%F|%s|%Y|%a|%n"" ""$P""/* ""$P""/.[!.]* 2>/dev/null
fi
";

            using var cmd = _client.CreateCommand($"/bin/sh -c '{EscapeSingleQuoteScript(script)}'");
            cmd.CommandTimeout = TimeSpan.FromSeconds(10);
            var output = cmd.Execute();

            if (cmd.ExitStatus != 0 && output.Contains("ERR:NOT_FOUND", StringComparison.OrdinalIgnoreCase))
            {
                throw new DirectoryNotFoundException($"远程目录不存在: {targetPath}");
            }

            var entries = ParseDirectoryOutput(output, targetPath);
            return entries;
        }, cancellationToken);
    }

    public static List<RemoteFileEntry> ParseDirectoryOutput(string output, string basePath)
    {
        var cleanOutput = AnsiOutputSanitizer.Sanitize(output);
        var entries = new List<RemoteFileEntry>();
        var lines = cleanOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed)) continue;
            if (trimmed.StartsWith("ERR:", StringComparison.OrdinalIgnoreCase)) continue;

            var parts = trimmed.Split('|');
            if (parts.Length < 5) continue;

            var typeIndicator = parts[0].Trim();
            var sizeStr = parts[1].Trim();
            var timeStr = parts[2].Trim();
            var permStr = parts[3].Trim();
            var name = string.Join('|', parts.Skip(4)).Trim();

            if (name is "." or ".." or "") continue;

            // 如果 name 是全路径，提取 basename
            if (name.Contains('/'))
            {
                name = name.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? name;
            }

            var isDir = typeIndicator.Equals("d", StringComparison.OrdinalIgnoreCase) ||
                        typeIndicator.Contains("directory", StringComparison.OrdinalIgnoreCase);

            long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size);

            DateTimeOffset? lastModified = null;
            if (double.TryParse(timeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var epochSec))
            {
                try
                {
                    lastModified = DateTimeOffset.FromUnixTimeSeconds((long)epochSec);
                }
                catch { }
            }

            var formattedPerms = FormatPermissions(typeIndicator, permStr);
            var fullPath = basePath.EndsWith('/') ? $"{basePath}{name}" : $"{basePath}/{name}";

            entries.Add(new RemoteFileEntry(
                Name: name,
                Path: fullPath,
                Size: size,
                IsDirectory: isDir,
                LastModified: lastModified,
                Permissions: formattedPerms
            ));
        }

        // 统一排序：目录优先，名称字母序
        return entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string FormatPermissions(string typeIndicator, string octalOrPerm)
    {
        var isDir = typeIndicator.Equals("d", StringComparison.OrdinalIgnoreCase) ||
                    typeIndicator.Contains("directory", StringComparison.OrdinalIgnoreCase);
        var isSymlink = typeIndicator.Equals("l", StringComparison.OrdinalIgnoreCase) ||
                        typeIndicator.Contains("link", StringComparison.OrdinalIgnoreCase);
        var prefix = isDir ? 'd' : (isSymlink ? 'l' : '-');

        if (octalOrPerm.Length >= 3 && int.TryParse(octalOrPerm[^3..], out var octalVal))
        {
            var u = (octalVal / 100) % 10;
            var g = (octalVal / 10) % 10;
            var o = octalVal % 10;

            static string ModeToRwx(int m) =>
                $"{( (m & 4) != 0 ? 'r' : '-' )}{( (m & 2) != 0 ? 'w' : '-' )}{( (m & 1) != 0 ? 'x' : '-' )}";

            return $"{prefix}{ModeToRwx(u)}{ModeToRwx(g)}{ModeToRwx(o)}";
        }

        return isDir ? "drwxr-xr-x" : (isSymlink ? "lrwxrwxrwx" : "-rw-r--r--");
    }

    public async Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safePath = EscapeBashArg(path);
            var script = $@"
if command -v base64 >/dev/null 2>&1; then
  echo -n ""B64:""
  base64 -w 0 ""$1"" 2>/dev/null || base64 ""$1"" 2>/dev/null
else
  echo -n ""RAW:""
  cat ""$1""
fi
";
            using var cmd = _client.CreateCommand($"/bin/sh -c '{EscapeSingleQuoteScript(script)}' _ {safePath}");
            cmd.CommandTimeout = TimeSpan.FromSeconds(15);
            var result = cmd.Execute();

            if (result.StartsWith("B64:", StringComparison.Ordinal))
            {
                var b64Data = result[4..].Trim();
                // 移除可能的内部换行
                b64Data = b64Data.Replace("\r", string.Empty).Replace("\n", string.Empty);
                var bytes = Convert.FromBase64String(b64Data);
                return Encoding.UTF8.GetString(bytes);
            }

            if (result.StartsWith("RAW:", StringComparison.Ordinal))
            {
                return result[4..];
            }

            return AnsiOutputSanitizer.Sanitize(result);
        }, cancellationToken);
    }

    public async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safePath = EscapeBashArg(path);
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(content));
            var safeB64 = EscapeBashArg(b64);

            var script = "printf \"%s\" \"$2\" | base64 -d > \"$1\"";
            using var cmd = _client.CreateCommand($"/bin/sh -c '{EscapeSingleQuoteScript(script)}' _ {safePath} {safeB64}");
            cmd.CommandTimeout = TimeSpan.FromSeconds(15);
            cmd.Execute();

            if (cmd.ExitStatus != 0)
            {
                throw new InvalidOperationException($"写入文件失败: {cmd.Error}");
            }
        }, cancellationToken);
    }

    public async Task<Stream> OpenReadStreamAsync(string path, CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safePath = EscapeBashArg(path);
            var script = $@"
P={safePath}
if [ -d ""$P"" ]; then
  D=$(dirname ""$P"")
  B=$(basename ""$P"")
  tar -czf - -C ""$D"" ""$B"" 2>/dev/null | base64
else
  base64 -w 0 ""$P"" 2>/dev/null || base64 ""$P"" 2>/dev/null
fi
";
            using var cmd = _client.CreateCommand($"/bin/sh -c '{EscapeSingleQuoteScript(script)}'");
            cmd.CommandTimeout = TimeSpan.FromSeconds(60);
            var result = cmd.Execute();

            var clean = AnsiOutputSanitizer.Sanitize(result)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Trim();

            if (string.IsNullOrEmpty(clean))
            {
                return (Stream)new MemoryStream();
            }

            var bytes = Convert.FromBase64String(clean);
            return (Stream)new MemoryStream(bytes);
        }, cancellationToken);
    }

    public async Task WriteFileStreamAsync(string path, Stream content, CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, cancellationToken);
        var b64 = Convert.ToBase64String(ms.ToArray());

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safePath = EscapeBashArg(path);
            var safeB64 = EscapeBashArg(b64);
            var script = "printf \"%s\" \"$2\" | base64 -d > \"$1\"";
            using var cmd = _client.CreateCommand($"/bin/sh -c '{EscapeSingleQuoteScript(script)}' _ {safePath} {safeB64}");
            cmd.CommandTimeout = TimeSpan.FromSeconds(60);
            cmd.Execute();

            if (cmd.ExitStatus != 0)
            {
                throw new InvalidOperationException($"写入文件失败: {cmd.Error}");
            }
        }, cancellationToken);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safePath = EscapeBashArg(path);
            using var cmd = _client.CreateCommand($"/bin/sh -c 'mkdir -p \"$1\"' _ {safePath}");
            cmd.CommandTimeout = TimeSpan.FromSeconds(10);
            cmd.Execute();
            if (cmd.ExitStatus != 0)
            {
                throw new InvalidOperationException($"创建目录失败: {cmd.Error}");
            }
        }, cancellationToken);
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safePath = EscapeBashArg(path);
            var flag = recursive ? "-rf" : "-f";
            using var cmd = _client.CreateCommand($"/bin/sh -c 'rm {flag} \"$1\"' _ {safePath}");
            cmd.CommandTimeout = TimeSpan.FromSeconds(15);
            cmd.Execute();
            if (cmd.ExitStatus != 0)
            {
                throw new InvalidOperationException($"删除失败: {cmd.Error}");
            }
        }, cancellationToken);
    }

    public async Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeSource = EscapeBashArg(sourcePath);
            var safeDest = EscapeBashArg(destinationPath);
            using var cmd = _client.CreateCommand($"/bin/sh -c 'mv \"$1\" \"$2\"' _ {safeSource} {safeDest}");
            cmd.CommandTimeout = TimeSpan.FromSeconds(10);
            cmd.Execute();
            if (cmd.ExitStatus != 0)
            {
                throw new InvalidOperationException($"重命名失败: {cmd.Error}");
            }
        }, cancellationToken);
    }

    public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safeSource = EscapeBashArg(sourcePath);
            var safeDest = EscapeBashArg(destinationPath);
            using var cmd = _client.CreateCommand($"/bin/sh -c 'cp -r \"$1\" \"$2\"' _ {safeSource} {safeDest}");
            cmd.CommandTimeout = TimeSpan.FromSeconds(30);
            cmd.Execute();
            if (cmd.ExitStatus != 0)
            {
                throw new InvalidOperationException($"复制失败: {cmd.Error}");
            }
        }, cancellationToken);
    }

    private string ExecuteRawCommand(string command)
    {
        using var cmd = _client.CreateCommand(command);
        cmd.CommandTimeout = TimeSpan.FromSeconds(5);
        return cmd.Execute();
    }

    private static string EscapeBashArg(string arg)
    {
        return "'" + arg.Replace("'", "'\\''") + "'";
    }

    private static string EscapeSingleQuoteScript(string script)
    {
        return script.Replace("'", "'\\''");
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        if (_ownsClient)
        {
            try
            {
                if (_client.IsConnected) _client.Disconnect();
                _client.Dispose();
            }
            catch { }
        }
        return ValueTask.CompletedTask;
    }
}
