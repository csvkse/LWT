using System.Collections.Concurrent;
using System.Diagnostics;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>Do not repeatedly spawn probes while a killed process remains blocked in the kernel.</summary>
internal static class MountProbeProcessGuard
{
    private sealed record PendingProcess(int Id, DateTime StartedAt);
    private static readonly ConcurrentDictionary<string, PendingProcess> pending = new(StringComparer.Ordinal);

    public static bool IsBlocked(string path)
    {
        path = MountOperationCoordinator.NormalizePath(path);
        if (!pending.TryGetValue(path, out var previous)) return false;
        try
        {
            using var process = Process.GetProcessById(previous.Id);
            if (!process.HasExited && process.StartTime == previous.StartedAt) return true;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { return true; }
        pending.TryRemove(path, out _);
        return false;
    }

    public static void RecordIfAlive(string path, Process process)
    {
        try
        {
            if (!process.HasExited)
                pending[MountOperationCoordinator.NormalizePath(path)] = new(process.Id, process.StartTime);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
