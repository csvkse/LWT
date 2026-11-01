using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

/// <summary>
/// 插件生命周期状态机与 CrashLoop 防雪崩熔断控制器。
/// </summary>
public sealed class PluginStateMachine
{
    private static readonly int[] BackoffDelaysSeconds = [1, 3, 10, 30];

    public string PluginId { get; }
    public PluginState State { get; private set; } = PluginState.Stopped;
    public string Readiness { get; private set; } = "Unsupported";
    public long CurrentRunId { get; private set; }
    public bool IsStopRequested { get; private set; }
    public int FastCrashCount { get; private set; }
    public int CrashCount => FastCrashCount;
    public DateTime? StartedAt { get; private set; }
    public string? LastError { get; private set; }

    public void Reset() => NotifyStopped();

    private readonly int _maxCrashRetries;
    private readonly int _flappingThresholdSec;
    private readonly object _lock = new();

    public PluginStateMachine(string pluginId, PluginSupervisionConfig? config = null)
    {
        PluginId = pluginId;
        _maxCrashRetries = config?.MaxCrashRetries > 0 ? config.MaxCrashRetries : 5;
        _flappingThresholdSec = config?.FlappingThresholdSec > 0 ? config.FlappingThresholdSec : 10;
    }

    public void NotifyStarting(long runId = 0)
    {
        lock (_lock)
        {
            CurrentRunId = runId;
            IsStopRequested = false;
            State = PluginState.Starting;
            Readiness = "Starting";
            StartedAt = DateTime.UtcNow;
            LastError = null;
        }
    }

    public void NotifyRunning(long runId = 0, string readiness = "Unsupported")
    {
        lock (_lock)
        {
            if (runId != 0 && runId != CurrentRunId) return;
            if (State == PluginState.Starting)
            {
                State = PluginState.Running;
                Readiness = readiness;
            }
        }
    }

    public void NotifyStopped()
    {
        lock (_lock)
        {
            IsStopRequested = true;
            State = PluginState.Stopped;
            Readiness = "Stopped";
            StartedAt = null;
            FastCrashCount = 0;
            LastError = null;
        }
    }

    public void MarkMissingRuntime(string missingReason)
    {
        lock (_lock)
        {
            State = PluginState.MissingRuntime;
            Readiness = "MissingRuntime";
            StartedAt = null;
            LastError = missingReason;
        }
    }

    public (bool ShouldRestart, TimeSpan Delay, PluginState NextState) RecordExit(int exitCode, string? errorReason) =>
        RecordExit(exitCode, 0, errorReason);

    public (bool ShouldRestart, TimeSpan Delay, PluginState NextState) RecordExit(int exitCode, long runId = 0, string? errorReason = null)
    {
        lock (_lock)
        {
            if (runId != 0 && runId != CurrentRunId)
            {
                return (false, TimeSpan.Zero, State);
            }

            if (IsStopRequested)
            {
                State = PluginState.Stopped;
                Readiness = "Stopped";
                StartedAt = null;
                return (false, TimeSpan.Zero, PluginState.Stopped);
            }

            LastError = errorReason ?? $"进程非正常退出，退出码: {exitCode}";
            var uptime = StartedAt.HasValue ? DateTime.UtcNow - StartedAt.Value : TimeSpan.Zero;
            StartedAt = null;

            if (uptime.TotalSeconds < _flappingThresholdSec)
            {
                FastCrashCount++;
            }
            else
            {
                FastCrashCount = 1;
            }

            if (FastCrashCount >= _maxCrashRetries)
            {
                State = PluginState.Degraded;
                Readiness = "Degraded";
                LastError = $"触发 CrashLoop 熔断保护：在 {_flappingThresholdSec} 秒内连续快速崩溃 {FastCrashCount} 次。最后一次退出信息: {LastError}";
                return (false, TimeSpan.Zero, PluginState.Degraded);
            }

            State = PluginState.Crashed;
            Readiness = "Crashed";

            var delayIndex = Math.Min(FastCrashCount - 1, BackoffDelaysSeconds.Length - 1);
            if (delayIndex < 0) delayIndex = 0;
            var delay = TimeSpan.FromSeconds(BackoffDelaysSeconds[delayIndex]);

            return (true, delay, PluginState.Backoff);
        }
    }
}
