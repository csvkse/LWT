using Quartz;

namespace LinuxWebTool.Infrastructure.Scheduling;

/// <summary>
/// 定时任务执行 Job：按任务配置读取已保存指令 → Shell 执行 → 写执行记录 → 回写最近/下次执行时间。
/// 通过 JobDataMap 的 taskId 关联任务（同一 Job 类型同时服务 Cron 触发与手动立即执行）。
/// </summary>
[DisallowConcurrentExecution]
public class ScheduledCommandJob(
    ScheduleStore scheduleStore,
    CommandStore commandStore,
    ExecutionStore executionStore,
    IShellExecutor shellExecutor,
    ILogger<ScheduledCommandJob> logger) : IJob
{
    public const string TaskIdKey = "taskId";

    public async Task Execute(IJobExecutionContext context)
    {
        var dataMap = context.MergedJobDataMap;
        if (!Guid.TryParse(dataMap.GetString(TaskIdKey), out var taskId))
        {
            logger.LogError("定时任务缺少有效的 taskId 参数：{JobKey}", context.JobDetail.Key);
            return;
        }

        var task = await scheduleStore.GetByIdAsync(taskId);
        if (task is null)
        {
            logger.LogWarning("定时任务不存在，跳过执行：{TaskId}", taskId);
            return;
        }

        var command = await commandStore.GetByIdAsync(task.CommandId);
        if (command is null)
        {
            logger.LogWarning("定时任务 {Task} 引用的指令不存在：{CommandId}", task.Name, task.CommandId);
            await executionStore.InsertAsync(new ExecutionRecord
            {
                Source = (int)ExecutionSource.Schedule,
                ScheduleTaskId = task.Id,
                CommandId = task.CommandId,
                CommandName = task.Name,
                CommandText = $"[指令已删除 {task.CommandId}]",
                Status = (int)ExecutionStatus.Failure,
                TriggerBy = "schedule",
                ErrorOutput = "任务引用的指令已被删除",
                StartTime = DateTime.Now,
                EndTime = DateTime.Now,
            });
            return;
        }

        var shellRequest = command.ScriptType == (int)ScriptType.BashScript
            ? new ShellRequest
            {
                ScriptText = command.CommandText,
                ScriptArguments = task.Arguments,
                TimeoutSeconds = task.TimeoutSeconds,
            }
            : new ShellRequest { CommandText = command.CommandText, TimeoutSeconds = task.TimeoutSeconds };
        var result = await shellExecutor.ExecuteAsync(shellRequest);

        await executionStore.InsertAsync(new ExecutionRecord
        {
            Source = (int)ExecutionSource.Schedule,
            ScheduleTaskId = task.Id,
            CommandId = command.Id,
            CommandName = command.Name,
            CommandText = command.CommandText,
            Status = (int)result.Status,
            ExitCode = result.ExitCode,
            Output = result.StandardOutput,
            ErrorOutput = result.ErrorOutput,
            DurationMs = result.DurationMs,
            TimedOut = result.TimedOut,
            Truncated = result.Truncated,
            TriggerBy = "schedule",
            StartTime = result.StartTime,
            EndTime = result.EndTime,
        });

        await scheduleStore.UpdateRunInfoAsync(task.Id, DateTime.Now, context.NextFireTimeUtc?.LocalDateTime);

        logger.LogInformation(
            "定时任务 {Task}（{Command}）执行完成：{Status}，耗时 {Duration}ms",
            task.Name, command.Name, result.Status, result.DurationMs);
    }
}
