using LinuxWebTool.Contracts.Features.Workers.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Workers.Supervisor;

/// <summary>
/// 进程生命周期状态机与 CrashLoop 防雪崩熔断控制器。
/// </summary>
public sealed class WorkerStateMachine
{
    private static readonly int[] BackoffDelaysSeconds = [1, 3, 10, 30];

    public string WorkerId { get; }
    public WorkerState State { get; private set; } = WorkerState.Stopped;
    public string Readiness { get; private set; } = "Unsupported";
    public long CurrentRunId { get; private set; }
    public bool IsStopRequested { get; private set; }
    public int FastCrashCount { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public string? LastError { get; private set; }

    private readonly int _maxCrashRetries;
    private readonly int _flappingThresholdSec;
    private readonly object _lock = new();

    public WorkerStateMachine(string workerId, WorkerSupervisionConfig? config = null)
    {
        WorkerId = workerId;
        _maxCrashRetries = config?.MaxCrashRetries > 0 ? config.MaxCrashRetries : 5;
        _flappingThresholdSec = config?.FlappingThresholdSec > 0 ? config.FlappingThresholdSec : 10;
    }

    /// <summary>
    /// 触发状态进入 Starting。关联当前运行代次 runId。
    /// </summary>
    public void NotifyStarting(long runId = 0)
    {
        lock (_lock)
        {
            CurrentRunId = runId;
            IsStopRequested = false;
            State = WorkerState.Starting;
            Readiness = "Starting";
            StartedAt = DateTime.UtcNow;
            LastError = null;
        }
    }

    /// <summary>
    /// 存活或就绪探测通过，判定为 Running。
    /// </summary>
    public void NotifyRunning(long runId = 0, string readiness = "Unsupported")
    {
        lock (_lock)
        {
            if (runId != 0 && runId != CurrentRunId) return;
            if (State == WorkerState.Starting)
            {
                State = WorkerState.Running;
                Readiness = readiness;
            }
        }
    }

    /// <summary>
    /// 用户手动停止或显式意图停止。
    /// </summary>
    public void NotifyStopped()
    {
        lock (_lock)
        {
            IsStopRequested = true;
            State = WorkerState.Stopped;
            Readiness = "Stopped";
            StartedAt = null;
            FastCrashCount = 0;
            LastError = null;
        }
    }

    /// <summary>
    /// 环境自检失败（缺少必需运行时），直接标记为 MissingRuntime 终态，避免误触发崩溃退避与循环重启。
    /// </summary>
    public void MarkMissingRuntime(string missingReason)
    {
        lock (_lock)
        {
            State = WorkerState.MissingRuntime;
            Readiness = "MissingRuntime";
            StartedAt = null;
            LastError = missingReason;
        }
    }

    /// <summary>
    /// 进程异常退出时调用，返回是否应自动重启以及下一次重试前的等待延迟。
    /// 若非当前 runId 或已存在人工停止意图，拒绝触发自动拉起。
    /// </summary>
    public (bool ShouldRestart, TimeSpan Delay, WorkerState NextState) RecordExit(int exitCode, long runId = 0, string? errorReason = null)
    {
        lock (_lock)
        {
            if (runId != 0 && runId != CurrentRunId)
            {
                return (false, TimeSpan.Zero, State);
            }

            if (IsStopRequested || State == WorkerState.Stopped)
            {
                return (false, TimeSpan.Zero, WorkerState.Stopped);
            }

            if (State == WorkerState.MissingRuntime)
            {
                return (false, TimeSpan.Zero, WorkerState.MissingRuntime);
            }

            var now = DateTime.UtcNow;
            var uptime = StartedAt.HasValue ? (now - StartedAt.Value).TotalSeconds : 0;
            LastError = errorReason ?? $"进程异常退出，ExitCode: {exitCode}";

            // 若正常运行超过 60 秒，重置连续崩溃计数
            if (uptime >= 60)
            {
                FastCrashCount = 0;
            }

            // 判断是否属于瞬时崩溃 (Flapping)
            if (uptime < _flappingThresholdSec)
            {
                FastCrashCount++;
            }

            // 检查是否突破熔断阈值
            if (FastCrashCount >= _maxCrashRetries)
            {
                State = WorkerState.Degraded;
                Readiness = "Degraded";
                LastError = $"触发 CrashLoop 熔断保护：在 {_flappingThresholdSec} 秒内连续崩溃 {FastCrashCount} 次，已停止无限重启。最后错误: {LastError}";
                return (false, TimeSpan.Zero, WorkerState.Degraded);
            }

            // 计算指数退避延迟
            var delayIndex = Math.Min(FastCrashCount - 1, BackoffDelaysSeconds.Length - 1);
            var delaySec = delayIndex >= 0 ? BackoffDelaysSeconds[delayIndex] : 1;
            var delay = TimeSpan.FromSeconds(delaySec);

            State = WorkerState.Backoff;
            Readiness = "Backoff";
            return (true, delay, WorkerState.Backoff);
        }
    }

    public (bool ShouldRestart, TimeSpan Delay, WorkerState NextState) RecordExit(int exitCode, string? errorReason) =>
        RecordExit(exitCode, 0, errorReason);

    /// <summary>
    /// 重置熔断与计数器。
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            FastCrashCount = 0;
            LastError = null;
            if (State == WorkerState.Degraded || State == WorkerState.Backoff || State == WorkerState.MissingRuntime)
            {
                State = WorkerState.Stopped;
            }
        }
    }
}
