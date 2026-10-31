using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;

namespace LinuxWebTool.Application.Features.Plugins;

/// <summary>
/// 一次性原生命令行执行适配器：
/// - 组合参数解析、受控进程运行与结果/产物适配；
/// - 退出码与结果产物双重判定：退出码不被文本伪装，坏 JSON 严格报错 ProtocolError。
/// </summary>
public sealed class NativeCommandExecutor : INativeCommandExecutor
{
    private readonly ICommandBindingResolver _bindingResolver;
    private readonly INativeProcessRunner _processRunner;

    public NativeCommandExecutor(
        ICommandBindingResolver bindingResolver,
        INativeProcessRunner processRunner)
    {
        _bindingResolver = bindingResolver ?? throw new ArgumentNullException(nameof(bindingResolver));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<PluginActionResult> ExecuteAsync(
        NativeCommandDefinition definition,
        CommandInvocation invocation,
        string workingDir,
        string operationId,
        ICommandProgressObserver? observer,
        CancellationToken ct)
    {
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (invocation == null) throw new ArgumentNullException(nameof(invocation));

        var timeoutSec = definition.TimeoutSeconds > 0 ? Math.Min(definition.TimeoutSeconds, 86400) : 30;
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        NativeProgramSpec spec;
        try
        {
            spec = await _bindingResolver.ResolveAsync(definition, invocation, workingDir, operationId, linkedCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PluginActionResult
            {
                Status = "failed",
                ErrorCode = "BindingError",
                Error = $"参数绑定失败: {ex.Message}"
            };
        }

        NativeProcessResult procResult;
        try
        {
            procResult = await _processRunner.RunAsync(spec, observer, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (timeoutCts.IsCancellationRequested)
            {
                return new PluginActionResult
                {
                    Status = "timedOut",
                    Error = $"命令执行超时 (超过 {timeoutSec} 秒限制)。",
                    EffectUncertain = true
                };
            }

            return new PluginActionResult
            {
                Status = "cancelled",
                Error = "命令已被调用者取消。",
                EffectUncertain = true
            };
        }
        catch (Exception ex)
        {
            return new PluginActionResult
            {
                Status = "failed",
                ErrorCode = "ExecutionError",
                Error = $"执行异常: {ex.Message}",
                EffectUncertain = true
            };
        }

        // 判定超时或取消状态
        if (procResult.TimedOut || (timeoutCts.IsCancellationRequested && procResult.Cancelled))
        {
            return new PluginActionResult
            {
                Status = "timedOut",
                ExitCode = procResult.ExitCode,
                Error = $"命令执行超时 (超过 {timeoutSec} 秒限制)。",
                EffectUncertain = true
            };
        }

        if (procResult.Cancelled)
        {
            return new PluginActionResult
            {
                Status = "cancelled",
                ExitCode = procResult.ExitCode,
                Error = "命令已被取消。",
                EffectUncertain = true
            };
        }

        // 判定退出码
        var acceptedCodes = definition.AcceptedExitCodes?.Count > 0 ? definition.AcceptedExitCodes : [0];
        if (!acceptedCodes.Contains(procResult.ExitCode))
        {
            var err = !string.IsNullOrWhiteSpace(procResult.StandardError)
                ? procResult.StandardError.Trim()
                : $"进程以未预期的退出码退出 ({procResult.ExitCode})。";

            return new PluginActionResult
            {
                Status = "failed",
                ExitCode = procResult.ExitCode,
                Error = err,
                ErrorCode = "NonZeroExitCode",
                EffectUncertain = false
            };
        }

        // 结果解析判定
        return ParseCommandResult(definition, procResult, workingDir);
    }

    private static PluginActionResult ParseCommandResult(
        NativeCommandDefinition definition,
        NativeProcessResult procResult,
        string workingDir)
    {
        var parser = definition.ResultParser?.ToLowerInvariant() ?? "bounded-text";

        switch (parser)
        {
            case "json":
            {
                var stdout = procResult.StandardOutput?.Trim();
                if (string.IsNullOrWhiteSpace(stdout))
                {
                    return new PluginActionResult
                    {
                        Status = "failed",
                        ExitCode = procResult.ExitCode,
                        ErrorCode = "ProtocolError",
                        Error = "命令未输出预期的 JSON 结果。"
                    };
                }

                try
                {
                    using var doc = JsonDocument.Parse(stdout);
                    return new PluginActionResult
                    {
                        Status = "succeeded",
                        ExitCode = procResult.ExitCode,
                        Data = doc.RootElement.Clone(),
                        Message = "命令执行成功并返回结构化数据。"
                    };
                }
                catch (JsonException ex)
                {
                    return new PluginActionResult
                    {
                        Status = "failed",
                        ExitCode = procResult.ExitCode,
                        ErrorCode = "ProtocolError",
                        Error = $"命令输出非合法 JSON: {ex.Message}"
                    };
                }
            }

            case "artifacts":
            {
                var artifactRefs = new List<PluginArtifactRef>();
                foreach (var rule in definition.ArtifactRules)
                {
                    var fullPath = PluginSecurityGuard.EnsureSafeArtifactPath(workingDir, rule.RelativePath);
                    if (!File.Exists(fullPath))
                    {
                        if (!rule.Optional)
                        {
                            return new PluginActionResult
                            {
                                Status = "failed",
                                ExitCode = procResult.ExitCode,
                                ErrorCode = "ArtifactMissing",
                                Error = $"未生成预期的必要产物文件: '{rule.RelativePath}'"
                            };
                        }
                        continue;
                    }

                    var fi = new FileInfo(fullPath);
                    artifactRefs.Add(new PluginArtifactRef
                    {
                        ArtifactId = rule.ArtifactId,
                        Filename = Path.GetFileName(fullPath),
                        SizeBytes = fi.Length,
                        ContentType = rule.ContentType ?? "application/octet-stream",
                        DownloadUrl = $"/api/PluginOperations/artifacts/{rule.ArtifactId}"
                    });
                }

                return new PluginActionResult
                {
                    Status = "succeeded",
                    ExitCode = procResult.ExitCode,
                    ArtifactRefs = artifactRefs,
                    Message = $"命令执行完成，已生成 {artifactRefs.Count} 项产物。"
                };
            }

            case "bounded-text":
            default:
            {
                return new PluginActionResult
                {
                    Status = "succeeded",
                    ExitCode = procResult.ExitCode,
                    Message = procResult.StandardOutput?.Trim()
                };
            }
        }
    }
}
