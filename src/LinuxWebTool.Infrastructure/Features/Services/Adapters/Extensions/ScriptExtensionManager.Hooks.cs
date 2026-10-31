using System.Diagnostics;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using LinuxWebTool.Contracts.Shared.Support;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

public sealed partial class ScriptExtensionManager
{
    private async Task<(string Status, string? Message, int? Pid)> ProbeExtensionStatusAsync(string dir, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await RunHookScriptAsync(dir, "status", null, cancellationToken);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout.Trim());
                var root = doc.RootElement;
                var target = root.TryGetProperty("data", out var pData) && pData.ValueKind == JsonValueKind.Object ? pData : root;

                var stat = target.TryGetProperty("status", out var pStat) ? pStat.GetString() ?? "Running"
                    : (root.TryGetProperty("status", out var topStat) ? topStat.GetString() ?? "Running" : "Running");

                var msg = target.TryGetProperty("message", out var pMsg) ? pMsg.GetString()
                    : (root.TryGetProperty("message", out var topMsg) ? topMsg.GetString() : null);

                int? pid = null;
                if (target.TryGetProperty("pid", out var pPid) && pPid.TryGetInt32(out var p)) pid = p;
                else if (root.TryGetProperty("pid", out var topPid) && topPid.TryGetInt32(out var tp)) pid = tp;

                return (stat, msg, pid);
            }
            catch
            {
                return ("Unknown", "状态探测协议异常: 输出非标准 JSON 协议", null);
            }
        }
        return ("Stopped", "服务未运行", null);
    }

    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunHookScriptAsync(
        string dir,
        string hookName,
        Dictionary<string, string>? parameters,
        CancellationToken cancellationToken,
        string? explicitTarget = null)
    {
        if (!PluginSecurityGuard.IsValidActionName(hookName))
        {
            return (-1, "", $"非法钩子动作名称: {hookName}");
        }

        var hooksDir = Path.Combine(dir, "hooks");
        if (!Directory.Exists(hooksDir)) hooksDir = dir;

        string? scriptPath = null;
        string? runnerExe = null;
        string? runnerArgs = null;
        var isWindows = OperatingSystem.IsWindows();

        if (isWindows)
        {
            var ps1 = Path.Combine(hooksDir, $"{hookName}.ps1");
            var bat = Path.Combine(hooksDir, $"{hookName}.bat");
            var js = Path.Combine(hooksDir, $"{hookName}.js");
            var py = Path.Combine(hooksDir, $"{hookName}.py");
            if (File.Exists(ps1)) { scriptPath = ps1; runnerExe = "powershell.exe"; runnerArgs = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ps1}\""; }
            else if (File.Exists(bat)) { scriptPath = bat; runnerExe = "cmd.exe"; runnerArgs = $"/c \"{bat}\""; }
            else if (File.Exists(js)) { scriptPath = js; runnerExe = "node"; runnerArgs = $"\"{js}\""; }
            else if (File.Exists(py)) { scriptPath = py; runnerExe = "python"; runnerArgs = $"\"{py}\""; }
        }
        else
        {
            var sh = Path.Combine(hooksDir, $"{hookName}.sh");
            var js = Path.Combine(hooksDir, $"{hookName}.js");
            var py = Path.Combine(hooksDir, $"{hookName}.py");
            var ps1 = Path.Combine(hooksDir, $"{hookName}.ps1");
            if (File.Exists(sh)) { scriptPath = sh; runnerExe = "bash"; runnerArgs = $"\"{sh}\""; }
            else if (File.Exists(js)) { scriptPath = js; runnerExe = "node"; runnerArgs = $"\"{js}\""; }
            else if (File.Exists(py)) { scriptPath = py; runnerExe = "python3"; runnerArgs = $"\"{py}\""; }
            else if (File.Exists(ps1)) { scriptPath = ps1; runnerExe = "pwsh"; runnerArgs = $"-NoProfile -NonInteractive -File \"{ps1}\""; }
        }

        if (string.IsNullOrEmpty(scriptPath) || string.IsNullOrEmpty(runnerExe))
        {
            return (-1, "", $"未找到动作钩子脚本: {hookName}");
        }

        var effectiveTarget = explicitTarget;
        if (string.IsNullOrWhiteSpace(effectiveTarget) && parameters != null && parameters.TryGetValue("target", out var pTar))
        {
            effectiveTarget = pTar;
        }
        if (string.IsNullOrWhiteSpace(effectiveTarget))
        {
            var mPath = Path.Combine(dir, "manifest.json");
            if (File.Exists(mPath))
            {
                try
                {
                    using var mDoc = JsonDocument.Parse(File.ReadAllText(mPath));
                    if (mDoc.RootElement.TryGetProperty("target", out var pT)) effectiveTarget = pT.GetString();
                    else if (mDoc.RootElement.TryGetProperty("defaultTarget", out var pDT)) effectiveTarget = pDT.GetString();
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(effectiveTarget) && Path.GetFileName(dir).Equals("ssh-access-guard", StringComparison.OrdinalIgnoreCase))
            {
                effectiveTarget = "host";
            }
        }
        effectiveTarget = string.IsNullOrWhiteSpace(effectiveTarget) ? "container" : effectiveTarget.Trim().ToLowerInvariant();

        // 统一构造 ExtensionHookRequest 协议请求
        var opId = "op-" + Guid.NewGuid().ToString("N")[..8];
        var configDict = ReadExtensionConfig(dir);
        var hookReq = new ExtensionHookRequest
        {
            ProtocolVersion = 1,
            OperationId = opId,
            Action = hookName,
            Parameters = parameters ?? [],
            Config = configDict,
            Target = effectiveTarget,
            Timestamp = DateTime.UtcNow.ToString("o")
        };
        var requestJson = JsonSerializer.Serialize(hookReq, ServiceJsonContext.Default.ExtensionHookRequest);

        // 宿主模式穿透执行
        if (!isWindows && effectiveTarget == "host")
        {
            var scriptContent = await File.ReadAllTextAsync(scriptPath, cancellationToken);
            var envHeader = $"export LWT_TARGET=\"host\"\nexport LWT_IS_PRIVILEGED=\"1\"\nexport LWT_EXTENSION_ID=\"{EscapeCliArg(Path.GetFileName(dir))}\"\nexport LWT_ACTION=\"{EscapeCliArg(hookName)}\"\nexport LWT_OPERATION_ID=\"{opId}\"\n";
            if (parameters != null)
            {
                foreach (var kvp in parameters)
                {
                    envHeader += $"export LWT_PARAM_{EscapeCliArg(kvp.Key.ToUpperInvariant())}=\"{EscapeCliArg(kvp.Value)}\"\n";
                }
            }

            var runnerPrefix = scriptPath.EndsWith(".js") ? "node" : (scriptPath.EndsWith(".py") ? "python3" : "bash");
            var fullScript = envHeader + $"echo '{EscapeJson(requestJson)}' | {runnerPrefix} \"{scriptPath}\"";

            var res = await _shellExecutor.ExecuteAsync(new ShellRequest
            {
                ScriptText = fullScript,
                Target = "host",
                WorkingDirectory = "/",
                TimeoutSeconds = 15
            }, cancellationToken);

            return (res.ExitCode ?? -1, res.StandardOutput, res.ErrorOutput);
        }

        // 普通容器/本机模式执行：通过标准输入喂入 JSON，有界采集输出
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = runnerExe,
                Arguments = runnerArgs,
                WorkingDirectory = dir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            startInfo.EnvironmentVariables["LWT_ACTION"] = hookName;
            startInfo.EnvironmentVariables["LWT_OPERATION_ID"] = opId;
            startInfo.EnvironmentVariables["LWT_TARGET"] = effectiveTarget;
            startInfo.EnvironmentVariables["LWT_EXTENSION_ID"] = Path.GetFileName(dir);
            startInfo.EnvironmentVariables["LWT_DATA_DIR"] = dir;
            startInfo.EnvironmentVariables["LWT_CONFIG_PATH"] = Path.Combine(dir, "config.json");
            startInfo.EnvironmentVariables["LWT_RUN_ID"] = opId;

            if (parameters != null)
            {
                foreach (var (k, v) in parameters)
                {
                    startInfo.EnvironmentVariables[$"LWT_PARAM_{k.ToUpperInvariant()}"] = v;
                }
            }

            using var proc = new Process { StartInfo = startInfo };
            proc.Start();

            // 写入 stdin 协议 JSON 并关闭流
            await proc.StandardInput.WriteLineAsync(requestJson.AsMemory(), cancellationToken);
            proc.StandardInput.Close();

            const int maxHookOutputBytes = 256 * 1024; // 256 KiB
            var stdoutTask = ReadBoundedStream(proc.StandardOutput, maxHookOutputBytes);
            var stderrTask = ReadBoundedStream(proc.StandardError, maxHookOutputBytes);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { }
                return (-1, "", $"钩子动作 '{hookName}' 执行超时已终止。");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            return (proc.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, "", $"执行钩子进程异常: {ex.Message}");
        }
    }

    private static async Task<string> ReadBoundedStream(StreamReader reader, int maxBytes)
    {
        var buffer = new char[4096];
        var sb = new System.Text.StringBuilder();
        int bytes = 0;
        bool truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, 0, buffer.Length);
            if (read <= 0) break;
            if (!truncated)
            {
                var charBytes = System.Text.Encoding.UTF8.GetByteCount(buffer, 0, read);
                if (bytes + charBytes <= maxBytes)
                {
                    sb.Append(buffer, 0, read);
                    bytes += charBytes;
                }
                else
                {
                    truncated = true;
                    var rem = Math.Max(0, maxBytes - bytes);
                    if (rem > 0) sb.Append(buffer, 0, Math.Min(read, rem));
                }
            }
        }
        return sb.ToString();
    }

    private static IReadOnlyList<string> ProbeMissingRunners(IReadOnlyList<string> requiredRunners)
    {
        var missing = new List<string>();
        var isWindows = OperatingSystem.IsWindows();

        foreach (var r in requiredRunners)
        {
            var runner = r.ToLowerInvariant().Trim();
            if (runner == "sh" || runner == "bash")
            {
                if (isWindows)
                {
                    if (!File.Exists(@"C:\Program Files\Git\bin\bash.exe") && !ProbeCommand("bash --version")) missing.Add(runner);
                }
                else
                {
                    if (!File.Exists("/bin/bash") && !File.Exists("/bin/sh") && !File.Exists("/usr/bin/bash") && !File.Exists("/usr/bin/sh")) missing.Add(runner);
                }
            }
            else if (runner == "node" || runner == "nodejs" || runner == "js")
            {
                if (isWindows)
                {
                    if (!ProbeCommand("node -v")) missing.Add("node");
                }
                else
                {
                    if (!File.Exists("/usr/bin/node") && !File.Exists("/bin/node") && !File.Exists("/usr/local/bin/node")) missing.Add("node");
                }
            }
            else if (runner == "python" || runner == "python3" || runner == "py")
            {
                if (isWindows)
                {
                    if (!ProbeCommand("python --version")) missing.Add("python3");
                }
                else
                {
                    if (!File.Exists("/usr/bin/python3") && !File.Exists("/bin/python3")) missing.Add("python3");
                }
            }
        }

        return missing;
    }

    private static IReadOnlyList<string> ProbeMissingBinaries(IReadOnlyList<string> requiredBinaries)
    {
        var missing = new List<string>();
        var isWindows = OperatingSystem.IsWindows();

        foreach (var b in requiredBinaries)
        {
            var bin = b.Trim();
            if (isWindows)
            {
                if (!ProbeCommand($"{bin} --version") && !ProbeCommand($"where {bin}")) missing.Add(bin);
            }
            else
            {
                var paths = new[]
                {
                    $"/usr/bin/{bin}",
                    $"/bin/{bin}",
                    $"/usr/sbin/{bin}",
                    $"/sbin/{bin}",
                    $"/usr/local/bin/{bin}"
                };
                if (!paths.Any(File.Exists)) missing.Add(bin);
            }
        }

        return missing;
    }

    private static bool ProbeCommand(string cmd)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo.FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
            proc.StartInfo.Arguments = OperatingSystem.IsWindows() ? $"/c \"{cmd}\"" : $"-c \"{cmd}\"";
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.Start();
            var exited = proc.WaitForExit(1000);
            if (!exited)
            {
                try { proc.Kill(true); } catch { }
                return false;
            }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? ResolveInstallGuide(ExtensionManifest manifest, IReadOnlyList<string> missingRunners, IReadOnlyList<string> missingBinaries)
    {
        if (missingRunners.Count == 0 && missingBinaries.Count == 0) return null;

        var distroKey = "debian";
        if (OperatingSystem.IsWindows()) distroKey = "windows";
        else if (File.Exists("/etc/alpine-release")) distroKey = "alpine";
        else if (File.Exists("/etc/redhat-release")) distroKey = "rhel";

        if (manifest.InstallGuide.TryGetValue(distroKey, out var guide))
        {
            return guide;
        }

        var missingAll = missingRunners.Concat(missingBinaries).Distinct();
        if (OperatingSystem.IsWindows())
        {
            return $"winget install {string.Join(" ", missingAll)}";
        }
        return distroKey switch
        {
            "alpine" => $"apk add --no-cache {string.Join(" ", missingAll)}",
            "rhel" => $"dnf install -y {string.Join(" ", missingAll)}",
            _ => $"apt update && apt install -y {string.Join(" ", missingAll)}"
        };
    }
}
