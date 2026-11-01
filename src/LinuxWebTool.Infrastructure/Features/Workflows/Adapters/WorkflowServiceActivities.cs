using System.Diagnostics;
using System.Text;
using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Features.Workflows.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Workflows.Adapters;

/// <summary>
/// 工作流统一插件服务活动 (Activity: plugin / extension)
/// </summary>
public sealed class ExtensionActionActivity(
    IPluginManager pluginManager,
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
        if (string.IsNullOrWhiteSpace(extId)) p.TryGetValue("pluginId", out extId);
        if (string.IsNullOrWhiteSpace(extId)) p.TryGetValue("plugin_id", out extId);

        if (string.IsNullOrWhiteSpace(extId))
        {
            return new ActivityResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "插件/扩展服务步骤缺少必填的 'pluginId' 或 'extensionId' 参数",
                DurationMs: 0,
                TimedOut: false
            );
        }

        p.TryGetValue("action", out var action);
        if (string.IsNullOrWhiteSpace(action)) action = "status";

        p.TryGetValue("param", out var paramVal);

        try
        {
            logger.LogInformation("工作流执行插件动作: [{PluginId}] {Action}", extId, action);
            var result = await pluginManager.ExecuteActionAsync(new PluginActionRequest
            {
                PluginId = extId,
                Action = action,
                Param = paramVal
            }, cancellationToken);

            sw.Stop();
            var isSuccess = result.Status == "succeeded";

            var failureStatus = IsPausePolicy(step.FailurePolicy) ? ExecutionStatus.Paused : ExecutionStatus.Failure;

            return new ActivityResult(
                Status: isSuccess ? ExecutionStatus.Success : failureStatus,
                ExitCode: result.ExitCode ?? (isSuccess ? 0 : 1),
                Output: isSuccess ? (result.Message ?? "动作执行成功") : string.Empty,
                ErrorOutput: isSuccess ? string.Empty : (result.Error ?? result.Message ?? "动作执行失败"),
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            logger.LogError(ex, "工作流执行插件动作异常: [{PluginId}] {Action}", extId, action);
            var failureStatus = IsPausePolicy(step.FailurePolicy) ? ExecutionStatus.Paused : ExecutionStatus.Failure;
            return new ActivityResult(
                Status: failureStatus,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: $"插件服务异常: {ex.Message}",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false
            );
        }
    }

    private static bool IsPausePolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy)) return true;
        return policy.Equals("PauseOnFailure", StringComparison.OrdinalIgnoreCase) ||
               policy.Equals("Pause", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// 工作流常驻服务与守护活动 (Activity: worker)
/// </summary>
public sealed class WorkerServiceActivity(
    IPluginManager pluginManager,
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
        if (string.IsNullOrWhiteSpace(workerId)) p.TryGetValue("pluginId", out workerId);

        if (string.IsNullOrWhiteSpace(workerId))
        {
            return new ActivityResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "Worker/Plugin 步骤缺少必填的 'pluginId' 或 'workerId' 参数",
                DurationMs: 0,
                TimedOut: false
            );
        }

        p.TryGetValue("operation", out var operation);
        if (string.IsNullOrWhiteSpace(operation)) operation = "invoke";

        var failureStatus = IsPausePolicy(step.FailurePolicy) ? ExecutionStatus.Paused : ExecutionStatus.Failure;

        try
        {
            logger.LogInformation("工作流执行服务操作: [{PluginId}] {Op}", workerId, operation);

            switch (operation.ToLowerInvariant())
            {
                case "start":
                {
                    var ok = await pluginManager.StartAsync(workerId, cancellationToken);
                    sw.Stop();
                    return new ActivityResult(
                        Status: ok ? ExecutionStatus.Success : failureStatus,
                        ExitCode: ok ? 0 : 1,
                        Output: ok ? $"插件服务 '{workerId}' 启动成功" : string.Empty,
                        ErrorOutput: ok ? string.Empty : $"插件服务 '{workerId}' 启动失败",
                        DurationMs: sw.ElapsedMilliseconds,
                        TimedOut: false);
                }

                case "stop":
                {
                    var ok = await pluginManager.StopAsync(workerId, cancellationToken);
                    sw.Stop();
                    return new ActivityResult(
                        Status: ok ? ExecutionStatus.Success : failureStatus,
                        ExitCode: ok ? 0 : 1,
                        Output: ok ? $"插件服务 '{workerId}' 已停止" : string.Empty,
                        ErrorOutput: ok ? string.Empty : $"插件服务 '{workerId}' 停止失败",
                        DurationMs: sw.ElapsedMilliseconds,
                        TimedOut: false);
                }

                case "restart":
                {
                    var ok = await pluginManager.RestartAsync(workerId, cancellationToken);
                    sw.Stop();
                    return new ActivityResult(
                        Status: ok ? ExecutionStatus.Success : failureStatus,
                        ExitCode: ok ? 0 : 1,
                        Output: ok ? $"插件服务 '{workerId}' 重启成功" : string.Empty,
                        ErrorOutput: ok ? string.Empty : $"插件服务 '{workerId}' 重启失败",
                        DurationMs: sw.ElapsedMilliseconds,
                        TimedOut: false);
                }

                case "invoke":
                default:
                {
                    var plugin = await pluginManager.GetByIdAsync(workerId, cancellationToken);
                    if (plugin == null)
                    {
                        sw.Stop();
                        return new ActivityResult(
                            Status: failureStatus,
                            ExitCode: -1,
                            Output: string.Empty,
                            ErrorOutput: $"未找到插件服务 '{workerId}'",
                            DurationMs: sw.ElapsedMilliseconds,
                            TimedOut: false);
                    }

                    if (plugin.State == PluginState.Disabled)
                    {
                        sw.Stop();
                        return new ActivityResult(
                            Status: failureStatus,
                            ExitCode: -1,
                            Output: string.Empty,
                            ErrorOutput: $"插件服务 '{workerId}' 已被显式禁用，工作流无法调用",
                            DurationMs: sw.ElapsedMilliseconds,
                            TimedOut: false);
                    }

                    if (plugin.State != PluginState.Running || !plugin.AllocatedPort.HasValue)
                    {
                        var startOk = await pluginManager.StartAsync(workerId, cancellationToken);
                        if (!startOk)
                        {
                            sw.Stop();
                            return new ActivityResult(
                                Status: failureStatus,
                                ExitCode: -1,
                                Output: string.Empty,
                                ErrorOutput: $"插件服务 '{workerId}' 未在运行且自动拉起失败",
                                DurationMs: sw.ElapsedMilliseconds,
                                TimedOut: false);
                        }
                        plugin = await pluginManager.GetByIdAsync(workerId, cancellationToken);
                    }

                    p.TryGetValue("subPath", out var subPath);
                    if (string.IsNullOrWhiteSpace(subPath)) p.TryGetValue("sub_path", out subPath);
                    subPath = subPath?.TrimStart('/') ?? "";

                    p.TryGetValue("method", out var method);
                    method = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();

                    p.TryGetValue("body", out var body);

                    var client = _httpClient;
                    var uri = $"http://127.0.0.1:{plugin!.AllocatedPort!.Value}/{subPath}";
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
            logger.LogError(ex, "工作流执行服务操作异常: [{PluginId}] {Op}", workerId, operation);
            return new ActivityResult(
                Status: failureStatus,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: $"插件服务异常: {ex.Message}",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false);
        }
    }

    private static bool IsPausePolicy(string? policy)
    {
        if (string.IsNullOrWhiteSpace(policy)) return true;
        return policy.Equals("PauseOnFailure", StringComparison.OrdinalIgnoreCase) ||
               policy.Equals("Pause", StringComparison.OrdinalIgnoreCase);
    }
}
