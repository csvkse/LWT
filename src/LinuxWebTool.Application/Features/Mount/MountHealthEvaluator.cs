
namespace LinuxWebTool.Application.Features.Mount;

/// <summary>
/// 存储挂载健康状态机决策评估器（纯领域规则）：
/// - 挂载状态流转有效性断言；
/// - 指数退避重试间隔计算；
/// - 探测延迟与错误码映射。
/// </summary>
public static class MountHealthEvaluator
{
    public const int MaxRetryAttempts = 5;
    public const int BaseRetryIntervalSeconds = 5;
    public const int MaxRetryIntervalSeconds = 300;

    /// <summary>计算指数退避重试延迟时间</summary>
    public static TimeSpan CalculateBackoff(int failedAttempts)
    {
        if (failedAttempts <= 0) return TimeSpan.FromSeconds(BaseRetryIntervalSeconds);
        var factor = Math.Pow(2, Math.Min(failedAttempts - 1, 6));
        var seconds = Math.Min(BaseRetryIntervalSeconds * factor, MaxRetryIntervalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>评估挂载健康级别</summary>
    public static MountHealthState EvaluateHealth(bool isMounted, bool canRead, long latencyMs)
    {
        if (!isMounted)
        {
            return MountHealthState.NotMounted;
        }

        if (!canRead)
        {
            return MountHealthState.ServerUnreachable;
        }

        if (latencyMs > 3000)
        {
            return MountHealthState.Stale;
        }

        return MountHealthState.Healthy;
    }
}
