using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
namespace LinuxWebTool.Infrastructure.Features.Mount.Adapters;

public sealed record SmbDirectoryItem(string Name, bool IsDirectory, long Size, DateTime Modified);
public sealed record SmbDirectoryResult(bool Success, bool Busy, string? Error, IReadOnlyList<SmbDirectoryItem> Entries);

/// <summary>Keep CIFS directory and metadata syscalls outside the web process.</summary>
public sealed class SmbDirectoryReader(ILogger<SmbDirectoryReader> logger, MountOperationCoordinator coordinator,
    SmbMountStore store)
{
    private readonly SemaphoreSlim slots = new(3);

    public async Task<SmbDirectoryResult> ReadAsync(string root, string path, string requestId, CancellationToken cancellationToken)
    {
        root = MountOperationCoordinator.NormalizePath(root);
        return await coordinator.TryRunWithMountLockAsync(root, async () =>
        {
            var mount = (await store.GetAllAsync()).FirstOrDefault(m => MountOperationCoordinator.NormalizePath(m.LocalPath) == root);
            if (mount is null) return new SmbDirectoryResult(false, true, "挂载配置已变化，请刷新后重试", []);
            var before = SmbMountService.ReadIdentity(root);
            if (!SmbMountService.Matches(before, mount.Server))
                return new(false, true, "SMB 未挂载或身份不匹配，等待恢复；不展示本地目录", []);
            var result = await ReadCoreAsync(root, path, requestId, cancellationToken);
            var after = SmbMountService.ReadIdentity(root);
            logger.LogDebug("SMB directory identity Request={RequestId} MountId={MountId} Before={Before} After={After} Entries={Entries}",
                requestId, mount.Id, before, after, result.Entries.Count);
            return before == after ? result : new(false, true, "读取期间挂载已变化，请重试", []);
        }, cancellationToken) ?? new(false, true, "挂载正在检查或恢复，请稍后重试", []);
    }

    private async Task<SmbDirectoryResult> ReadCoreAsync(string root, string path, string requestId, CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            if (MountProbeProcessGuard.IsBlocked(root)) return new(false, true, "上次 SMB 文件系统操作尚未退出，暂停读取", []);
            acquired = await slots.WaitAsync(0, cancellationToken);
            if (!acquired) return new(false, true, "SMB 目录读取繁忙，请稍后重试", []);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var process = new Process { StartInfo = new("find")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            }};
            foreach (var arg in new[] { "--", path, "-mindepth", "1", "-maxdepth", "1", "-printf", "%f\\0%Y\\0%s\\0%T@\\0" })
                process.StartInfo.ArgumentList.Add(arg);
            var clock = Stopwatch.StartNew();
            process.Start();
            logger.LogInformation("SMB directory start Request={RequestId} Root={Root} Path={Path} PID={Pid}", requestId, root, path, process.Id);
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, timeout.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors).WaitAsync(timeout.Token);
                logger.LogInformation("SMB directory end Request={RequestId} PID={Pid} Exit={Exit} ElapsedMs={Elapsed}", requestId, process.Id, process.ExitCode, clock.ElapsedMilliseconds);
                if (process.ExitCode != 0)
                {
                    var error = (await errors).Trim();
                    logger.LogWarning("SMB directory command failed Request={RequestId} Root={Root} Path={Path} Error={Error}", requestId, root, path, error);
                    return new(false, false, error, []);
                }
                var fields = (await output).Split('\0');
                var entries = new List<SmbDirectoryItem>();
                for (var i = 0; i + 3 < fields.Length; i += 4)
                {
                    var size = long.Parse(fields[i + 2], CultureInfo.InvariantCulture);
                    var seconds = double.Parse(fields[i + 3], CultureInfo.InvariantCulture);
                    entries.Add(new(fields[i], fields[i + 1] == "d", size, DateTime.UnixEpoch.AddSeconds(seconds).ToLocalTime()));
                }
                return new(true, false, null, entries);
            }
            catch (Exception ex)
            {
                await MountProcessDiagnostics.CaptureAsync(logger, process, "find", root, clock.ElapsedMilliseconds);
                try { process.Kill(entireProcessTree: true); } catch (Exception killError) { logger.LogWarning(killError, "SMB directory process kill failed PID={Pid}", process.Id); }
                MountProbeProcessGuard.RecordIfAlive(root, process);
                logger.LogWarning(ex, "SMB directory failed Request={RequestId} Root={Root} Path={Path} PID={Pid} ElapsedMs={Elapsed} StillAlive={StillAlive}",
                    requestId, root, path, process.Id, clock.ElapsedMilliseconds, MountProbeProcessGuard.IsBlocked(root));
                logger.LogWarning("SMB blocked process Request={RequestId} PID={Pid} ProcessState={ProcessState}", requestId, process.Id, MountProbeProcessGuard.ReadState(process.Id));
                // Observe readers even when the kernel has not released the process pipes.
                _ = output.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                _ = errors.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                cancellationToken.ThrowIfCancellationRequested();
                return new(false, false, ex is OperationCanceledException ? "SMB 目录读取超时，挂载可能失效" : "SMB 目录读取失败，请查看服务日志", []);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMB directory invocation failed Request={RequestId} Root={Root} Path={Path}", requestId, root, path);
            return new(false, false, "SMB 目录读取进程启动失败，请查看服务日志", []);
        }
        finally { if (acquired) slots.Release(); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) != 0)
        {
            if (result.Length + count > 4 * 1024 * 1024) throw new IOException("SMB directory output exceeded 4 MiB");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
