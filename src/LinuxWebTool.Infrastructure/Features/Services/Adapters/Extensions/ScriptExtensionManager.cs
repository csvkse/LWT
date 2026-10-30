using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

/// <summary>
/// 动态脚本扩展管理器实现：
/// - 扫描本地 data/services/extensions/ 目录下的插件包；
/// - 基于 manifest.json 元数据与钩子脚本 (Bash/PowerShell/Python/Node) 动态承载自定义服务；
/// - 统一超时约束与安全防护。
/// </summary>
public sealed class ScriptExtensionManager : IScriptExtensionManager
{
    private readonly IShellExecutor _shellExecutor;
    private readonly ILogger<ScriptExtensionManager> _logger;
    private readonly string _baseExtensionsDir;

    public ScriptExtensionManager(IShellExecutor shellExecutor, ILogger<ScriptExtensionManager> logger, string? baseExtensionsDir = null)
    {
        _shellExecutor = shellExecutor;
        _logger = logger;
        _baseExtensionsDir = baseExtensionsDir ?? Path.Combine(AppContext.BaseDirectory, "data", "services", "extensions");
    }

    public async Task<IReadOnlyList<ExtensionInstance>> ListExtensionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_baseExtensionsDir))
        {
            Directory.CreateDirectory(_baseExtensionsDir);
            EnsureSampleExtensionCreated();
        }
        else if (!Directory.EnumerateFileSystemEntries(_baseExtensionsDir).Any())
        {
            EnsureSampleExtensionCreated();
        }

        var list = new List<ExtensionInstance>();
        var subDirs = Directory.GetDirectories(_baseExtensionsDir);

        foreach (var dir in subDirs)
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var id = root.TryGetProperty("id", out var pId) ? pId.GetString() ?? Path.GetFileName(dir) : Path.GetFileName(dir);
                var name = root.TryGetProperty("name", out var pName) ? pName.GetString() ?? id : id;
                var cat = root.TryGetProperty("category", out var pCat) ? pCat.GetString() ?? "custom" : "custom";
                var ver = root.TryGetProperty("version", out var pVer) ? pVer.GetString() ?? "1.0.0" : "1.0.0";
                var desc = root.TryGetProperty("description", out var pDesc) ? pDesc.GetString() : null;
                var icon = root.TryGetProperty("icon", out var pIcon) ? pIcon.GetString() : "🔌";

                var platforms = new List<string>();
                if (root.TryGetProperty("supportedPlatforms", out var pPlat) && pPlat.ValueKind == JsonValueKind.Array)
                {
                    foreach (var p in pPlat.EnumerateArray()) platforms.Add(p.GetString() ?? "");
                }
                if (platforms.Count == 0) platforms.AddRange(["linux", "windows"]);

                // 检查当前操作系统是否匹配
                var currentOs = OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsLinux() ? "linux" : "macos");
                if (!platforms.Contains(currentOs, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var formFields = new List<ExtensionFormField>();
                if (root.TryGetProperty("configForm", out var pForm) && pForm.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in pForm.EnumerateArray())
                    {
                        var field = f.TryGetProperty("field", out var pF) ? pF.GetString() ?? "" : "";
                        var label = f.TryGetProperty("label", out var pL) ? pL.GetString() ?? field : field;
                        var type = f.TryGetProperty("type", out var pT) ? pT.GetString() ?? "text" : "text";
                        var def = f.TryGetProperty("default", out var pD) ? pD.GetString() : null;
                        var req = f.TryGetProperty("required", out var pR) && pR.GetBoolean();
                        formFields.Add(new ExtensionFormField
                        {
                            Field = field,
                            Label = label,
                            Type = type,
                            DefaultValue = def,
                            Required = req
                        });
                    }
                }

                var manifest = new ExtensionManifest
                {
                    Id = id,
                    Name = name,
                    Category = cat,
                    Version = ver,
                    Description = desc,
                    Icon = icon,
                    SupportedPlatforms = platforms,
                    ConfigForm = formFields
                };

                // 执行 status 钩子获取真实运行状态
                var (status, statusMsg, pid) = await ProbeExtensionStatusAsync(dir, cancellationToken);

                // 读取 config.json
                var configDict = new Dictionary<string, string>();
                var configPath = Path.Combine(dir, "config.json");
                if (File.Exists(configPath))
                {
                    try
                    {
                        var cJson = await File.ReadAllTextAsync(configPath, cancellationToken);
                        using var cDoc = JsonDocument.Parse(cJson);
                        foreach (var prop in cDoc.RootElement.EnumerateObject())
                        {
                            configDict[prop.Name] = prop.Value.ToString();
                        }
                    }
                    catch { }
                }

                list.Add(new ExtensionInstance
                {
                    Manifest = manifest,
                    Status = status,
                    StatusMessage = statusMsg,
                    Pid = pid,
                    CurrentConfig = configDict
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load extension at {Dir}", dir);
            }
        }

        return list;
    }

    public async Task<ServiceActionResult> ExecuteExtensionActionAsync(ExtensionActionRequest request, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, request.ExtensionId);
        if (!Directory.Exists(dir))
        {
            return new ServiceActionResult { Success = false, Message = $"未找到扩展包: {request.ExtensionId}" };
        }

        if (request.Action.Equals("config_set", StringComparison.OrdinalIgnoreCase))
        {
            if (request.ConfigValues != null)
            {
                var configPath = Path.Combine(dir, "config.json");
                var json = SerializeDictionary(request.ConfigValues);
                await File.WriteAllTextAsync(configPath, json, cancellationToken);
            }

            // 若有 config_set 钩子则触发
            await RunHookScriptAsync(dir, "config_set", cancellationToken);
            return new ServiceActionResult { Success = true, Message = "配置已保存并同步给扩展服务" };
        }

        var actionVerb = request.Action.ToLowerInvariant();
        var (exitCode, stdout, stderr) = await RunHookScriptAsync(dir, actionVerb, cancellationToken);

        return new ServiceActionResult
        {
            Success = exitCode == 0,
            Message = exitCode == 0 ? $"操作 [{actionVerb}] 执行成功" : $"执行失败: {stderr}",
            Output = stdout
        };
    }

    public async Task<string> GetExtensionLogsAsync(string extensionId, int lines = 100, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, extensionId);
        if (!Directory.Exists(dir)) return "扩展包未找到";

        // 优先检查是否有 logs 钩子
        var (exitCode, stdout, _) = await RunHookScriptAsync(dir, "logs", cancellationToken);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            return stdout;
        }

        // 降级检查本地 service.log
        var logFile = Path.Combine(dir, "service.log");
        if (File.Exists(logFile))
        {
            try
            {
                var allLines = await File.ReadAllLinesAsync(logFile, cancellationToken);
                return string.Join(Environment.NewLine, allLines.TakeLast(lines));
            }
            catch (Exception ex)
            {
                return $"读取日志失败: {ex.Message}";
            }
        }

        return "暂无日志输出。可在扩展包下放置 logs 钩子脚本或写入 service.log。";
    }

    private async Task<(string Status, string? Message, int? Pid)> ProbeExtensionStatusAsync(string dir, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, _) = await RunHookScriptAsync(dir, "status", cancellationToken);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout.Trim());
                var st = doc.RootElement.TryGetProperty("status", out var pSt) ? pSt.GetString() ?? "Stopped" : "Stopped";
                var msg = doc.RootElement.TryGetProperty("message", out var pMsg) ? pMsg.GetString() : null;
                var pid = doc.RootElement.TryGetProperty("pid", out var pPid) ? pPid.GetInt32() : (int?)null;
                return (st, msg, pid);
            }
            catch
            {
                return (stdout.Trim().Contains("running", StringComparison.OrdinalIgnoreCase) ? "Running" : "Stopped", stdout.Trim(), null);
            }
        }
        return ("Stopped", "服务未运行", null);
    }

    private async Task<(int ExitCode, string StandardOutput, string StandardError)> RunHookScriptAsync(string dir, string hookName, CancellationToken cancellationToken)
    {
        var hooksDir = Path.Combine(dir, "hooks");
        if (!Directory.Exists(hooksDir)) hooksDir = dir;

        // 按优先级寻找匹配的执行脚本
        string? scriptPath = null;
        string runnerCmd = "";

        var isWindows = OperatingSystem.IsWindows();

        if (isWindows)
        {
            var ps1 = Path.Combine(hooksDir, $"{hookName}.ps1");
            var bat = Path.Combine(hooksDir, $"{hookName}.bat");
            if (File.Exists(ps1)) { scriptPath = ps1; runnerCmd = $"powershell -ExecutionPolicy Bypass -File \"{ps1}\""; }
            else if (File.Exists(bat)) { scriptPath = bat; runnerCmd = $"cmd /c \"{bat}\""; }
        }
        else
        {
            var sh = Path.Combine(hooksDir, $"{hookName}.sh");
            var py = Path.Combine(hooksDir, $"{hookName}.py");
            var js = Path.Combine(hooksDir, $"{hookName}.js");
            if (File.Exists(sh)) { scriptPath = sh; runnerCmd = $"bash \"{sh}\""; }
            else if (File.Exists(py)) { scriptPath = py; runnerCmd = $"python3 \"{py}\""; }
            else if (File.Exists(js)) { scriptPath = js; runnerCmd = $"node \"{js}\""; }
        }

        if (string.IsNullOrEmpty(scriptPath) || string.IsNullOrEmpty(runnerCmd))
        {
            return (-1, "", $"未找到钩子脚本: {hookName}");
        }

        var res = await _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = runnerCmd,
            WorkingDirectory = dir,
            TimeoutSeconds = 8
        }, cancellationToken);

        return (res.ExitCode ?? -1, res.StandardOutput, res.ErrorOutput);
    }

    private void EnsureSampleExtensionCreated()
    {
        try
        {
            var sampleDir = Path.Combine(_baseExtensionsDir, "sample-healthcheck");
            if (Directory.Exists(sampleDir)) return;
            Directory.CreateDirectory(sampleDir);
            var hooksDir = Path.Combine(sampleDir, "hooks");
            Directory.CreateDirectory(hooksDir);

            const string sampleManifestJson = """
{
  "id": "sample-healthcheck",
  "name": "系统健康守护扩展 (示例)",
  "category": "system",
  "version": "1.0.0",
  "description": "基于动态脚本扩展的系统自检守护服务示例",
  "icon": "🩺",
  "supportedPlatforms": ["linux", "windows"],
  "configForm": [
    { "field": "check_interval", "label": "巡检频率(秒)", "type": "number", "default": "60", "required": true }
  ]
}
""";

            File.WriteAllText(Path.Combine(sampleDir, "manifest.json"), sampleManifestJson);

            // 写入 Windows 与 Linux 示例 status 钩子
            File.WriteAllText(Path.Combine(hooksDir, "status.ps1"), "Write-Output '{\"status\":\"Running\",\"message\":\"Active & healthy\",\"pid\":9999}'");
            File.WriteAllText(Path.Combine(hooksDir, "status.sh"), "echo '{\"status\":\"Running\",\"message\":\"Active & healthy\",\"pid\":9999}'");
            File.WriteAllText(Path.Combine(hooksDir, "start.ps1"), "Write-Output 'Started'");
            File.WriteAllText(Path.Combine(hooksDir, "start.sh"), "echo 'Started'");
            File.WriteAllText(Path.Combine(hooksDir, "stop.ps1"), "Write-Output 'Stopped'");
            File.WriteAllText(Path.Combine(hooksDir, "stop.sh"), "echo 'Stopped'");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to create sample extension");
        }
    }

    private static string SerializeDictionary(Dictionary<string, string> dict)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("{");
        var first = true;
        foreach (var (k, v) in dict)
        {
            if (!first) sb.AppendLine(",");
            first = false;
            var safeKey = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(k);
            var safeVal = System.Text.Encodings.Web.JavaScriptEncoder.Default.Encode(v);
            sb.Append($"  \"{safeKey}\": \"{safeVal}\"");
        }
        sb.AppendLine();
        sb.Append('}');
        return sb.ToString();
    }
}
