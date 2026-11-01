using System.Collections.Concurrent;
using System.Diagnostics;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Strategies;

/// <summary>
/// 常驻微服务与后台守护进程执行策略。
/// 负责长时进程监管、动态端口分配、日志环形缓冲与 CrashLoopBackoff 熔断保护。
/// </summary>
public sealed class ResidentExecutionStrategy : IExecutionProfileStrategy
{
    public PluginProfile Profile => PluginProfile.Resident;

    private readonly ConcurrentDictionary<string, Process> _processes = new(StringComparer.OrdinalIgnoreCase);

    public int? GetProcessId(string pluginId)
    {
        return _processes.TryGetValue(pluginId, out var proc) && !proc.HasExited ? proc.Id : null;
    }

    public ValueTask<(bool Success, string? ErrorMessage)> StartAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine stateMachine,
        PluginPortManager portManager,
        PluginProcessRingBuffer ringBuffer,
        IReadOnlyDictionary<PluginRuntime, IRuntimeAdapter> adapters,
        CancellationToken ct)
    {
        if (_processes.TryGetValue(manifest.Id, out var existingProc) && !existingProc.HasExited)
        {
            return ValueTask.FromResult((true, (string?)null));
        }

        if (!adapters.TryGetValue(manifest.Runtime, out var adapter))
        {
            var err = $"未找到对应运行时适配器: {manifest.Runtime}";
            stateMachine.MarkMissingRuntime(err);
            return ValueTask.FromResult((false, (string?)err));
        }

        if (!adapter.CheckAvailable(out var missingReason, out _))
        {
            stateMachine.MarkMissingRuntime(missingReason ?? "缺少必要运行时环境");
            return ValueTask.FromResult((false, missingReason));
        }

        int? allocatedPort = null;
        if (manifest.Network != null && manifest.Network.Mode is "path" or "port")
        {
            try
            {
                allocatedPort = portManager.AllocatePort(manifest.Id, manifest.Network.Port);
            }
            catch (Exception ex)
            {
                return ValueTask.FromResult((false, (string?)$"分配端口失败: {ex.Message}"));
            }
        }

        var runId = DateTime.UtcNow.Ticks;
        stateMachine.NotifyStarting(runId);

        var spec = adapter.BuildStartSpec(manifest, pluginDir, allocatedPort);
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
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };

            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [STDOUT] {e.Data}");
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [STDERR] {e.Data}");
                }
            };

            proc.Exited += (_, _) =>
            {
                var exitCode = proc.ExitCode;
                _processes.TryRemove(manifest.Id, out _);
                ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] 进程已退出，退出码: {exitCode}");
                stateMachine.RecordExit(exitCode, runId);
            };

            if (!proc.Start())
            {
                stateMachine.RecordExit(-1, runId, "无法启动底层系统进程");
                return ValueTask.FromResult((false, (string?)"启动进程失败"));
            }

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _processes[manifest.Id] = proc;
            stateMachine.NotifyRunning(runId, readiness: "Ready");
            ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] 插件微服务启动成功 (PID: {proc.Id}, 端口: {allocatedPort?.ToString() ?? "无"})");

            return ValueTask.FromResult((true, (string?)null));
        }
        catch (Exception ex)
        {
            stateMachine.RecordExit(-1, runId, ex.Message);
            ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [ERROR] 启动进程异常: {ex.Message}");
            return ValueTask.FromResult((false, (string?)$"启动异常: {ex.Message}"));
        }
    }

    public ValueTask<bool> StopAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine stateMachine,
        PluginPortManager portManager,
        CancellationToken ct)
    {
        stateMachine.NotifyStopped();
        portManager.ReleasePort(manifest.Id);

        if (_processes.TryRemove(manifest.Id, out var proc))
        {
            try
            {
                if (!proc.HasExited)
                {
                    proc.Kill(entireProcessTree: true);
                }
                proc.Dispose();
            }
            catch
            {
                // 忽略进程杀灭异常
            }
        }

        return ValueTask.FromResult(true);
    }

    public async ValueTask<PluginActionResult> ExecuteActionAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginActionRequest request,
        IReadOnlyDictionary<PluginRuntime, IRuntimeAdapter> adapters,
        CancellationToken ct)
    {
        // 1. 优先探测是否匹配 hooks 目录下的脚本
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
        if (scriptFile != null)
        {
            var ext = Path.GetExtension(scriptFile).ToLowerInvariant();
            var runtime = ext switch
            {
                ".py" => PluginRuntime.Python,
                ".js" => PluginRuntime.Node,
                ".ps1" => PluginRuntime.PowerShell,
                _ => PluginRuntime.Bash
            };

            if (adapters.TryGetValue(runtime, out var adapter))
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(request.Param))
                {
                    env["LWT_PARAM"] = request.Param;
                }

                var actionManifest = manifest with
                {
                    Entrypoint = Path.GetRelativePath(pluginDir, scriptFile),
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
                    foreach (var arg in spec.ArgumentList) psi.ArgumentList.Add(arg);
                }
                else if (!string.IsNullOrEmpty(spec.Arguments))
                {
                    psi.Arguments = spec.Arguments;
                }

                foreach (var (k, v) in spec.EnvironmentVariables) psi.EnvironmentVariables[k] = v;

                try
                {
                    using var p = Process.Start(psi);
                    if (p != null)
                    {
                        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
                        var stderrTask = p.StandardError.ReadToEndAsync(ct);
                        await p.WaitForExitAsync(ct);
                        var stdout = await stdoutTask;
                        var stderr = await stderrTask;

                        if (p.ExitCode == 0)
                        {
                            return new PluginActionResult { Status = "succeeded", ExitCode = 0, Message = stdout.Trim() };
                        }
                        return new PluginActionResult { Status = "failed", ExitCode = p.ExitCode, Message = stderr.Trim(), Error = stderr.Trim() };
                    }
                }
                catch (Exception ex)
                {
                    return new PluginActionResult { Status = "failed", Message = $"运行动作异常: {ex.Message}", Error = ex.ToString() };
                }
            }
        }

        return new PluginActionResult
        {
            Status = "succeeded",
            Message = $"常驻微服务 '{manifest.Name}' 已接收动作: {request.Action}"
        };
    }
}
