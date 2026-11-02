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
        var isWindows = OperatingSystem.IsWindows();
        var scriptCandidates = isWindows
            ? new[]
            {
                Path.Combine(pluginDir, "hooks", $"{request.Action}.ps1"),
                Path.Combine(pluginDir, "hooks", $"{request.Action}.sh"),
                Path.Combine(pluginDir, "hooks", $"{request.Action}.py"),
                Path.Combine(pluginDir, "hooks", $"{request.Action}.js"),
                Path.Combine(pluginDir, $"{request.Action}.ps1"),
                Path.Combine(pluginDir, $"{request.Action}.sh"),
                Path.Combine(pluginDir, $"{request.Action}.py"),
                Path.Combine(pluginDir, $"{request.Action}.js")
            }
            : new[]
            {
                Path.Combine(pluginDir, "hooks", $"{request.Action}.sh"),
                Path.Combine(pluginDir, "hooks", $"{request.Action}.py"),
                Path.Combine(pluginDir, "hooks", $"{request.Action}.js"),
                Path.Combine(pluginDir, "hooks", $"{request.Action}.ps1"),
                Path.Combine(pluginDir, $"{request.Action}.sh"),
                Path.Combine(pluginDir, $"{request.Action}.py"),
                Path.Combine(pluginDir, $"{request.Action}.js"),
                Path.Combine(pluginDir, $"{request.Action}.ps1")
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

        var effectiveTarget = !string.IsNullOrWhiteSpace(request.Target)
            ? request.Target
            : (manifest.Target ?? "container");
        var isHost = string.Equals(effectiveTarget, "host", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsLinux();

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(request.Param))
        {
            env["LWT_PARAM"] = request.Param;
            env["LWT_PARAM_VALUE"] = request.Param;
            env["LWT_PARAM_IP"] = request.Param;
        }
        env["LWT_TARGET"] = effectiveTarget;

        var nsenterPath = isHost ? (File.Exists("/usr/bin/nsenter") ? "/usr/bin/nsenter" : (File.Exists("/bin/nsenter") ? "/bin/nsenter" : null)) : null;
        var canPierce = isHost && nsenterPath != null && Directory.Exists("/proc/1/ns");

        ProcessStartInfo psi;
        if (canPierce && runtime == PluginRuntime.Bash)
        {
            psi = new ProcessStartInfo
            {
                FileName = nsenterPath!,
                WorkingDirectory = pluginDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add("-n");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add("/bin/bash");
            psi.ArgumentList.Add("-c");

            var scriptPath = scriptFile ?? Path.Combine(pluginDir, manifest.Entrypoint);
            var scriptContent = File.Exists(scriptPath) ? await File.ReadAllTextAsync(scriptPath, ct) : "";
            var normalized = scriptContent.Replace("\r\n", "\n").Replace("\r", "\n");

            var envPrefix = "export LWT_TARGET='host'";
            if (!string.IsNullOrEmpty(request.Param))
            {
                var escapedParam = request.Param.Replace("'", "'\\''");
                envPrefix += $" LWT_PARAM='{escapedParam}' LWT_PARAM_VALUE='{escapedParam}' LWT_PARAM_IP='{escapedParam}'; set -- '{escapedParam}';";
            }
            else
            {
                envPrefix += ";";
            }

            psi.ArgumentList.Add(envPrefix + "\n" + normalized);
            foreach (var (k, v) in env)
            {
                psi.EnvironmentVariables[k] = v;
            }
        }
        else
        {
            var actionManifest = manifest with
            {
                Entrypoint = targetEntry,
                Runtime = runtime
            };

            var spec = adapter.BuildStartSpec(actionManifest, pluginDir, null, env);
            psi = new ProcessStartInfo
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
                if (!string.IsNullOrEmpty(request.Param))
                {
                    psi.ArgumentList.Add(request.Param);
                }
            }
            else if (!string.IsNullOrEmpty(spec.Arguments))
            {
                psi.Arguments = spec.Arguments;
                if (!string.IsNullOrEmpty(request.Param))
                {
                    psi.Arguments += $" \"{request.Param}\"";
                }
            }

            foreach (var (k, v) in spec.EnvironmentVariables)
            {
                psi.EnvironmentVariables[k] = v;
            }
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
                System.Text.Json.JsonElement? dataElement = null;
                var trimmed = stdout?.Trim();
                if (!string.IsNullOrWhiteSpace(trimmed) && (trimmed.StartsWith('{') || trimmed.StartsWith('[')))
                {
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                        dataElement = doc.RootElement.Clone();
                    }
                    catch
                    {
                        // 非合法 JSON，保持 dataElement 为 null
                    }
                }

                var msg = string.IsNullOrWhiteSpace(stdout) ? "执行成功" : stdout.Trim();
                if (dataElement.HasValue && dataElement.Value.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (dataElement.Value.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        msg = msgProp.GetString() ?? msg;
                    }
                }

                return new PluginActionResult
                {
                    Status = "succeeded",
                    ExitCode = 0,
                    Data = dataElement,
                    Message = msg
                };
            }

            System.Text.Json.JsonElement? errData = null;
            var errTrimmed = !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim();
            var errMsg = $"执行失败 (退出码 {p.ExitCode}): {errTrimmed}";
            if (!string.IsNullOrWhiteSpace(errTrimmed) && (errTrimmed.StartsWith('{') || errTrimmed.StartsWith('[')))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(errTrimmed);
                    errData = doc.RootElement.Clone();
                    if (errData.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        errData.Value.TryGetProperty("message", out var errProp) && errProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        errMsg = errProp.GetString() ?? errMsg;
                    }
                }
                catch { }
            }

            return new PluginActionResult
            {
                Status = "failed",
                ExitCode = p.ExitCode,
                Data = errData,
                Message = errMsg,
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
