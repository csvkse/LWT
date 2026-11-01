using System.Diagnostics;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Contracts.Shared.Support;

namespace LinuxWebTool.Infrastructure.Features.Workers;

public sealed partial class WorkersManager
{
    public string? ResolveWorkerAssetPath(string id, string? relativePath)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return null;

        var workerDir = Path.Combine(_workersRoot, id);
        if (!Directory.Exists(workerDir)) return null;

        var cleanRel = string.IsNullOrWhiteSpace(relativePath) ? "index.html" : relativePath.Trim().TrimStart('/', '\\');
        if (cleanRel.Contains("..")) return null;

        var candidates = new[]
        {
            Path.Combine(workerDir, "ui", cleanRel),
            Path.Combine(workerDir, "assets", cleanRel),
            Path.Combine(workerDir, cleanRel)
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                var full = Path.GetFullPath(c);
                var fullWorkerDir = Path.GetFullPath(workerDir);
                if (full.StartsWith(fullWorkerDir, StringComparison.OrdinalIgnoreCase))
                {
                    return full;
                }
            }
        }

        return null;
    }

    public async ValueTask<WorkerActionResult> ExecuteActionAsync(WorkerActionRequest request, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(request.WorkerId))
        {
            return new WorkerActionResult { Success = false, Message = $"非法 Worker ID: {request.WorkerId}" };
        }
        if (!PluginSecurityGuard.IsValidActionName(request.Action))
        {
            return new WorkerActionResult { Success = false, Message = $"非法动作名称: {request.Action}" };
        }

        var dir = Path.Combine(_workersRoot, request.WorkerId);
        if (!Directory.Exists(dir))
        {
            return new WorkerActionResult { Success = false, Message = $"未找到 Worker: {request.WorkerId}" };
        }

        if (File.Exists(Path.Combine(dir, ".disabled")))
        {
            return new WorkerActionResult { Success = false, Message = $"Worker '{request.WorkerId}' 已被显式禁用，禁止执行任何操作。" };
        }

        if (request.Action.Equals("config_set", StringComparison.OrdinalIgnoreCase))
        {
            if (request.ConfigValues != null)
            {
                var configPath = Path.Combine(dir, "config.json");
                var json = JsonSerializer.Serialize(request.ConfigValues, WorkerJsonContext.Default.DictionaryStringString);
                await File.WriteAllTextAsync(configPath, json, ct);
            }
            return new WorkerActionResult { Success = true, Message = "配置已更新" };
        }

        var scriptCandidates = new[]
        {
            Path.Combine(dir, "hooks", $"{request.Action}.ps1"),
            Path.Combine(dir, "hooks", $"{request.Action}.sh"),
            Path.Combine(dir, "hooks", $"{request.Action}.py"),
            Path.Combine(dir, "hooks", $"{request.Action}.js"),
            Path.Combine(dir, $"{request.Action}.ps1"),
            Path.Combine(dir, $"{request.Action}.sh"),
            Path.Combine(dir, $"{request.Action}.py"),
            Path.Combine(dir, $"{request.Action}.js")
        };

        var scriptFile = scriptCandidates.FirstOrDefault(File.Exists);
        if (scriptFile != null)
        {
            var ext = Path.GetExtension(scriptFile).ToLowerInvariant();
            var runtime = ext switch
            {
                ".py" => WorkerRuntimeType.Python,
                ".js" => WorkerRuntimeType.Node,
                ".ps1" => WorkerRuntimeType.PowerShell,
                _ => WorkerRuntimeType.Bash
            };

            var actionManifest = new WorkerManifest
            {
                Id = request.WorkerId,
                Name = request.WorkerId,
                Entrypoint = Path.GetRelativePath(dir, scriptFile),
                Runtime = runtime
            };

            if (_adapters.TryGetValue(runtime, out var adapter))
            {
                var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (request.ActionParams != null)
                {
                    foreach (var (k, v) in request.ActionParams)
                    {
                        env[$"LWT_PARAM_{k.ToUpperInvariant()}"] = v;
                    }
                }

                var spec = adapter.BuildStartSpec(actionManifest, dir, null, env);
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
                    if (p != null)
                    {
                        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
                        var stderrTask = p.StandardError.ReadToEndAsync(ct);
                        await p.WaitForExitAsync(ct);
                        var stdout = await stdoutTask;
                        var stderr = await stderrTask;
                        if (p.ExitCode == 0)
                        {
                            return new WorkerActionResult { Success = true, Message = "执行成功", Output = stdout };
                        }
                        return new WorkerActionResult { Success = false, Message = $"执行失败 (退出码 {p.ExitCode}): {stderr}", Output = stdout };
                    }
                }
                catch (Exception ex)
                {
                    return new WorkerActionResult { Success = false, Message = $"运行脚本异常: {ex.Message}" };
                }
            }
        }

        return new WorkerActionResult { Success = true, Message = $"Worker 动作 '{request.Action}' 执行完成" };
    }
}
