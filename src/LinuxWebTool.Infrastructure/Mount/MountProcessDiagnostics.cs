using System.Diagnostics;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>Bounded proc snapshots; never reads command lines or credentials.</summary>
internal static class MountProcessDiagnostics
{
    public static async Task CaptureAsync(ILogger logger, Process process, string command, string path, long elapsedMs)
    {
        if (!OperatingSystem.IsLinux()) return;
        var snapshotId = Guid.NewGuid();
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        async Task<string> Read(string name)
        {
            try
            {
                using var reader = new StreamReader(name);
                var buffer = new char[2048];
                var count = await reader.ReadAsync(buffer.AsMemory(), budget.Token).AsTask().WaitAsync(budget.Token);
                return new string(buffer, 0, count).Replace('\0', ' ');
            }
            catch (Exception ex) { return $"unavailable:{ex.GetType().Name}"; }
        }
        try
        {
            var pid = process.Id;
            var state = MountProbeProcessGuard.ReadState(pid);
            var wchan = await Read($"/proc/{pid}/wchan");
            var syscall = await Read($"/proc/{pid}/syscall");
            var stack = await Read($"/proc/{pid}/stack");
            var children = await Read($"/proc/{pid}/task/{pid}/children");
            logger.LogWarning("SMB process snapshot SnapshotId={SnapshotId} Command={Command} Path={Path} PID={Pid} ElapsedMs={Elapsed} State={State} Wchan={Wchan} Children={Children}",
                snapshotId, command, path, pid, elapsedMs, state, wchan.Trim(), children.Trim());
            logger.LogDebug("SMB process snapshot detail SnapshotId={SnapshotId} Syscall={Syscall} Stack={Stack}", snapshotId, syscall, stack);
            foreach (var child in children.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(4))
                if (int.TryParse(child.Trim(), out var childPid))
                    logger.LogDebug("SMB process snapshot child SnapshotId={SnapshotId} PID={Pid} State={State} Wchan={Wchan}",
                        snapshotId, childPid, MountProbeProcessGuard.ReadState(childPid), await Read($"/proc/{childPid}/wchan"));
        }
        catch (Exception ex) { logger.LogDebug("SMB snapshot unavailable SnapshotId={SnapshotId} Reason={Reason}", snapshotId, ex.GetType().Name); }
    }
}
