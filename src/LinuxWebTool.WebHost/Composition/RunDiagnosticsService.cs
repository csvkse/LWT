
namespace LinuxWebTool.WebHost.Composition;

/// <summary>Persistent evidence of lifecycle completion, not a proof of crash cause.</summary>
public sealed class RunDiagnosticsService(DataPaths paths, IHostApplicationLifetime lifetime,
    ILogger<RunDiagnosticsService> logger) : IHostedService
{
    private readonly string runId = Guid.NewGuid().ToString("N");
    private IDisposable? stopping;
    private IDisposable? stopped;
    private string Marker => paths.PathFor("run-state.txt");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(Marker))
            {
                var previous = File.ReadAllText(Marker);
                if (!previous.StartsWith("completed ", StringComparison.Ordinal))
                    logger.LogWarning("Previous run has no normal exit record Marker={Marker}; shared data directory or write failure can also explain this", previous[..Math.Min(previous.Length, 160)]);
            }
            File.WriteAllText(Marker, $"running {runId} {DateTime.UtcNow:O}");
        }
        catch (Exception ex) { logger.LogWarning(ex, "Run marker unavailable RunId={RunId}", runId); }
        string mountNamespace = "unsupported";
        if (OperatingSystem.IsLinux())
        {
            try { mountNamespace = new FileInfo("/proc/self/ns/mnt").LinkTarget ?? "unavailable"; }
            catch { mountNamespace = "unavailable"; }
        }
        logger.LogInformation("Application run start RunId={RunId} Version={Version} PID={Pid} MountNamespace={Namespace}",
            runId, typeof(RunDiagnosticsService).Assembly.GetName().Version, Environment.ProcessId, mountNamespace);
        if (OperatingSystem.IsLinux())
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/self/mountinfo").Where(l => l.Contains(" - cifs ")).Take(64))
                {
                    var parts = line.Split(' ');
                    var separator = Array.IndexOf(parts, "-");
                    if (separator > 5 && parts.Length > separator + 2)
                        logger.LogInformation("Startup CIFS inventory RunId={RunId} KernelMountId={Id} Path={Path} Source={Source} Options={Options}",
                            runId, parts[0], parts[4], parts[separator + 2],
                            string.Join(',', parts[5].Split(',').Where(o => o is "rw" or "ro" or "nosuid" or "nodev" or "noexec" or "relatime")));
                }
            }
            catch (Exception ex) { logger.LogWarning(ex, "Startup mount inventory unavailable RunId={RunId}", runId); }
        }
        stopping = lifetime.ApplicationStopping.Register(() => logger.LogInformation("Application stopping RunId={RunId} MountCleanupPolicy=PreserveMounts", runId));
        stopped = lifetime.ApplicationStopped.Register(() =>
        {
            try
            {
                if (File.ReadAllText(Marker).StartsWith($"running {runId} ", StringComparison.Ordinal))
                    File.WriteAllText(Marker, $"completed {runId} {DateTime.UtcNow:O}");
            }
            catch (Exception ex) { logger.LogWarning(ex, "Run completion marker unavailable RunId={RunId}", runId); }
            logger.LogInformation("Application stopped RunId={RunId}", runId);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
