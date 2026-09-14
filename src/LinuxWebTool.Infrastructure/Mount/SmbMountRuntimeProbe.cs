using System.Diagnostics;
using System.Net.Sockets;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>SMB 运行期探针：TCP 探测不会触碰失效挂载，文件系统探测由有超时的子进程执行。</summary>
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

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "stat",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-f");
            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("%T");
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add(mount.LocalPath);
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(FsProbeTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "文件系统探测超时，挂载可能已失效");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return process.HasExited && process.ExitCode == 0
                ? (true, null)
                : (false, stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? $"stat 退出码 {process.ExitCode}");
        }
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
