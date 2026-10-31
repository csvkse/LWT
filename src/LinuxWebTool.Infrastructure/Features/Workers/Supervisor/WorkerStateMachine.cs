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
    /// 触发状态进入 Starting。
    /// </summary>
    public void NotifyStarting()
    {
        lock (_lock)
        {
            State = WorkerState.Starting;
            StartedAt = DateTime.UtcNow;
            LastError = null;
        }
    }

    /// <summary>
    /// 存活超过阈值判定为健康 Running。
    /// </summary>
    public void NotifyRunning()
    {
        lock (_lock)
        {
            if (State == WorkerState.Starting)
            {
                State = WorkerState.Running;
            }
        }
    }

    /// <summary>
    /// 用户手动停止或删除。
    /// </summary>
    public void NotifyStopped()
    {
        lock (_lock)
        {
            State = WorkerState.Stopped;
            StartedAt = null;
            FastCrashCount = 0;
            LastError = null;
        }
    }

    /// <summary>
    /// 进程异常退出时调用，返回是否应自动重启以及下一次重试前的等待延迟。
    /// </summary>
    public (bool ShouldRestart, TimeSpan Delay, WorkerState NextState) RecordExit(int exitCode, string? errorReason = null)
    {
        lock (_lock)
        {
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
                LastError = $"触发 CrashLoop 熔断保护：在 {_flappingThresholdSec} 秒内连续崩溃 {FastCrashCount} 次，已停止无限重启。最后错误: {LastError}";
                return (false, TimeSpan.Zero, WorkerState.Degraded);
            }

            // 计算指数退避延迟
            var delayIndex = Math.Min(FastCrashCount - 1, BackoffDelaysSeconds.Length - 1);
            var delaySec = delayIndex >= 0 ? BackoffDelaysSeconds[delayIndex] : 1;
            var delay = TimeSpan.FromSeconds(delaySec);

            State = WorkerState.Backoff;
            return (true, delay, WorkerState.Backoff);
        }
    }

    /// <summary>
    /// 重置熔断与计数器。
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            FastCrashCount = 0;
            LastError = null;
            if (State == WorkerState.Degraded || State == WorkerState.Backoff)
            {
                State = WorkerState.Stopped;
            }
        }
    }
}
