using System.Collections.Concurrent;

namespace LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

/// <summary>
/// 远程主机执行熔断器：防止目标主机离线/异常时持续重试拖垮调度线程池。
/// 连续失败达到阈值即触发熔断，在隔离恢复期内自动跳过（Skipped）执行。
/// </summary>
public sealed class HostCircuitBreaker
{
    private readonly ConcurrentDictionary<string, HostCircuitState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _failureThreshold;
    private readonly TimeSpan _openDuration;

    public HostCircuitBreaker(int failureThreshold = 3, TimeSpan? openDuration = null)
    {
        _failureThreshold = Math.Max(1, failureThreshold);
        _openDuration = openDuration ?? TimeSpan.FromSeconds(60);
    }

    public bool AllowExecution(string hostId, out string? reason)
    {
        var state = _states.GetOrAdd(hostId, _ => new HostCircuitState());
        lock (state)
        {
            if (state.State == CircuitState.Open)
            {
                if (DateTime.UtcNow - state.LastStateChange >= _openDuration)
                {
                    state.State = CircuitState.HalfOpen;
                    state.LastStateChange = DateTime.UtcNow;
                    reason = null;
                    return true;
                }

                var remainingSeconds = (int)Math.Max(1, (_openDuration - (DateTime.UtcNow - state.LastStateChange)).TotalSeconds);
                reason = $"远端主机连续失败 {state.FailureCount} 次，处于熔断隔离期（剩余约 {remainingSeconds} 秒，最近原因: {state.LastFailureMessage ?? "连接超时"}）";
                return false;
            }

            reason = null;
            return true;
        }
    }

    public void ReportSuccess(string hostId)
    {
        var state = _states.GetOrAdd(hostId, _ => new HostCircuitState());
        lock (state)
        {
            state.FailureCount = 0;
            state.State = CircuitState.Closed;
            state.LastStateChange = DateTime.UtcNow;
            state.LastFailureMessage = null;
        }
    }

    public void ReportFailure(string hostId, string? errorMessage = null)
    {
        var state = _states.GetOrAdd(hostId, _ => new HostCircuitState());
        lock (state)
        {
            state.FailureCount++;
            state.LastFailureMessage = errorMessage;
            if (state.State == CircuitState.HalfOpen || state.FailureCount >= _failureThreshold)
            {
                state.State = CircuitState.Open;
                state.LastStateChange = DateTime.UtcNow;
            }
        }
    }

    public void Reset(string hostId)
    {
        _states.TryRemove(hostId, out _);
    }

    public CircuitState GetState(string hostId)
    {
        return _states.TryGetValue(hostId, out var state) ? state.State : CircuitState.Closed;
    }

    private sealed class HostCircuitState
    {
        public CircuitState State { get; set; } = CircuitState.Closed;
        public int FailureCount { get; set; }
        public DateTime LastStateChange { get; set; } = DateTime.UtcNow;
        public string? LastFailureMessage { get; set; }
    }
}
