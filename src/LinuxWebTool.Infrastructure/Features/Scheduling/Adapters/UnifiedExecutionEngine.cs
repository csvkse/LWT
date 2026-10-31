using System.Diagnostics;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Workflows.Contracts;
using LinuxWebTool.Contracts.Shared.Contracts;
using LinuxWebTool.Infrastructure.Features.Workflows.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;

public interface IUnifiedExecutionEngine
{
    Task<StandardExecutionResult> ExecuteCommandAsync(CommandExecutionContext context, string? commandName = null, CancellationToken cancellationToken = default);
    Task<BatchExecutionResponse> ExecuteBatchAsync(BatchExecutionRequest request, string triggerBy, CancellationToken cancellationToken = default);
    Task<BatchExecutionResponse> ExecuteWorkflowAsync(Guid workflowId, string triggerBy, ExecutionTarget? targetOverride = null, CancellationToken cancellationToken = default);
}

public sealed class UnifiedExecutionEngine(
    ExecutionStrategyResolver strategyResolver,
    ExecutionStore executionStore,
    CommandStore commandStore,
    RemoteHostStore hostStore,
    WorkflowStore workflowStore,
    ActivityRegistry activityRegistry,
    ILogger<UnifiedExecutionEngine> logger) : IUnifiedExecutionEngine
{
    public async Task<StandardExecutionResult> ExecuteCommandAsync(
        CommandExecutionContext context, 
        string? commandName = null, 
        CancellationToken cancellationToken = default)
    {
        string? targetHostName = null;
        if (context.Target.TargetType == TargetType.RemoteHost && !string.IsNullOrWhiteSpace(context.Target.HostId))
        {
            var host = await hostStore.GetByIdAsync(context.Target.HostId);
            targetHostName = host != null ? (string.IsNullOrWhiteSpace(host.Name) ? host.Host : host.Name) : "已删除主机";
        }

        var strategy = strategyResolver.Resolve(context.Target.TargetType);
        var result = await strategy.ExecuteAsync(context, cancellationToken);

        var nameToRecord = commandName;
        if (string.IsNullOrWhiteSpace(nameToRecord) && context.CommandId.HasValue)
        {
            var cmd = await commandStore.GetByIdAsync(context.CommandId.Value);
            nameToRecord = cmd?.Name;
        }
        if (string.IsNullOrWhiteSpace(nameToRecord))
        {
            nameToRecord = context.CommandText.Length > 30 ? context.CommandText[..30] + "..." : context.CommandText;
        }

        var record = new ExecutionRecord
        {
            Source = context.ScheduleTaskId.HasValue ? (int)ExecutionSource.Schedule : (int)ExecutionSource.Manual,
            CommandId = context.CommandId,
            ScheduleTaskId = context.ScheduleTaskId,
            CommandName = nameToRecord,
            CommandText = context.CommandText,
            Status = (int)result.Status,
            ExitCode = result.ExitCode,
            Output = result.Output,
            ErrorOutput = result.ErrorOutput,
            DurationMs = result.DurationMs,
            TimedOut = result.TimedOut,
            Truncated = result.Truncated,
            TriggerBy = context.TriggerBy ?? "manual",
            StartTime = result.StartTime,
            EndTime = result.EndTime,
            TargetType = (int)context.Target.TargetType,
            TargetHostId = context.Target.HostId,
            TargetHostName = targetHostName,
            BatchId = context.BatchId,
            StepOrder = context.StepOrder,
            WorkflowId = context.WorkflowId?.ToString()
        };

        await executionStore.InsertAsync(record);

        if (context.CommandId.HasValue && result.Status != ExecutionStatus.Skipped)
        {
            await commandStore.UpdateLastExecTimeAsync(context.CommandId.Value);
        }

        return result;
    }

    public async Task<BatchExecutionResponse> ExecuteBatchAsync(
        BatchExecutionRequest request, 
        string triggerBy, 
        CancellationToken cancellationToken = default)
    {
        var batchId = $"batch-{Guid.NewGuid():N}";
        var sw = Stopwatch.StartNew();
        var results = new List<ExecuteResult>();

        var defaultTarget = !string.IsNullOrWhiteSpace(request.TargetHostId)
            ? ExecutionTarget.Remote(request.TargetHostId)
            : ExecutionTarget.Local;

        // 若传入的是 CommandIds 列表，则转换为 Commands 列表
        var commandsToRun = new List<BatchExecutionItem>(request.Commands);
        if (commandsToRun.Count == 0 && request.CommandIds.Count > 0)
        {
            foreach (var cmdId in request.CommandIds)
            {
                var cmd = await commandStore.GetByIdAsync(cmdId);
                if (cmd != null)
                {
                    var hostId = request.RespectIndividualHost ? cmd.DefaultTargetHostId : request.TargetHostId;
                    commandsToRun.Add(new BatchExecutionItem(
                        CommandId: cmd.Id,
                        CommandText: cmd.CommandText,
                        ScriptType: cmd.ScriptType,
                        Arguments: null,
                        TimeoutSeconds: (cmd.TimeoutSeconds is null or <= 0 ? 60 : cmd.TimeoutSeconds.Value),
                        TargetHostId: hostId,
                        Name: cmd.Name
                    ));
                }
            }
        }

        bool hasFailure = false;

        if (request.ExecutionMode == ExecutionMode.Parallel)
        {
            var semaphore = new SemaphoreSlim(Math.Clamp(request.MaxConcurrency, 1, 20));
            var tasks = commandsToRun.Select(async (item, index) =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var target = !string.IsNullOrWhiteSpace(item.TargetHostId)
                        ? ExecutionTarget.Remote(item.TargetHostId)
                        : defaultTarget;

                    var context = new CommandExecutionContext(
                        CommandText: item.CommandText,
                        ScriptType: item.ScriptType,
                        Arguments: item.Arguments,
                        TimeoutSeconds: item.TimeoutSeconds,
                        WorkingDirectory: null,
                        Target: target,
                        TriggerBy: triggerBy,
                        CommandId: item.CommandId,
                        ScheduleTaskId: null,
                        BatchId: batchId,
                        StepOrder: index + 1
                    );

                    var res = await ExecuteCommandAsync(context, item.Name, cancellationToken);
                    return MapToExecuteResult(res, target, batchId, index + 1);
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var executedResults = await Task.WhenAll(tasks);
            results.AddRange(executedResults.OrderBy(r => r.StepOrder));
        }
        else
        {
            for (int i = 0; i < commandsToRun.Count; i++)
            {
                var item = commandsToRun[i];
                var stepOrder = i + 1;

                if (hasFailure && request.FailurePolicy == FailurePolicy.FailFast)
                {
                    var now = DateTime.Now;
                    var skipReason = "前序命令执行失败，触发快速失败 (FailFast) 熔断策略，已跳过";
                    var target = !string.IsNullOrWhiteSpace(item.TargetHostId)
                        ? ExecutionTarget.Remote(item.TargetHostId)
                        : defaultTarget;

                    var skippedRecord = new ExecutionRecord
                    {
                        Source = (int)ExecutionSource.Manual,
                        CommandId = item.CommandId,
                        CommandName = item.Name ?? item.CommandText,
                        CommandText = item.CommandText,
                        Status = (int)ExecutionStatus.Skipped,
                        ExitCode = -1,
                        Output = string.Empty,
                        ErrorOutput = skipReason,
                        DurationMs = 0,
                        TimedOut = false,
                        Truncated = false,
                        TriggerBy = triggerBy,
                        StartTime = now,
                        EndTime = now,
                        TargetType = (int)target.TargetType,
                        TargetHostId = target.HostId,
                        BatchId = batchId,
                        StepOrder = stepOrder
                    };
                    await executionStore.InsertAsync(skippedRecord);

                    results.Add(new ExecuteResult
                    {
                        Status = ExecutionStatus.Skipped,
                        ExitCode = -1,
                        StandardOutput = string.Empty,
                        ErrorOutput = skipReason,
                        DurationMs = 0,
                        TimedOut = false,
                        Truncated = false,
                        StartTime = now,
                        EndTime = now,
                        TargetType = (int)target.TargetType,
                        TargetHostId = target.HostId,
                        BatchId = batchId,
                        StepOrder = stepOrder,
                        SkipReason = skipReason
                    });
                    continue;
                }

                var activeTarget = !string.IsNullOrWhiteSpace(item.TargetHostId)
                    ? ExecutionTarget.Remote(item.TargetHostId)
                    : defaultTarget;

                var cmdContext = new CommandExecutionContext(
                    CommandText: item.CommandText,
                    ScriptType: item.ScriptType,
                    Arguments: item.Arguments,
                    TimeoutSeconds: item.TimeoutSeconds,
                    WorkingDirectory: null,
                    Target: activeTarget,
                    TriggerBy: triggerBy,
                    CommandId: item.CommandId,
                    ScheduleTaskId: null,
                    BatchId: batchId,
                    StepOrder: stepOrder
                );

                var stepRes = await ExecuteCommandAsync(cmdContext, item.Name, cancellationToken);
                results.Add(MapToExecuteResult(stepRes, activeTarget, batchId, stepOrder));

                if (stepRes.Status == ExecutionStatus.Failure)
                {
                    hasFailure = true;
                }
            }
        }

        sw.Stop();
        var overallStatus = results.Any(r => r.Status == ExecutionStatus.Paused)
            ? ExecutionStatus.Paused
            : (results.Any(r => r.Status == ExecutionStatus.Failure)
            ? ExecutionStatus.Failure
            : (results.All(r => r.Status == ExecutionStatus.Skipped) ? ExecutionStatus.Skipped : ExecutionStatus.Success));

        return new BatchExecutionResponse(batchId, results, overallStatus, sw.ElapsedMilliseconds);
    }

    public async Task<BatchExecutionResponse> ExecuteWorkflowAsync(
        Guid workflowId, 
        string triggerBy, 
        ExecutionTarget? targetOverride = null, 
        CancellationToken cancellationToken = default)
    {
        var workflow = await workflowStore.GetByIdAsync(workflowId);
        if (workflow is null)
        {
            throw new InvalidOperationException($"工作流不存在: {workflowId}");
        }

        var batchId = $"wf-{Guid.NewGuid():N}";
        var sw = Stopwatch.StartNew();
        var results = new List<ExecuteResult>();

        WorkflowDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize(workflow.DefinitionJson, WorkflowJsonContext.Default.WorkflowDefinition);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "工作流定义解析失败: {WorkflowId}", workflowId);
            throw new InvalidOperationException($"工作流定义 JSON 格式错误: {ex.Message}");
        }

        var defaultTarget = targetOverride ?? (!string.IsNullOrWhiteSpace(workflow.DefaultTargetHostId)
            ? ExecutionTarget.Remote(workflow.DefaultTargetHostId)
            : ExecutionTarget.Local);

        var pipelineContext = new Dictionary<string, object?>();
        bool abortPipeline = false;
        int globalStepOrder = 0;

        foreach (var stage in definition?.Stages ?? Enumerable.Empty<WorkflowStage>())
        {
            var stageMode = stage.ExecutionMode;
            if (stageMode == ExecutionMode.Parallel)
            {
                if (abortPipeline && (stage.FailurePolicy == FailurePolicy.FailFast || (FailurePolicy)workflow.FailurePolicy == FailurePolicy.FailFast))
                {
                    foreach (var step in stage.Steps ?? Enumerable.Empty<WorkflowStep>())
                    {
                        var order = Interlocked.Increment(ref globalStepOrder);
                        var now = DateTime.Now;
                        var isPaused = results.Any(r => r.Status == ExecutionStatus.Paused);
                        var skipReason = isPaused ? "工作流步骤执行异常，已触发安全自动暂停 (Auto-Paused)，保留现场等待人工恢复" : "前序工作流步骤失败，已熔断跳过后续步骤";
                        var effectiveTarget = !string.IsNullOrWhiteSpace(step.TargetHostId)
                            ? ExecutionTarget.Remote(step.TargetHostId)
                            : defaultTarget;

                        var skippedRecord = new ExecutionRecord
                        {
                            Source = (int)ExecutionSource.Manual,
                            CommandName = step.Name,
                            CommandText = step.CommandText ?? step.InlineScript ?? $"[Activity: {step.ActivityType ?? step.StepType}]",
                            Status = (int)ExecutionStatus.Skipped,
                            ExitCode = -1,
                            Output = string.Empty,
                            ErrorOutput = skipReason,
                            DurationMs = 0,
                            TimedOut = false,
                            Truncated = false,
                            TriggerBy = triggerBy,
                            StartTime = now,
                            EndTime = now,
                            TargetType = (int)effectiveTarget.TargetType,
                            TargetHostId = effectiveTarget.HostId,
                            BatchId = batchId,
                            StepOrder = order,
                            WorkflowId = workflowId.ToString()
                        };
                        await executionStore.InsertAsync(skippedRecord);

                        results.Add(new ExecuteResult
                        {
                            Status = ExecutionStatus.Skipped,
                            ExitCode = -1,
                            StandardOutput = string.Empty,
                            ErrorOutput = skipReason,
                            DurationMs = 0,
                            TimedOut = false,
                            Truncated = false,
                            StartTime = now,
                            EndTime = now,
                            TargetType = (int)effectiveTarget.TargetType,
                            TargetHostId = effectiveTarget.HostId,
                            BatchId = batchId,
                            StepOrder = order,
                            SkipReason = skipReason,
                            CommandName = step.Name
                        });
                    }
                    continue;
                }

                var tasks = (stage.Steps ?? Enumerable.Empty<WorkflowStep>()).Select(async step =>
                {
                    var order = Interlocked.Increment(ref globalStepOrder);
                    var effectiveTarget = !string.IsNullOrWhiteSpace(step.TargetHostId)
                        ? ExecutionTarget.Remote(step.TargetHostId)
                        : defaultTarget;

                    return await ExecuteWorkflowStepAsync(step, effectiveTarget, triggerBy, batchId, order, workflowId, pipelineContext, cancellationToken);
                });

                var stageResults = await Task.WhenAll(tasks);
                results.AddRange(stageResults.OrderBy(r => r.StepOrder));

                if (stageResults.Any(r => r.Status == ExecutionStatus.Failure || r.Status == ExecutionStatus.Paused))
                {
                    abortPipeline = true;
                }
            }
            else
            {
                foreach (var step in stage.Steps ?? Enumerable.Empty<WorkflowStep>())
                {
                    globalStepOrder++;
                    if (abortPipeline && (stage.FailurePolicy == FailurePolicy.FailFast || (FailurePolicy)workflow.FailurePolicy == FailurePolicy.FailFast))
                    {
                        var now = DateTime.Now;
                        var isPaused = results.Any(r => r.Status == ExecutionStatus.Paused);
                        var skipReason = isPaused ? "工作流步骤执行异常，已触发安全自动暂停 (Auto-Paused)，保留现场等待人工恢复" : "前序工作流步骤失败，已熔断跳过后续步骤";
                        var effectiveTarget = !string.IsNullOrWhiteSpace(step.TargetHostId)
                            ? ExecutionTarget.Remote(step.TargetHostId)
                            : defaultTarget;

                        var skippedRecord = new ExecutionRecord
                        {
                            Source = (int)ExecutionSource.Manual,
                            CommandName = step.Name,
                            CommandText = step.CommandText ?? step.InlineScript ?? $"[Activity: {step.ActivityType ?? step.StepType}]",
                            Status = (int)ExecutionStatus.Skipped,
                            ExitCode = -1,
                            Output = string.Empty,
                            ErrorOutput = skipReason,
                            DurationMs = 0,
                            TimedOut = false,
                            Truncated = false,
                            TriggerBy = triggerBy,
                            StartTime = now,
                            EndTime = now,
                            TargetType = (int)effectiveTarget.TargetType,
                            TargetHostId = effectiveTarget.HostId,
                            BatchId = batchId,
                            StepOrder = globalStepOrder,
                            WorkflowId = workflowId.ToString()
                        };
                        await executionStore.InsertAsync(skippedRecord);

                        results.Add(new ExecuteResult
                        {
                            Status = ExecutionStatus.Skipped,
                            ExitCode = -1,
                            StandardOutput = string.Empty,
                            ErrorOutput = skipReason,
                            DurationMs = 0,
                            TimedOut = false,
                            Truncated = false,
                            StartTime = now,
                            EndTime = now,
                            TargetType = (int)effectiveTarget.TargetType,
                            TargetHostId = effectiveTarget.HostId,
                            BatchId = batchId,
                            StepOrder = globalStepOrder,
                            SkipReason = skipReason
                        });
                        continue;
                    }

                    var stepTarget = !string.IsNullOrWhiteSpace(step.TargetHostId)
                        ? ExecutionTarget.Remote(step.TargetHostId)
                        : defaultTarget;

                    var stepRes = await ExecuteWorkflowStepAsync(step, stepTarget, triggerBy, batchId, globalStepOrder, workflowId, pipelineContext, cancellationToken);
                    results.Add(stepRes);

                    if (stepRes.Status == ExecutionStatus.Failure || stepRes.Status == ExecutionStatus.Paused)
                    {
                        abortPipeline = true;
                    }
                }
            }
        }

        sw.Stop();
        await workflowStore.UpdateLastRunTimeAsync(workflowId);

        var overallStatus = results.Any(r => r.Status == ExecutionStatus.Paused)
            ? ExecutionStatus.Paused
            : (results.Any(r => r.Status == ExecutionStatus.Failure)
            ? ExecutionStatus.Failure
            : (results.All(r => r.Status == ExecutionStatus.Skipped) ? ExecutionStatus.Skipped : ExecutionStatus.Success));

        return new BatchExecutionResponse(batchId, results, overallStatus, sw.ElapsedMilliseconds);
    }

    private async Task<ExecuteResult> ExecuteWorkflowStepAsync(
        WorkflowStep step,
        ExecutionTarget target,
        string triggerBy,
        string batchId,
        int stepOrder,
        Guid workflowId,
        Dictionary<string, object?> pipelineContext,
        CancellationToken cancellationToken)
    {
        var actType = step.ActivityType ?? (step.StepType == "Http" ? "http" : "shell");
        var activity = activityRegistry.Get(actType);
        if (activity is null)
        {
            var now = DateTime.Now;
            var err = $"未知的 ActivityType: '{actType}'";
            return new ExecuteResult
            {
                Status = ExecutionStatus.Failure,
                ExitCode = -1,
                StandardOutput = string.Empty,
                ErrorOutput = err,
                DurationMs = 0,
                TimedOut = false,
                Truncated = false,
                StartTime = now,
                EndTime = now,
                TargetType = (int)target.TargetType,
                TargetHostId = target.HostId,
                BatchId = batchId,
                StepOrder = stepOrder
            };
        }

        var actContext = new ActivityExecutionContext(
            Step: step,
            EffectiveTarget: target,
            TriggerBy: triggerBy,
            BatchId: batchId,
            StepOrder: stepOrder,
            WorkflowId: workflowId,
            PipelineContext: pipelineContext
        );

        var startTime = DateTime.Now;
        var actResult = await activity.ExecuteAsync(actContext, cancellationToken);
        var endTime = DateTime.Now;

        string? targetHostName = null;
        if (target.TargetType == TargetType.RemoteHost && !string.IsNullOrWhiteSpace(target.HostId))
        {
            var host = await hostStore.GetByIdAsync(target.HostId);
            targetHostName = host != null ? (string.IsNullOrWhiteSpace(host.Name) ? host.Host : host.Name) : "已删除主机";
        }

        var record = new ExecutionRecord
        {
            Source = (int)ExecutionSource.Manual,
            CommandId = step.CommandId,
            CommandName = step.Name,
            CommandText = step.CommandText ?? step.InlineScript ?? $"[{actType}]",
            Status = (int)actResult.Status,
            ExitCode = actResult.ExitCode,
            Output = actResult.Output,
            ErrorOutput = actResult.ErrorOutput,
            DurationMs = actResult.DurationMs,
            TimedOut = actResult.TimedOut,
            Truncated = false,
            TriggerBy = triggerBy,
            StartTime = startTime,
            EndTime = endTime,
            TargetType = (int)target.TargetType,
            TargetHostId = target.HostId,
            TargetHostName = targetHostName,
            BatchId = batchId,
            StepOrder = stepOrder,
            WorkflowId = workflowId.ToString()
        };

        await executionStore.InsertAsync(record);

        return new ExecuteResult
        {
            Status = actResult.Status,
            ExitCode = actResult.ExitCode,
            StandardOutput = actResult.Output,
            ErrorOutput = actResult.ErrorOutput,
            DurationMs = actResult.DurationMs,
            TimedOut = actResult.TimedOut,
            Truncated = false,
            StartTime = startTime,
            EndTime = endTime,
            TargetType = (int)target.TargetType,
            TargetHostId = target.HostId,
            TargetHostName = targetHostName,
            BatchId = batchId,
            StepOrder = stepOrder,
            SkipReason = actResult.SkipReason
        };
    }

    private static ExecuteResult MapToExecuteResult(StandardExecutionResult res, ExecutionTarget target, string batchId, int stepOrder)
    {
        return new ExecuteResult
        {
            Status = res.Status,
            ExitCode = res.ExitCode,
            StandardOutput = res.Output,
            ErrorOutput = res.ErrorOutput,
            DurationMs = res.DurationMs,
            TimedOut = res.TimedOut,
            Truncated = res.Truncated,
            StartTime = res.StartTime,
            EndTime = res.EndTime,
            TargetType = (int)target.TargetType,
            TargetHostId = target.HostId,
            BatchId = batchId,
            StepOrder = stepOrder,
            SkipReason = res.SkipReason
        };
    }
}
