using System.Collections.Concurrent;
using System.Diagnostics;
namespace LinuxWebTool.Infrastructure.Features.Mount.Adapters;

/// <summary>
/// 防止针对挂起内核（如 D 状态 CIFS）反复生成探测子进程。
/// 具备 30 秒有界 TTL 淘汰、Linux 僵尸进程(Z)豁免以及主动 Clear 清理能力。
/// </summary>
internal static class MountProbeProcessGuard
{
    private sealed record PendingProcess(int Id, DateTime StartedAt, DateTime RecordedAt);
    private static readonly ConcurrentDictionary<string, PendingProcess> pending = new(StringComparer.Ordinal);
    private static readonly TimeSpan DefaultTtl = TimeSpan.FromSeconds(30);

    public static bool IsBlocked(string path, TimeSpan? ttl = null)
    {
        path = MountOperationCoordinator.NormalizePath(path);
        if (!pending.TryGetValue(path, out var previous)) return false;

        // 1. 超时淘汰（TTL 防御）：绝不允许永久卡死阻塞
        var maxAge = ttl ?? DefaultTtl;
        if (DateTime.UtcNow - previous.RecordedAt > maxAge)
        {
            pending.TryRemove(path, out _);
            return false;
        }

        // 2. Linux /proc 真实内核状态嗅探
        if (OperatingSystem.IsLinux())
        {
            var state = ReadState(previous.Id);
            // 僵尸进程(Z)、已销毁进程(X/exited-or-unavailable)、未找到 -> 绝不阻塞
            if (state is "Z" or "X" or "exited-or-unavailable")
            {
                pending.TryRemove(path, out _);
                return false;
            }

            // 若非 D 状态（比如处于可中断睡眠 S 或运行 R），尝试再次触发强杀，若未能杀死则根据 TTL 判断
            if (state is not "D")
            {
                try
                {
                    using var p = Process.GetProcessById(previous.Id);
                    if (!p.HasExited) p.Kill(entireProcessTree: true);
                }
                catch { }
            }

            // 只有当进程在 Linux 内核真正处于 D 状态（TASK_UNINTERRUPTIBLE）且在 TTL 窗口内时才判定阻塞
            return true;
        }

        // 3. 非 Linux 平台或兜底探测
        try
        {
            using var process = Process.GetProcessById(previous.Id);
            if (!process.HasExited && process.StartTime == previous.StartedAt) return true;
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }

        pending.TryRemove(path, out _);
        return false;
    }

    public static void RecordIfAlive(string path, Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                var norm = MountOperationCoordinator.NormalizePath(path);
                pending[norm] = new(process.Id, process.StartTime, DateTime.UtcNow);
            }
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public static void Clear(string path)
    {
        pending.TryRemove(MountOperationCoordinator.NormalizePath(path), out _);
    }

    public static string ReadState(int pid)
    {
        if (!OperatingSystem.IsLinux()) return "unknown";
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[(stat.LastIndexOf(')') + 1)..].TrimStart().Split(' ')[0];
        }
        catch { return "exited-or-unavailable"; }
    }
}
