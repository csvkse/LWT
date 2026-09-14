namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>
/// 启动期只负责快速重试；仍失败时立刻交给 MountHealthService 做长周期退避恢复。
/// 返回值表示“执行下一次尝试前”的等待时间。
/// </summary>
internal static class SmbMountStartupRetryPlan
{
    internal const int AttemptCount = 3;

    internal static TimeSpan? GetDelayBeforeAttempt(int attempt) => attempt switch
    {
        <= 0 => throw new ArgumentOutOfRangeException(nameof(attempt)),
        1 => null,
        2 => TimeSpan.FromSeconds(5),
        3 => TimeSpan.FromSeconds(15),
        _ => null,
    };
}
