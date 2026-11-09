using System.Diagnostics;
using System.Text;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Build;

/// <summary>
/// 声明式指令编译策略 (Declarative Source-Compiled Build Strategy)
/// </summary>
public sealed class SourceCompiledBuildStrategy : IPluginBuildStrategy
{
    public bool CanHandle(PluginManifest manifest)
    {
        return manifest.GetEffectiveLifecycle() == PluginLifecycle.SourceCompiled;
    }

    public async Task<PluginBuildResult> BuildAsync(string pluginDir, PluginManifest manifest, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (manifest.Build == null || string.IsNullOrWhiteSpace(manifest.Build.Command))
        {
            return new PluginBuildResult
            {
                Success = false,
                ExitCode = 1,
                ErrorMessage = "生命周期规格为 SourceCompiled 的插件必须声明有效的构建规范 (build.command)",
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        var workingDir = pluginDir;
        if (!string.IsNullOrWhiteSpace(manifest.Build.WorkingDir))
        {
            var resolved = Path.GetFullPath(Path.Combine(pluginDir, manifest.Build.WorkingDir));
            if (!resolved.StartsWith(Path.GetFullPath(pluginDir), StringComparison.OrdinalIgnoreCase))
            {
                return new PluginBuildResult
                {
                    Success = false,
                    ExitCode = 1,
                    ErrorMessage = "构建工作目录禁止超出插件目录范围",
                    ElapsedMs = sw.ElapsedMilliseconds
                };
            }
            if (!Directory.Exists(resolved))
            {
                Directory.CreateDirectory(resolved);
            }
            workingDir = resolved;
        }

        var cmd = manifest.Build.Command.Trim();
        var isWindows = OperatingSystem.IsWindows();
        var shellExe = isWindows ? "cmd.exe" : "/bin/sh";
        var shellArgs = isWindows ? $"/c \"{cmd}\"" : $"-c \"{cmd.Replace("\"", "\\\"")}\"";

        var psi = new ProcessStartInfo
        {
            FileName = shellExe,
            Arguments = shellArgs,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var customEnv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["LWT_PLUGIN_ID"] = manifest.Id,
            ["PLUGIN_ID"] = manifest.Id,
            ["PLUGIN_DIR"] = pluginDir,
            ["BUILD_DIR"] = workingDir
        };

        if (manifest.Build.Env != null)
        {
            foreach (var (k, v) in manifest.Build.Env)
            {
                customEnv[k] = v;
            }
        }

        PluginEnvironmentSanitizer.ApplySafeEnvironment(psi, customEnv);

        var outputSb = new StringBuilder();
        var errorSb = new StringBuilder();

        var timeoutSeconds = manifest.Build.TimeoutSeconds > 0 ? manifest.Build.TimeoutSeconds : 180;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        int exitCode;
        try
        {
            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) outputSb.AppendLine(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) errorSb.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(cts.Token);
            exitCode = process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new PluginBuildResult
            {
                Success = false,
                ExitCode = -1,
                Output = outputSb.ToString(),
                Error = errorSb.ToString(),
                ErrorMessage = $"构建超时 (超过限制 {timeoutSeconds} 秒)",
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PluginBuildResult
            {
                Success = false,
                ExitCode = -1,
                Output = outputSb.ToString(),
                Error = errorSb.ToString(),
                ErrorMessage = $"拉起构建进程异常: {ex.Message}",
                ElapsedMs = sw.ElapsedMilliseconds
            };
        }

        sw.Stop();

        var verifiedArtifacts = new List<string>();
        if (exitCode == 0 && manifest.Build.Artifacts != null && manifest.Build.Artifacts.Count > 0)
        {
            foreach (var art in manifest.Build.Artifacts)
            {
                var clean = art.TrimStart('/', '\\');
                var artPath = Path.Combine(pluginDir, clean);
                if (File.Exists(artPath) || Directory.Exists(artPath))
                {
                    verifiedArtifacts.Add(clean);
                }
                else
                {
                    outputSb.AppendLine($"[Warning] 构建产物未找到: {clean}");
                }
            }
        }

        return new PluginBuildResult
        {
            Success = exitCode == 0,
            ExitCode = exitCode,
            Output = outputSb.ToString(),
            Error = errorSb.ToString(),
            Artifacts = verifiedArtifacts,
            ErrorMessage = exitCode == 0 ? null : "构建进程返回非零退出代码",
            ElapsedMs = sw.ElapsedMilliseconds
        };
    }
}
