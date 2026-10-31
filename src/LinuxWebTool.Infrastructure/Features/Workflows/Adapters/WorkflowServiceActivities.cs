using System.Diagnostics;
using System.Text;
using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Contracts.Features.Workflows.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Workflows.Adapters;

/// <summary>
/// 工作流运维扩展服务活动 (Activity: extension)
/// </summary>
public sealed class ExtensionActionActivity(
    IScriptExtensionManager extensionManager,
    ILogger<ExtensionActionActivity> logger) : IWorkflowActivity
{
    public string ActivityType => "extension";

    public async Task<ActivityResult> ExecuteAsync(ActivityExecutionContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var step = context.Step;
        var p = step.Parameters ?? new Dictionary<string, string>();

        p.TryGetValue("extensionId", out var extId);
        if (string.IsNullOrWhiteSpace(extId)) p.TryGetValue("extension_id", out extId);

        if (string.IsNullOrWhiteSpace(extId))
        {
            return new ActivityResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "扩展服务步骤缺少必填的 'extensionId' 参数",
                DurationMs: 0,
                TimedOut: false
            );
        }

        p.TryGetValue("action", out var action);
        if (string.IsNullOrWhiteSpace(action)) action = "status";

        var actionParams = new Dictionary<string, string>(p, StringComparer.OrdinalIgnoreCase);
        actionParams.Remove("extensionId");
        actionParams.Remove("extension_id");
        actionParams.Remove("action");

        try
        {
            logger.LogInformation("工作流执行扩展动作: [{ExtensionId}] {Action}", extId, action);
            var result = await extensionManager.ExecuteExtensionActionAsync(new ExtensionActionRequest
            {
                ExtensionId = extId,
                Action = action,
                ActionParams = actionParams
            }, cancellationToken);

            sw.Stop();
            var isSuccess = result.Success;

            // 检查步骤或自身指定的失败挂起策略
            var failureStatus = IsPausePolicy(step.FailurePolicy) ? ExecutionStatus.Paused : ExecutionStatus.Failure;

            return new ActivityResult(
                Status: isSuccess ? ExecutionStatus.Success : failureStatus,
                ExitCode: isSuccess ? 0 : 1,
                Output: isSuccess ? (result.Message ?? "扩展动作执行成功") : string.Empty,
                ErrorOutput: isSuccess ? string.Empty : (result.Message ?? "扩展动作执行失败"),
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "工作流执行扩展动作异常: [{ExtensionId}] {Action}", extId, action);
            var failureStatus = IsPausePolicy(step.FailurePolicy) ? ExecutionStatus.Paused : ExecutionStatus.Failure;
            return new ActivityResult(
                Status: failureStatus,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: $"扩展服务异常: {ex.Message}",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false
            );
        }
    }

    private static bool IsPausePolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy)) return true; // 扩展节点默认故障自动暂停保护
        return policy.Equals("PauseOnFailure", StringComparison.OrdinalIgnoreCase) ||
               policy.Equals("Pause", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 工作流 SaaS 函数微服务与常驻守护活动 (Activity: worker)
/// </summary>
public sealed class WorkerServiceActivity(
    IWorkersManager workersManager,
    ILogger<WorkerServiceActivity> logger) : IWorkflowActivity
{
    private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(15),
        ConnectTimeout = TimeSpan.FromSeconds(10)
    });

    public string ActivityType => "worker";

    public async Task<ActivityResult> ExecuteAsync(ActivityExecutionContext context, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var step = context.Step;
        var p = step.Parameters ?? new Dictionary<string, string>();

        p.TryGetValue("workerId", out var workerId);
        if (string.IsNullOrWhiteSpace(workerId)) p.TryGetValue("worker_id", out workerId);

        if (string.IsNullOrWhiteSpace(workerId))
        {
            return new ActivityResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "Worker 步骤缺少必填的 'workerId' 参数",
                DurationMs: 0,
                TimedOut: false
            );
        }

        p.TryGetValue("operation", out var operation);
        if (string.IsNullOrWhiteSpace(operation)) operation = "invoke";

        var failureStatus = IsPausePolicy(step.FailurePolicy) ? ExecutionStatus.Paused : ExecutionStatus.Failure;

        try
        {
            logger.LogInformation("工作流执行 Worker 操作: [{WorkerId}] {Op}", workerId, operation);

            switch (operation.ToLowerInvariant())
            {
                case "start":
                {
                    var ok = await workersManager.StartAsync(workerId, cancellationToken);
                    sw.Stop();
                    return new ActivityResult(
                        Status: ok ? ExecutionStatus.Success : failureStatus,
                        ExitCode: ok ? 0 : 1,
                        Output: ok ? $"Worker '{workerId}' 启动成功" : string.Empty,
                        ErrorOutput: ok ? string.Empty : $"Worker '{workerId}' 启动失败",
                        DurationMs: sw.ElapsedMilliseconds,
                        TimedOut: false);
                }

                case "stop":
                {
                    var ok = await workersManager.StopAsync(workerId, cancellationToken);
                    sw.Stop();
                    return new ActivityResult(
                        Status: ok ? ExecutionStatus.Success : failureStatus,
                        ExitCode: ok ? 0 : 1,
                        Output: ok ? $"Worker '{workerId}' 已停止" : string.Empty,
                        ErrorOutput: ok ? string.Empty : $"Worker '{workerId}' 停止失败",
                        DurationMs: sw.ElapsedMilliseconds,
                        TimedOut: false);
                }

                case "restart":
                {
                    var ok = await workersManager.RestartAsync(workerId, cancellationToken);
                    sw.Stop();
                    return new ActivityResult(
                        Status: ok ? ExecutionStatus.Success : failureStatus,
                        ExitCode: ok ? 0 : 1,
                        Output: ok ? $"Worker '{workerId}' 重启成功" : string.Empty,
                        ErrorOutput: ok ? string.Empty : $"Worker '{workerId}' 重启失败",
                        DurationMs: sw.ElapsedMilliseconds,
                        TimedOut: false);
                }

                case "invoke":
                default:
                {
                    var worker = await workersManager.GetByIdAsync(workerId, cancellationToken);
                    if (worker == null)
                    {
                        sw.Stop();
                        return new ActivityResult(
                            Status: failureStatus,
                            ExitCode: -1,
                            Output: string.Empty,
                            ErrorOutput: $"未找到 Worker '{workerId}'",
                            DurationMs: sw.ElapsedMilliseconds,
                            TimedOut: false);
                    }

                    if (!worker.IsEnabled)
                    {
                        sw.Stop();
                        return new ActivityResult(
                            Status: failureStatus,
                            ExitCode: -1,
                            Output: string.Empty,
                            ErrorOutput: $"Worker '{workerId}' 已被显式禁用，工作流无法调用",
                            DurationMs: sw.ElapsedMilliseconds,
                            TimedOut: false);
                    }

                    if (worker.State != WorkerState.Running || !worker.BoundPort.HasValue)
                    {
                        // 尝试自动拉起
                        var startOk = await workersManager.StartAsync(workerId, cancellationToken);
                        if (!startOk)
                        {
                            sw.Stop();
                            return new ActivityResult(
                                Status: failureStatus,
                                ExitCode: -1,
                                Output: string.Empty,
                                ErrorOutput: $"Worker '{workerId}' 未在运行且自动拉起失败",
                                DurationMs: sw.ElapsedMilliseconds,
                                TimedOut: false);
                        }
                        worker = await workersManager.GetByIdAsync(workerId, cancellationToken);
                    }

                    p.TryGetValue("subPath", out var subPath);
                    if (string.IsNullOrWhiteSpace(subPath)) p.TryGetValue("sub_path", out subPath);
                    subPath = subPath?.TrimStart('/') ?? "";

                    p.TryGetValue("method", out var method);
                    method = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();

                    p.TryGetValue("body", out var body);

                    var client = _httpClient;
                    var uri = $"http://127.0.0.1:{worker!.BoundPort!.Value}/{subPath}";
                    using var req = new HttpRequestMessage(new HttpMethod(method), uri);
                    if (!string.IsNullOrWhiteSpace(body) && (method == "POST" || method == "PUT" || method == "PATCH"))
                    {
                        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    }

                    var resp = await client.SendAsync(req, cancellationToken);
                    var content = await resp.Content.ReadAsStringAsync(cancellationToken);
                    sw.Stop();

                    var isHttpOk = resp.IsSuccessStatusCode;
                    return new ActivityResult(
                        Status: isHttpOk ? ExecutionStatus.Success : failureStatus,
                        ExitCode: (int)resp.StatusCode,
                        Output: content,
                        ErrorOutput: isHttpOk ? string.Empty : $"HTTP 状态码: {(int)resp.StatusCode} {resp.ReasonPhrase}",
                        DurationMs: sw.ElapsedMilliseconds,
                        TimedOut: false);
                }
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "工作流执行 Worker 操作异常: [{WorkerId}] {Op}", workerId, operation);
            return new ActivityResult(
                Status: failureStatus,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: $"Worker 异常: {ex.Message}",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false);
        }
    }

    private static bool IsPausePolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy)) return true; // Worker 节点默认故障自动暂停保护
        return policy.Equals("PauseOnFailure", StringComparison.OrdinalIgnoreCase) ||
               policy.Equals("Pause", StringComparison.OrdinalIgnoreCase);
    }
}
