using System.Diagnostics;
using System.Net.Sockets;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>SMB 运行期探针：TCP 探测不会触碰失效挂载，文件系统与容量探测由有超时的子进程执行。</summary>
public sealed class SmbMountRuntimeProbe : IMountRuntimeProbe
{
    private static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FsProbeTimeout = TimeSpan.FromSeconds(5);
    public async Task<bool> IsServerReachableAsync(SmbMount mount, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            using var client = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TcpTimeout);
            await client.ConnectAsync(ExtractHost(mount.Server), ExtractPort(mount), timeoutCts.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Success, string? Error)> IsFileSystemAccessibleAsync(SmbMount mount, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return (false, "当前系统不支持文件系统探测");
        }

        // 读取目录项会经过 CIFS readdir，statfs 成功并不能证明共享目录可用。
        return await RunFsProbeAsync(
            ["ls", "-A", "--", mount.LocalPath],
            FsProbeTimeout,
            "文件系统探测超时，挂载可能已失效",
            cancellationToken);
    }

    public async Task<(bool Success, string? Error)> IsCapacityProbeOkAsync(SmbMount mount, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return (false, "当前系统不支持容量探测");
        }

        // 容量探测补充目录探测，但不能证明所有子目录可用，也不能保证绕过 CIFS 缓存。
        return await RunFsProbeAsync(
            ["df", "-P", "--", mount.LocalPath],
            FsProbeTimeout,
            "容量探测超时，SMB 会话可能已退化",
            cancellationToken);
    }

    internal static async Task<(bool Success, string? Error)> RunFsProbeAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        string timeoutError,
        CancellationToken cancellationToken, string? guardedPath = null)
    {
        var path = guardedPath ?? arguments[^1];
        if (MountProbeProcessGuard.IsBlocked(path)) return (false, "上次文件系统探针尚未退出");
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = arguments[0],
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var argument in arguments.Skip(1))
            {
                process.StartInfo.ArgumentList.Add(argument);
            }
            process.StartInfo.Environment["LC_ALL"] = "C";

            process.Start();
            var stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                MountProbeProcessGuard.RecordIfAlive(path, process);
                return (false, timeoutError);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                MountProbeProcessGuard.RecordIfAlive(path, process);
                throw;
            }

            await stdoutTask;
            var stderr = await stderrTask;
            return process.HasExited && process.ExitCode == 0
                ? (true, null)
                : (false, stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? $"退出码 {process.ExitCode}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string ExtractHost(string server)
    {
        var normalized = server.Trim().Replace('\\', '/').TrimStart('/');
        var host = normalized.Split('/', 2)[0];
        var colon = host.LastIndexOf(':');
        return colon > 0 && int.TryParse(host[(colon + 1)..], out _) ? host[..colon] : host;
    }

    private static int ExtractPort(SmbMount mount)
    {
        var option = (mount.Options ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(o => o.StartsWith("port=", StringComparison.OrdinalIgnoreCase));
        return option is not null
            && int.TryParse(option["port=".Length..], out var port)
            && port > 0 ? port : 445;
    }
}
