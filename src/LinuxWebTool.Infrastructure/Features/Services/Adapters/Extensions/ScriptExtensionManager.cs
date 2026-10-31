using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

/// <summary>
/// 动态脚本扩展管理器实现：
/// - 扫描本地 data/services/extensions/ 目录下的插件包；
/// - 基于 manifest.json 元数据、声明式依赖检查与多语言钩子脚本 (Bash/Node/Python/PowerShell) 动态承载自定义服务；
/// - 深度支持环境依赖探针、缺失状态置灰与多发行版安装引导；
/// - 支持从 GitHub 仓库 (单仓库多模块 Mono-repo) 检索与批量导入插件，全流程抗路径穿透。
/// </summary>
public sealed partial class ScriptExtensionManager : IScriptExtensionManager
{
    private readonly IShellExecutor _shellExecutor;
    private readonly IExtensionRepoFetcher _repoFetcher;
    private readonly ILogger<ScriptExtensionManager> _logger;
    private readonly string _baseExtensionsDir;

    public ScriptExtensionManager(
        IShellExecutor shellExecutor,
        IExtensionRepoFetcher repoFetcher,
        ILogger<ScriptExtensionManager> logger,
        string? baseExtensionsDir = null)
    {
        _shellExecutor = shellExecutor;
        _repoFetcher = repoFetcher;
        _logger = logger;
        _baseExtensionsDir = baseExtensionsDir ?? Path.Combine(AppContext.BaseDirectory, "data", "services", "extensions");
    }

    public ScriptExtensionManager(
        IShellExecutor shellExecutor,
        ILogger<ScriptExtensionManager> logger,
        string? baseExtensionsDir = null)
        : this(shellExecutor, new CompositeExtensionRepoFetcher(shellExecutor, NullLogger<CompositeExtensionRepoFetcher>.Instance), logger, baseExtensionsDir)
    {
    }

    public async Task<IReadOnlyList<ExtensionInstance>> ListExtensionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_baseExtensionsDir))
        {
            Directory.CreateDirectory(_baseExtensionsDir);
            EnsureDefaultExtensionsCreated();
        }
        else if (!Directory.EnumerateFileSystemEntries(_baseExtensionsDir).Any())
        {
            EnsureDefaultExtensionsCreated();
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
                var manifest = ParseManifest(json, dir);
                if (manifest == null) continue;

                var currentOs = OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsLinux() ? "linux" : "macos");
                if (!manifest.SupportedPlatforms.Contains(currentOs, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                var missingRunners = ProbeMissingRunners(manifest.Dependencies.Runners);
                var missingBinaries = ProbeMissingBinaries(manifest.Dependencies.Binaries);

                var isEnabled = manifest.IsEnabled && !File.Exists(Path.Combine(dir, ".disabled"));
                string status;
                string? statusMsg;
                int? pid = null;

                if (!isEnabled)
                {
                    status = "Disabled";
                    statusMsg = "扩展服务已被显式禁用";
                }
                else if (missingRunners.Count > 0)
                {
                    status = "MissingRuntime";
                    statusMsg = $"缺少执行运行时: {string.Join(", ", missingRunners)}";
                }
                else if (missingBinaries.Count > 0)
                {
                    status = "MissingBinary";
                    statusMsg = $"缺少依赖程序: {string.Join(", ", missingBinaries)}";
                }
                else
                {
                    (status, statusMsg, pid) = await ProbeExtensionStatusAsync(dir, cancellationToken);
                }

                var installGuideText = ResolveInstallGuide(manifest, missingRunners, missingBinaries);
                var configDict = ReadExtensionConfig(dir);
                var source = await GetSourceMetadataAsync(manifest.Id, cancellationToken);

                list.Add(new ExtensionInstance
                {
                    Manifest = manifest,
                    Status = status,
                    StatusMessage = statusMsg,
                    Pid = pid,
                    CurrentConfig = configDict,
                    MissingRunners = missingRunners,
                    MissingBinaries = missingBinaries,
                    InstallGuideText = installGuideText,
                    IsEnabled = isEnabled,
                    Source = source
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

        if (File.Exists(Path.Combine(dir, ".disabled")))
        {
            return new ServiceActionResult { Success = false, Message = $"扩展服务 '{request.ExtensionId}' 已被显式禁用，禁止执行任何操作。" };
        }

        if (request.Action.Equals("config_set", StringComparison.OrdinalIgnoreCase))
        {
            if (request.ConfigValues != null)
            {
                var configPath = Path.Combine(dir, "config.json");
                var json = SerializeDictionary(request.ConfigValues);
                await File.WriteAllTextAsync(configPath, json, cancellationToken);
            }
            await RunHookScriptAsync(dir, "config_reload", null, cancellationToken);
            return new ServiceActionResult { Success = true, Message = "配置已更新" };
        }

        var effectiveTarget = request.Target ?? (request.ActionParams != null && request.ActionParams.TryGetValue("target", out var tVal) ? tVal : null);
        var (exitCode, stdout, stderr) = await RunHookScriptAsync(dir, request.Action, request.ActionParams, cancellationToken, effectiveTarget);
        var isSuccess = exitCode == 0;

        string? parsedMessage = null;
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout.Trim());
                if (doc.RootElement.TryGetProperty("message", out var pMsg))
                {
                    parsedMessage = pMsg.GetString();
                }
                if (doc.RootElement.TryGetProperty("success", out var pSucc))
                {
                    isSuccess = pSucc.GetBoolean();
                }
            }
            catch { }
        }

        return new ServiceActionResult
        {
            Success = isSuccess,
            Message = parsedMessage ?? (isSuccess ? "操作成功完成" : $"操作失败 (退出码 {exitCode}): {stderr}"),
            Output = stdout
        };
    }

    public async Task<string> GetExtensionLogsAsync(string extensionId, int lines = 100, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, extensionId);
        if (!Directory.Exists(dir))
        {
            return "未找到该扩展插件目录。";
        }

        var logPath = Path.Combine(dir, "service.log");
        if (!File.Exists(logPath))
        {
            var (exitCode, stdout, stderr) = await RunHookScriptAsync(dir, "logs", null, cancellationToken);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                return stdout;
            }
            return "暂无持久化日志记录文件 (service.log)。";
        }

        try
        {
            var allLines = await File.ReadAllLinesAsync(logPath, cancellationToken);
            var tailLines = allLines.TakeLast(Math.Max(10, lines));
            return string.Join(Environment.NewLine, tailLines);
        }
        catch (Exception ex)
        {
            return $"读取日志失败: {ex.Message}";
        }
    }

    #region 内部工具与探针实现
    private async Task<(string Status, string? Message, int? Pid)> ProbeExtensionStatusAsync(string dir, CancellationToken cancellationToken)
    {
        var (exitCode, stdout, stderr) = await RunHookScriptAsync(dir, "status", null, cancellationToken);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(stdout.Trim());
                var root = doc.RootElement;
                var stat = root.TryGetProperty("status", out var pStat) ? pStat.GetString() ?? "Running" : "Running";
                var msg = root.TryGetProperty("message", out var pMsg) ? pMsg.GetString() : null;
                int? pid = root.TryGetProperty("pid", out var pPid) && pPid.TryGetInt32(out var p) ? p : null;
                return (stat, msg, pid);
            }
            catch
            {
                return (stdout.Trim().Contains("running", StringComparison.OrdinalIgnoreCase) ? "Running" : "Stopped", stdout.Trim(), null);
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
        var hooksDir = Path.Combine(dir, "hooks");
        if (!Directory.Exists(hooksDir)) hooksDir = dir;

        string? scriptPath = null;
        string runnerCmd = "";
        var isWindows = OperatingSystem.IsWindows();

        if (isWindows)
        {
            var ps1 = Path.Combine(hooksDir, $"{hookName}.ps1");
            var bat = Path.Combine(hooksDir, $"{hookName}.bat");
            var js = Path.Combine(hooksDir, $"{hookName}.js");
            if (File.Exists(ps1)) { scriptPath = ps1; runnerCmd = $"powershell -ExecutionPolicy Bypass -File \"{ps1}\""; }
            else if (File.Exists(bat)) { scriptPath = bat; runnerCmd = $"cmd /c \"{bat}\""; }
            else if (File.Exists(js)) { scriptPath = js; runnerCmd = $"node \"{js}\""; }
        }
        else
        {
            var sh = Path.Combine(hooksDir, $"{hookName}.sh");
            var js = Path.Combine(hooksDir, $"{hookName}.js");
            var py = Path.Combine(hooksDir, $"{hookName}.py");
            if (File.Exists(sh)) { scriptPath = sh; runnerCmd = $"bash \"{sh}\""; }
            else if (File.Exists(js)) { scriptPath = js; runnerCmd = $"node \"{js}\""; }
            else if (File.Exists(py)) { scriptPath = py; runnerCmd = $"python3 \"{py}\""; }
        }

        if (string.IsNullOrEmpty(scriptPath) || string.IsNullOrEmpty(runnerCmd))
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
        }
        effectiveTarget = string.IsNullOrWhiteSpace(effectiveTarget) ? "container" : effectiveTarget.Trim().ToLowerInvariant();

        // 宿主模式穿透执行 (Linux 下通过 nsenter 执行脚本文本，零物理文件残留)
        if (!isWindows && effectiveTarget == "host")
        {
            var scriptContent = await File.ReadAllTextAsync(scriptPath, cancellationToken);
            // 注入通用运维环境变量与 nsenter 穿透前缀
            var envHeader = "export LWT_TARGET=\"host\"\nexport LWT_IS_PRIVILEGED=\"1\"\nexport LWT_NSENTER_PREFIX=\"\"\n";
            var fullScript = envHeader + scriptContent;

            var argsList = new List<string>();
            if (parameters != null)
            {
                foreach (var kvp in parameters)
                {
                    if (!kvp.Key.Equals("target", StringComparison.OrdinalIgnoreCase))
                    {
                        argsList.Add(kvp.Value);
                    }
                }
            }

            var res = await _shellExecutor.ExecuteAsync(new ShellRequest
            {
                ScriptText = fullScript,
                ScriptArguments = string.Join(" ", argsList.Select(a => $"\"{EscapeCliArg(a)}\"")),
                Target = "host",
                WorkingDirectory = "/",
                TimeoutSeconds = 15
            }, cancellationToken);

            return (res.ExitCode ?? -1, res.StandardOutput, res.ErrorOutput);
        }

        if (parameters != null)
        {
            foreach (var kvp in parameters)
            {
                if (kvp.Key.Equals("ip", StringComparison.OrdinalIgnoreCase))
                {
                    runnerCmd += $" \"{EscapeCliArg(kvp.Value)}\"";
                }
            }
        }

        var cmdRes = await _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = runnerCmd,
            WorkingDirectory = dir,
            TimeoutSeconds = 12
        }, cancellationToken);

        return (cmdRes.ExitCode ?? -1, cmdRes.StandardOutput, cmdRes.ErrorOutput);
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
            using var proc = new System.Diagnostics.Process();
            proc.StartInfo.FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "sh";
            proc.StartInfo.Arguments = OperatingSystem.IsWindows() ? $"/c \"{cmd}\"" : $"-c \"{cmd}\"";
            proc.StartInfo.RedirectStandardOutput = true;
            proc.StartInfo.RedirectStandardError = true;
            proc.StartInfo.UseShellExecute = false;
            proc.StartInfo.CreateNoWindow = true;
            proc.Start();
            return proc.WaitForExit(1000) && proc.ExitCode == 0;
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

    private static ExtensionManifest? ParseManifest(string json, string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var id = root.TryGetProperty("id", out var pId) ? pId.GetString() ?? Path.GetFileName(dir) : Path.GetFileName(dir);
            var name = root.TryGetProperty("name", out var pName) ? pName.GetString() ?? id : id;
            var cat = root.TryGetProperty("category", out var pCat) ? pCat.GetString() ?? "custom" : "custom";
            var ver = root.TryGetProperty("version", out var pVer) ? pVer.GetString() ?? "1.0.0" : "1.0.0";
            var desc = root.TryGetProperty("description", out var pDesc) ? pDesc.GetString() : null;
            var icon = root.TryGetProperty("icon", out var pIcon) ? pIcon.GetString() : "🔌";
            var target = root.TryGetProperty("target", out var pTar) ? pTar.GetString() ?? "container" : (root.TryGetProperty("defaultTarget", out var pDTar) ? pDTar.GetString() ?? "container" : "container");

            var platforms = new List<string>();
            if (root.TryGetProperty("supportedPlatforms", out var pPlat) && pPlat.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in pPlat.EnumerateArray()) platforms.Add(p.GetString() ?? "");
            }
            if (platforms.Count == 0) platforms.AddRange(["linux", "windows"]);

            var runners = new List<string>();
            var binaries = new List<string>();
            if (root.TryGetProperty("dependencies", out var pDep))
            {
                if (pDep.TryGetProperty("runners", out var pR) && pR.ValueKind == JsonValueKind.Array)
                {
                    foreach (var r in pR.EnumerateArray()) runners.Add(r.GetString() ?? "");
                }
                if (pDep.TryGetProperty("binaries", out var pB) && pB.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in pB.EnumerateArray()) binaries.Add(b.GetString() ?? "");
                }
            }

            var installGuide = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("installGuide", out var pGuide) && pGuide.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in pGuide.EnumerateObject())
                {
                    installGuide[p.Name] = p.Value.GetString() ?? "";
                }
            }

            var customActions = new List<ExtensionCustomAction>();
            if (root.TryGetProperty("customActions", out var pActs) && pActs.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in pActs.EnumerateArray())
                {
                    var action = a.TryGetProperty("action", out var pA) ? pA.GetString() ?? "" : "";
                    var label = a.TryGetProperty("label", out var pL) ? pL.GetString() ?? action : action;
                    var aIcon = a.TryGetProperty("icon", out var pI) ? pI.GetString() : "⚙️";
                    var aDesc = a.TryGetProperty("description", out var pD) ? pD.GetString() : null;
                    var reqParam = a.TryGetProperty("requireParam", out var pReq) && pReq.GetBoolean();
                    var pNameStr = a.TryGetProperty("paramName", out var pPn) ? pPn.GetString() : null;
                    var pPlace = a.TryGetProperty("paramPlaceholder", out var pPh) ? pPh.GetString() : null;

                    customActions.Add(new ExtensionCustomAction
                    {
                        Action = action,
                        Label = label,
                        Icon = aIcon,
                        Description = aDesc,
                        RequireParam = reqParam,
                        ParamName = pNameStr,
                        ParamPlaceholder = pPlace
                    });
                }
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

            return new ExtensionManifest
            {
                Id = id,
                Name = name,
                Category = cat,
                Version = ver,
                Description = desc,
                Icon = icon,
                SupportedPlatforms = platforms,
                Dependencies = new ExtensionDependencies { Runners = runners, Binaries = binaries },
                InstallGuide = installGuide,
                CustomActions = customActions,
                ConfigForm = formFields
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadExtensionConfig(string dir)
    {
        var configDict = new Dictionary<string, string>();
        var configPath = Path.Combine(dir, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var cJson = File.ReadAllText(configPath);
                using var cDoc = JsonDocument.Parse(cJson);
                foreach (var prop in cDoc.RootElement.EnumerateObject())
                {
                    configDict[prop.Name] = prop.Value.ToString();
                }
            }
            catch { }
        }
        return configDict;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            var destSub = Path.Combine(targetDir, Path.GetFileName(sub));
            Directory.CreateDirectory(destSub);
            CopyDirectoryRecursive(sub, destSub);
        }
    }

    private static string EscapeJson(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    private static string EscapeCliArg(string s) => s.Replace("\"", "\\\"").Replace(";", "").Replace("&", "").Replace("|", "");

    private static string SerializeDictionary(Dictionary<string, string> dict)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{\n");
        var first = true;
        foreach (var kvp in dict)
        {
            if (!first) sb.Append(",\n");
            sb.Append($"  \"{EscapeJson(kvp.Key)}\": \"{EscapeJson(kvp.Value)}\"");
            first = false;
        }
        sb.Append("\n}");
        return sb.ToString();
    }
    #endregion
}
