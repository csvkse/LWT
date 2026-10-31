using System.Text.Json;
using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Shared.Contracts;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;
using Quartz;

namespace LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;

/// <summary>
/// 定时任务执行 Job：调度执行单机命令、远端主机分发命令或工作流管道。
/// 具备目标缺失与熔断保护自动跳过 (Skipped) 机制，防止调度器堵塞。
/// </summary>
[DisallowConcurrentExecution]
public class ScheduledCommandJob(
    ScheduleStore scheduleStore,
    CommandStore commandStore,
    ExecutionStore executionStore,
    RemoteHostStore remoteHostStore,
    HostCircuitBreaker circuitBreaker,
    IUnifiedExecutionEngine executionEngine,
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

        var isManual = context.JobDetail.Key.Group == "LinuxCommandOnce";
        var triggerBy = isManual ? "manual" : "schedule";
        var source = isManual ? ExecutionSource.Manual : ExecutionSource.Schedule;

        try
        {
            // 1. 若为工作流类型
            if (task.TargetBizType == (int)TargetBizType.Workflow && !string.IsNullOrWhiteSpace(task.WorkflowId) && Guid.TryParse(task.WorkflowId, out var wfGuid))
            {
                logger.LogInformation("定时任务触发工作流执行: Task={Task}, WorkflowId={WfId}", task.Name, task.WorkflowId);
                ExecutionTarget? targetOverride = null;
                if (task.TargetHostMode == (int)TargetHostMode.SingleRemote && !string.IsNullOrWhiteSpace(task.TargetHostId))
                {
                    targetOverride = ExecutionTarget.Remote(task.TargetHostId);
                }

                await executionEngine.ExecuteWorkflowAsync(wfGuid, triggerBy, targetOverride);
                await scheduleStore.UpdateRunInfoAsync(task.Id, DateTime.Now, isManual ? null : context.NextFireTimeUtc?.LocalDateTime);
                return;
            }

            // 2. 指令业务类型
            var command = await commandStore.GetByIdAsync(task.CommandId);
            if (command is null)
            {
                var now = DateTime.Now;
                var skipMsg = $"[指令缺失] 任务引用的指令已被删除 (ID: {task.CommandId})";
                logger.LogWarning("定时任务 {Task} 引用的指令不存在：{CommandId}", task.Name, task.CommandId);
                await executionStore.InsertAsync(new ExecutionRecord
                {
                    Source = (int)source,
                    ScheduleTaskId = task.Id,
                    CommandId = task.CommandId,
                    CommandName = task.Name,
                    CommandText = $"[指令已删除 {task.CommandId}]",
                    Status = (int)ExecutionStatus.Skipped,
                    TriggerBy = triggerBy,
                    ErrorOutput = skipMsg,
                    StartTime = now,
                    EndTime = now,
                    TargetType = (int)TargetType.Local
                });
                await scheduleStore.UpdateRunInfoAsync(task.Id, now, isManual ? null : context.NextFireTimeUtc?.LocalDateTime);
                return;
            }

            // 3. 目标主机分发模式判定
            if (task.TargetHostMode == (int)TargetHostMode.MultiRemote && !string.IsNullOrWhiteSpace(task.TargetHostIds))
            {
                await ExecuteMultiHostTaskAsync(task, command, source, triggerBy);
                await scheduleStore.UpdateRunInfoAsync(task.Id, DateTime.Now, isManual ? null : context.NextFireTimeUtc?.LocalDateTime);
                return;
            }

            // 单主机 (本地 或 单远端主机)
            ExecutionTarget target;
            if (task.TargetHostMode == (int)TargetHostMode.SingleRemote)
            {
                if (string.IsNullOrWhiteSpace(task.TargetHostId))
                {
                    var now = DateTime.Now;
                    await executionStore.InsertAsync(new ExecutionRecord
                    {
                        Source = (int)source,
                        ScheduleTaskId = task.Id,
                        CommandId = command.Id,
                        CommandName = command.Name,
                        CommandText = command.CommandText,
                        Status = (int)ExecutionStatus.Skipped,
                        TriggerBy = triggerBy,
                        ErrorOutput = "[配置错误] 单主机模式未选择目标主机",
                        StartTime = now,
                        EndTime = now,
                        TargetType = (int)TargetType.RemoteHost
                    });
                    await scheduleStore.UpdateRunInfoAsync(task.Id, now, isManual ? null : context.NextFireTimeUtc?.LocalDateTime);
                    return;
                }

                var remoteHost = await remoteHostStore.GetByIdAsync(task.TargetHostId);
                if (remoteHost is null)
                {
                    var now = DateTime.Now;
                    var skipMsg = $"[目标缺失] 任务关联的主机 (ID: {task.TargetHostId}) 已被删除，任务已安全跳过";
                    logger.LogWarning("定时任务 {Task} 绑定的远程主机已被删除，已跳过执行以防调度器阻塞", task.Name);
                    await executionStore.InsertAsync(new ExecutionRecord
                    {
                        Source = (int)source,
                        ScheduleTaskId = task.Id,
                        CommandId = command.Id,
                        CommandName = command.Name,
                        CommandText = command.CommandText,
                        Status = (int)ExecutionStatus.Skipped,
                        TriggerBy = triggerBy,
                        ErrorOutput = skipMsg,
                        StartTime = now,
                        EndTime = now,
                        TargetType = (int)TargetType.RemoteHost,
                        TargetHostId = task.TargetHostId,
                        TargetHostName = "已删除主机"
                    });
                    await scheduleStore.UpdateRunInfoAsync(task.Id, now, isManual ? null : context.NextFireTimeUtc?.LocalDateTime);
                    return;
                }

                if (!circuitBreaker.AllowExecution(task.TargetHostId, out var openReason))
                {
                    var now = DateTime.Now;
                    var skipMsg = $"[熔断防护] {openReason}";
                    logger.LogWarning("定时任务 {Task} 绑定的远程主机处于熔断隔离期，已跳过执行", task.Name);
                    await executionStore.InsertAsync(new ExecutionRecord
                    {
                        Source = (int)source,
                        ScheduleTaskId = task.Id,
                        CommandId = command.Id,
                        CommandName = command.Name,
                        CommandText = command.CommandText,
                        Status = (int)ExecutionStatus.Skipped,
                        TriggerBy = triggerBy,
                        ErrorOutput = skipMsg,
                        StartTime = now,
                        EndTime = now,
                        TargetType = (int)TargetType.RemoteHost,
                        TargetHostId = task.TargetHostId,
                        TargetHostName = remoteHost.Name
                    });
                    await scheduleStore.UpdateRunInfoAsync(task.Id, now, isManual ? null : context.NextFireTimeUtc?.LocalDateTime);
                    return;
                }

                target = ExecutionTarget.Remote(task.TargetHostId);
            }
            else
            {
                target = ExecutionTarget.Local;
            }

            var timeoutSeconds = task.TimeoutSeconds ?? (command.TimeoutSeconds is null or <= 0 ? 60 : command.TimeoutSeconds.Value);
            var cmdContext = new CommandExecutionContext(
                CommandText: command.CommandText,
                ScriptType: command.ScriptType,
                Arguments: task.Arguments,
                TimeoutSeconds: timeoutSeconds,
                WorkingDirectory: null,
                Target: target,
                TriggerBy: triggerBy,
                CommandId: command.Id,
                ScheduleTaskId: task.Id,
                ExecutionTarget: !string.IsNullOrWhiteSpace(task.ExecutionTarget) ? task.ExecutionTarget : (command.ExecutionTarget ?? "container")
            );

            var result = await executionEngine.ExecuteCommandAsync(cmdContext, command.Name);

            await scheduleStore.UpdateRunInfoAsync(task.Id, DateTime.Now, isManual ? null : context.NextFireTimeUtc?.LocalDateTime);

            logger.LogInformation(
                "定时任务 {Task}（{Command}）执行完成：{Status}，耗时 {Duration}ms",
                task.Name, command.Name, result.Status, result.DurationMs);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "定时任务 {Task} 执行未捕获异常", task.Name);
            await scheduleStore.UpdateRunInfoAsync(task.Id, DateTime.Now, isManual ? null : context.NextFireTimeUtc?.LocalDateTime);
        }
    }

    private async Task ExecuteMultiHostTaskAsync(
        ScheduleTask task, 
        LinuxCommand command, 
        ExecutionSource source, 
        string triggerBy)
    {
        List<string> hostIds = [];
        try
        {
            if (task.TargetHostIds!.TrimStart().StartsWith("["))
            {
                hostIds = JsonSerializer.Deserialize(task.TargetHostIds, WorkflowJsonContext.Default.ListString) ?? [];
            }
            else
            {
                hostIds = task.TargetHostIds.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "解析 TargetHostIds 失败: {Json}", task.TargetHostIds);
            return;
        }

        var batchId = $"sched-multi-{Guid.NewGuid():N}";
        int stepOrder = 0;
        var timeoutSeconds = task.TimeoutSeconds ?? (command.TimeoutSeconds is null or <= 0 ? 60 : command.TimeoutSeconds.Value);

        foreach (var hostId in hostIds)
        {
            stepOrder++;
            var target = ExecutionTarget.Remote(hostId);
            var cmdContext = new CommandExecutionContext(
                CommandText: command.CommandText,
                ScriptType: command.ScriptType,
                Arguments: task.Arguments,
                TimeoutSeconds: timeoutSeconds,
                WorkingDirectory: null,
                Target: target,
                TriggerBy: triggerBy,
                CommandId: command.Id,
                ScheduleTaskId: task.Id,
                ExecutionTarget: !string.IsNullOrWhiteSpace(task.ExecutionTarget) ? task.ExecutionTarget : (command.ExecutionTarget ?? "container"),
                BatchId: batchId,
                StepOrder: stepOrder
            );

            var res = await executionEngine.ExecuteCommandAsync(cmdContext, command.Name);
            if (res.Status == ExecutionStatus.Failure && task.FailurePolicy == (int)FailurePolicy.FailFast)
            {
                logger.LogWarning("定时任务多主机分发在主机 {HostId} 失败，按 FailFast 策略中止后续主机分发", hostId);
                break;
            }
        }
    }
}
