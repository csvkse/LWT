using System.Diagnostics;
using System.Text;
using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Workflows.Contracts;
using LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Workflows.Adapters;

public sealed record ActivityExecutionContext(
    WorkflowStep Step,
    ExecutionTarget EffectiveTarget,
    string TriggerBy,
    string BatchId,
    int StepOrder,
    Guid WorkflowId,
    Dictionary<string, object?> PipelineContext
);

public sealed record ActivityResult(
    ExecutionStatus Status,
    int ExitCode,
    string Output,
    string ErrorOutput,
    long DurationMs,
    bool TimedOut,
    string? SkipReason = null
);

public interface IWorkflowActivity
{
    string ActivityType { get; }
    Task<ActivityResult> ExecuteAsync(ActivityExecutionContext context, CancellationToken cancellationToken = default);
}

public sealed class ShellCommandActivity(
    CommandStore commandStore,
    ExecutionStrategyResolver strategyResolver,
    ILogger<ShellCommandActivity> logger) : IWorkflowActivity
{
    public string ActivityType => "shell";

    public async Task<ActivityResult> ExecuteAsync(ActivityExecutionContext context, CancellationToken cancellationToken = default)
    {
        var step = context.Step;
        logger.LogDebug("执行工作流 Shell 步骤: {StepName}", step.Name);
        string commandText = step.CommandText ?? step.InlineScript ?? string.Empty;
        var scriptType = step.ScriptType;
        var timeoutSeconds = step.TimeoutSeconds ?? 60;

        if (step.CommandId.HasValue)
        {
            var cmd = await commandStore.GetByIdAsync(step.CommandId.Value);
            if (cmd is null)
            {
                return new ActivityResult(
                    Status: ExecutionStatus.Skipped,
                    ExitCode: -1,
                    Output: string.Empty,
                    ErrorOutput: $"[指令缺失] 步骤引用的指令 (ID: {step.CommandId}) 已被删除",
                    DurationMs: 0,
                    TimedOut: false,
                    SkipReason: "步骤引用的指令已不存在"
                );
            }
            commandText = cmd.CommandText;
            scriptType = cmd.ScriptType;
            if (step.TimeoutSeconds is null or <= 0)
            {
                timeoutSeconds = cmd.TimeoutSeconds is null or <= 0 ? 60 : cmd.TimeoutSeconds.Value;
            }
        }

        var executionContext = new CommandExecutionContext(
            CommandText: commandText,
            ScriptType: scriptType,
            Arguments: step.Arguments,
            TimeoutSeconds: timeoutSeconds,
            WorkingDirectory: null,
            Target: context.EffectiveTarget,
            TriggerBy: context.TriggerBy,
            CommandId: step.CommandId,
            ScheduleTaskId: null,
            BatchId: context.BatchId,
            StepOrder: context.StepOrder,
            WorkflowId: context.WorkflowId
        );

        var strategy = strategyResolver.Resolve(context.EffectiveTarget.TargetType);
        var res = await strategy.ExecuteAsync(executionContext, cancellationToken);

        return new ActivityResult(
            Status: res.Status,
            ExitCode: res.ExitCode ?? -1,
            Output: res.Output,
            ErrorOutput: res.ErrorOutput,
            DurationMs: res.DurationMs,
            TimedOut: res.TimedOut,
            SkipReason: res.SkipReason
        );
    }
}

public sealed class HttpWebhookActivity(
    ILogger<HttpWebhookActivity> logger) : IWorkflowActivity
{
    private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    });

    public string ActivityType => "http";

    public async Task<ActivityResult> ExecuteAsync(ActivityExecutionContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var step = context.Step;
        var paramsMap = step.Parameters ?? new Dictionary<string, string>();

        paramsMap.TryGetValue("url", out var url);
        if (string.IsNullOrWhiteSpace(url))
        {
            return new ActivityResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "HTTP Webhook 步骤缺少必填的 'url' 参数",
                DurationMs: 0,
                TimedOut: false
            );
        }

        paramsMap.TryGetValue("method", out var method);
        method = string.IsNullOrWhiteSpace(method) ? "POST" : method.ToUpperInvariant();

        paramsMap.TryGetValue("body", out var body);

        try
        {
            var timeoutSeconds = step.TimeoutSeconds is > 0 ? step.TimeoutSeconds.Value : 30;
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            var request = new HttpRequestMessage(new HttpMethod(method), url);
            if (!string.IsNullOrWhiteSpace(body) && (method == "POST" || method == "PUT" || method == "PATCH"))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            var response = await _httpClient.SendAsync(request, linkedCts.Token);
            var content = await response.Content.ReadAsStringAsync(linkedCts.Token);
            sw.Stop();

            var isSuccess = response.IsSuccessStatusCode;
            return new ActivityResult(
                Status: isSuccess ? ExecutionStatus.Success : ExecutionStatus.Failure,
                ExitCode: (int)response.StatusCode,
                Output: content,
                ErrorOutput: isSuccess ? string.Empty : $"HTTP 状态码: {(int)response.StatusCode} {response.ReasonPhrase}",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            return new ActivityResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "HTTP Webhook 请求被取消",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false
            );
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new ActivityResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "HTTP Webhook 请求超时",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: true
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "HTTP Webhook 步骤执行失败: {Url}", url);
            return new ActivityResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: $"HTTP Webhook 异常: {ex.Message}",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: ex is TimeoutException
            );
        }
    }
}

public sealed class ActivityRegistry
{
    private readonly Dictionary<string, IWorkflowActivity> _activities = new(StringComparer.OrdinalIgnoreCase);

    public ActivityRegistry(IEnumerable<IWorkflowActivity> activities)
    {
        foreach (var act in activities)
        {
            _activities[act.ActivityType] = act;
        }
    }

    public IWorkflowActivity? Get(string? activityType)
    {
        var key = string.IsNullOrWhiteSpace(activityType) ? "shell" : activityType;
        return _activities.TryGetValue(key, out var act) ? act : null;
    }
}
