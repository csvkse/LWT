using System.Diagnostics;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Strategies;

/// <summary>
/// 一次性脚本轻量扩展执行策略。
/// 触发时运行解释器、捕获标准输出与退出码，不占用常驻端口与长期进程。
/// </summary>
public sealed class OneShotExecutionStrategy : IExecutionProfileStrategy
{
    public PluginProfile Profile => PluginProfile.OneShot;

    public ValueTask<(bool Success, string? ErrorMessage)> StartAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine stateMachine,
        PluginPortManager portManager,
        PluginProcessRingBuffer ringBuffer,
        IReadOnlyDictionary<PluginRuntime, IRuntimeAdapter> adapters,
        CancellationToken ct)
    {
        // OneShot 插件无长驻进程，显式启动触发其 entrypoint 一次性执行
        stateMachine.NotifyStarting();
        stateMachine.NotifyRunning(readiness: "Ready");
        return ValueTask.FromResult((true, (string?)null));
    }

    public ValueTask<bool> StopAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine stateMachine,
        PluginPortManager portManager,
        CancellationToken ct)
    {
        stateMachine.NotifyStopped();
        return ValueTask.FromResult(true);
    }

    public async ValueTask<PluginActionResult> ExecuteActionAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginActionRequest request,
        IReadOnlyDictionary<PluginRuntime, IRuntimeAdapter> adapters,
        CancellationToken ct)
    {
        var scriptCandidates = new[]
        {
            Path.Combine(pluginDir, "hooks", $"{request.Action}.ps1"),
            Path.Combine(pluginDir, "hooks", $"{request.Action}.sh"),
            Path.Combine(pluginDir, "hooks", $"{request.Action}.py"),
            Path.Combine(pluginDir, "hooks", $"{request.Action}.js"),
            Path.Combine(pluginDir, $"{request.Action}.ps1"),
            Path.Combine(pluginDir, $"{request.Action}.sh"),
            Path.Combine(pluginDir, $"{request.Action}.py"),
            Path.Combine(pluginDir, $"{request.Action}.js")
        };

        var scriptFile = scriptCandidates.FirstOrDefault(File.Exists);
        var targetEntry = scriptFile != null ? Path.GetRelativePath(pluginDir, scriptFile) : manifest.Entrypoint;

        var ext = Path.GetExtension(targetEntry).ToLowerInvariant();
        var runtime = ext switch
        {
            ".py" => PluginRuntime.Python,
            ".js" => PluginRuntime.Node,
            ".ps1" => PluginRuntime.PowerShell,
            ".sh" => PluginRuntime.Bash,
            _ => manifest.Runtime
        };

        if (!adapters.TryGetValue(runtime, out var adapter))
        {
            return new PluginActionResult
            {
                Status = "failed",
                Message = $"未找到对应运行时适配器: {runtime}"
            };
        }

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(request.Param))
        {
            env["LWT_PARAM"] = request.Param;
            env["LWT_PARAM_VALUE"] = request.Param;
        }

        var actionManifest = manifest with
        {
            Entrypoint = targetEntry,
            Runtime = runtime
        };

        var spec = adapter.BuildStartSpec(actionManifest, pluginDir, null, env);
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (spec.ArgumentList != null && spec.ArgumentList.Count > 0)
        {
            foreach (var arg in spec.ArgumentList)
            {
                psi.ArgumentList.Add(arg);
            }
        }
        else if (!string.IsNullOrEmpty(spec.Arguments))
        {
            psi.Arguments = spec.Arguments;
        }

        foreach (var (k, v) in spec.EnvironmentVariables)
        {
            psi.EnvironmentVariables[k] = v;
        }

        try
        {
            using var p = Process.Start(psi);
            if (p == null)
            {
                return new PluginActionResult
                {
                    Status = "failed",
                    Message = "启动进程失败，未能创建进程实例"
                };
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (p.ExitCode == 0)
            {
                return new PluginActionResult
                {
                    Status = "succeeded",
                    ExitCode = 0,
                    Message = string.IsNullOrWhiteSpace(stdout) ? "执行成功" : stdout.Trim()
                };
            }

            return new PluginActionResult
            {
                Status = "failed",
                ExitCode = p.ExitCode,
                Message = $"执行失败 (退出码 {p.ExitCode}): {stderr.Trim()}",
                Error = stderr.Trim()
            };
        }
        catch (Exception ex)
        {
            return new PluginActionResult
            {
                Status = "failed",
                Message = $"运行动作异常: {ex.Message}",
                Error = ex.ToString()
            };
        }
    }
}
