using System.Diagnostics;
using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Scheduling.Adapters;

public interface ICommandExecutionStrategy
{
    TargetType SupportedTargetType { get; }
    Task<StandardExecutionResult> ExecuteAsync(CommandExecutionContext context, CancellationToken cancellationToken = default);
}

public sealed class LocalCommandExecutionStrategy(
    IShellExecutor shellExecutor,
    ILogger<LocalCommandExecutionStrategy> logger) : ICommandExecutionStrategy
{
    public TargetType SupportedTargetType => TargetType.Local;

    public async Task<StandardExecutionResult> ExecuteAsync(CommandExecutionContext context, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.Now;
        try
        {
            var shellRequest = context.ScriptType == (int)ScriptType.BashScript
                ? new ShellRequest
                {
                    ScriptText = context.CommandText,
                    ScriptArguments = context.Arguments,
                    TimeoutSeconds = context.TimeoutSeconds,
                    Target = context.ExecutionTarget ?? "container",
                }
                : new ShellRequest
                {
                    CommandText = context.CommandText,
                    TimeoutSeconds = context.TimeoutSeconds,
                    Target = context.ExecutionTarget ?? "container",
                };

            var shellResult = await shellExecutor.ExecuteAsync(shellRequest, cancellationToken);
            return new StandardExecutionResult(
                Status: shellResult.Status,
                ExitCode: shellResult.ExitCode,
                Output: shellResult.StandardOutput,
                ErrorOutput: shellResult.ErrorOutput,
                DurationMs: shellResult.DurationMs,
                TimedOut: shellResult.TimedOut,
                Truncated: shellResult.Truncated,
                StartTime: shellResult.StartTime,
                EndTime: shellResult.EndTime
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var endTime = DateTime.Now;
            return new StandardExecutionResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "命令执行被取消",
                DurationMs: (long)(endTime - startTime).TotalMilliseconds,
                TimedOut: false,
                Truncated: false,
                StartTime: startTime,
                EndTime: endTime
            );
        }
        catch (Exception ex)
        {
            var endTime = DateTime.Now;
            logger.LogError(ex, "本地命令执行异常: {CommandText}", context.CommandText);
            return new StandardExecutionResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: $"本地执行异常: {ex.Message}",
                DurationMs: (long)(endTime - startTime).TotalMilliseconds,
                TimedOut: false,
                Truncated: false,
                StartTime: startTime,
                EndTime: endTime
            );
        }
    }
}

public sealed class RemoteSshCommandExecutionStrategy(
    RemoteHostStore hostStore,
    RemoteHostClientFactory clientFactory,
    HostCircuitBreaker circuitBreaker,
    ILogger<RemoteSshCommandExecutionStrategy> logger) : ICommandExecutionStrategy
{
    public TargetType SupportedTargetType => TargetType.RemoteHost;

    public async Task<StandardExecutionResult> ExecuteAsync(CommandExecutionContext context, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.Now;
        var hostId = context.Target.HostId;

        if (string.IsNullOrWhiteSpace(hostId))
        {
            var now = DateTime.Now;
            return new StandardExecutionResult(
                Status: ExecutionStatus.Skipped,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "[目标缺失] 未指定远程主机 ID",
                DurationMs: 0,
                TimedOut: false,
                Truncated: false,
                StartTime: now,
                EndTime: now,
                SkipReason: "未指定远程主机 ID"
            );
        }

        var host = await hostStore.GetByIdAsync(hostId);
        if (host is null)
        {
            var now = DateTime.Now;
            var reason = $"[目标缺失] 远程主机 (ID: {hostId}) 已被删除或不存在";
            logger.LogWarning("远程主机不存在，自动跳过执行: {HostId}", hostId);
            return new StandardExecutionResult(
                Status: ExecutionStatus.Skipped,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: reason,
                DurationMs: 0,
                TimedOut: false,
                Truncated: false,
                StartTime: now,
                EndTime: now,
                SkipReason: reason
            );
        }

        if (!circuitBreaker.AllowExecution(hostId, out var openReason))
        {
            var now = DateTime.Now;
            var reason = $"[熔断防护] {openReason}";
            logger.LogWarning("远程主机触发熔断跳过执行: {HostId}, 原因: {Reason}", hostId, openReason);
            return new StandardExecutionResult(
                Status: ExecutionStatus.Skipped,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: reason,
                DurationMs: 0,
                TimedOut: false,
                Truncated: false,
                StartTime: now,
                EndTime: now,
                SkipReason: reason
            );
        }

        var sw = Stopwatch.StartNew();
        try
        {
            await using var adapter = clientFactory.CreateAdapter(host);
            var timeoutSeconds = Math.Clamp(context.TimeoutSeconds <= 0 ? 60 : context.TimeoutSeconds, 1, 600);
            var timeout = TimeSpan.FromSeconds(timeoutSeconds);

            var fullCommand = string.IsNullOrWhiteSpace(context.Arguments)
                ? context.CommandText
                : $"{context.CommandText} {context.Arguments}";

            var execResponse = await adapter.ExecuteCommandAsync(fullCommand, timeout, cancellationToken);
            sw.Stop();
            var endTime = DateTime.Now;

            await hostStore.TouchConnectedAsync(hostId);
            circuitBreaker.ReportSuccess(hostId);

            var status = execResponse.ExitCode == 0 ? ExecutionStatus.Success : ExecutionStatus.Failure;
            return new StandardExecutionResult(
                Status: status,
                ExitCode: execResponse.ExitCode,
                Output: execResponse.Output ?? string.Empty,
                ErrorOutput: execResponse.ErrorOutput ?? string.Empty,
                DurationMs: execResponse.DurationMs > 0 ? execResponse.DurationMs : sw.ElapsedMilliseconds,
                TimedOut: false,
                Truncated: false,
                StartTime: startTime,
                EndTime: endTime
            );
        }
        catch (TimeoutException tex)
        {
            sw.Stop();
            var endTime = DateTime.Now;
            circuitBreaker.ReportFailure(hostId, "命令执行超时");
            logger.LogWarning(tex, "远程主机 {HostId} 执行超时", hostId);
            return new StandardExecutionResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: $"执行超时: {tex.Message}",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: true,
                Truncated: false,
                StartTime: startTime,
                EndTime: endTime
            );
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            var endTime = DateTime.Now;
            return new StandardExecutionResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: "命令执行被取消",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false,
                Truncated: false,
                StartTime: startTime,
                EndTime: endTime
            );
        }
        catch (Exception ex)
        {
            sw.Stop();
            var endTime = DateTime.Now;
            circuitBreaker.ReportFailure(hostId, ex.Message);
            logger.LogError(ex, "远程主机 {HostId} 执行失败: {Message}", hostId, ex.Message);
            return new StandardExecutionResult(
                Status: ExecutionStatus.Failure,
                ExitCode: -1,
                Output: string.Empty,
                ErrorOutput: $"远端连接或执行失败: {ex.Message}",
                DurationMs: sw.ElapsedMilliseconds,
                TimedOut: false,
                Truncated: false,
                StartTime: startTime,
                EndTime: endTime
            );
        }
    }
}

public sealed class ExecutionStrategyResolver(
    LocalCommandExecutionStrategy localStrategy,
    RemoteSshCommandExecutionStrategy remoteStrategy)
{
    public ICommandExecutionStrategy Resolve(TargetType targetType)
    {
        return targetType switch
        {
            TargetType.RemoteHost => remoteStrategy,
            _ => localStrategy
        };
    }
}
