namespace LinuxWebTool.Infrastructure.Scheduling;

/// <summary>定时任务与 Quartz 调度器的同步接口（由 ScheduleManager 实现）。</summary>
public interface IScheduleManager
{
    /// <summary>任务增改 / 启停后同步：Enabled 时注册 Cron 触发器，否则移除。返回下次触发时间。</summary>
    Task<DateTime?> SyncAsync(ScheduleTask task);

    /// <summary>任务删除后移除对应调度。</summary>
    Task RemoveAsync(Guid taskId);

    /// <summary>手动立即执行一次（一次性触发器，不影响 Cron 计划）。</summary>
    Task TriggerNowAsync(ScheduleTask task);

    /// <summary>应用启动时按当前启用任务全量重建调度（内存调度重启即清空，天然一致）。</summary>
    Task SyncAllAsync(IReadOnlyList<ScheduleTask> tasks);
}
