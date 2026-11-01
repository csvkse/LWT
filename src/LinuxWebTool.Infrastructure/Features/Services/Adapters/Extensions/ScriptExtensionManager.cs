using System.Collections.Concurrent;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using LinuxWebTool.Contracts.Shared.Support;
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
    private readonly string[] _fallbackExtensionsDirs;
    private readonly ConcurrentDictionary<string, (string Status, string? Message, int? Pid, DateTime ExpireAt)> _statusCache = new(StringComparer.OrdinalIgnoreCase);

    public ScriptExtensionManager(
        IShellExecutor shellExecutor,
        IExtensionRepoFetcher repoFetcher,
        ILogger<ScriptExtensionManager> logger,
        string? baseExtensionsDir = null,
        string? contentRoot = null,
        IReadOnlyList<string>? fallbackExtensionsDirs = null)
    {
        _shellExecutor = shellExecutor;
        _repoFetcher = repoFetcher;
        _logger = logger;
        _baseExtensionsDir = baseExtensionsDir ?? Path.Combine(AppContext.BaseDirectory, "data", "services", "extensions");

        var fallbacks = new List<string>();
        if (fallbackExtensionsDirs != null)
        {
            foreach (var f in fallbackExtensionsDirs)
            {
                if (!string.IsNullOrWhiteSpace(f) && Directory.Exists(f) && !fallbacks.Contains(f, StringComparer.OrdinalIgnoreCase) && !string.Equals(f, _baseExtensionsDir, StringComparison.OrdinalIgnoreCase))
                {
                    fallbacks.Add(f);
                }
            }
        }
        if (!string.IsNullOrWhiteSpace(contentRoot))
        {
            var cExt = Path.Combine(contentRoot, "extensions");
            if (Directory.Exists(cExt) && !string.Equals(cExt, _baseExtensionsDir, StringComparison.OrdinalIgnoreCase) && !fallbacks.Contains(cExt, StringComparer.OrdinalIgnoreCase))
            {
                fallbacks.Add(cExt);
            }
            var repoExt = Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "extensions"));
            if (Directory.Exists(repoExt) && !fallbacks.Contains(repoExt, StringComparer.OrdinalIgnoreCase) && !string.Equals(repoExt, _baseExtensionsDir, StringComparison.OrdinalIgnoreCase))
            {
                fallbacks.Add(repoExt);
            }
        }
        var appExt = Path.Combine(AppContext.BaseDirectory, "extensions");
        if (Directory.Exists(appExt) && !string.Equals(appExt, _baseExtensionsDir, StringComparison.OrdinalIgnoreCase) && !fallbacks.Contains(appExt, StringComparer.OrdinalIgnoreCase))
        {
            fallbacks.Add(appExt);
        }
        _fallbackExtensionsDirs = [.. fallbacks];
    }

    public ScriptExtensionManager(
        IShellExecutor shellExecutor,
        ILogger<ScriptExtensionManager> logger,
        string? baseExtensionsDir = null,
        string? contentRoot = null,
        IReadOnlyList<string>? fallbackExtensionsDirs = null)
        : this(shellExecutor, new CompositeExtensionRepoFetcher(shellExecutor, NullLogger<CompositeExtensionRepoFetcher>.Instance), logger, baseExtensionsDir, contentRoot, fallbackExtensionsDirs)
    {
    }

    public string ResolveExtensionDirectory(string extensionId)
    {
        var primaryDir = PluginSecurityGuard.EnsureSafePluginDirectory(_baseExtensionsDir, extensionId);
        if (Directory.Exists(primaryDir))
        {
            return primaryDir;
        }

        foreach (var fallback in _fallbackExtensionsDirs)
        {
            if (Directory.Exists(fallback))
            {
                var candidate = Path.Combine(fallback, extensionId);
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "manifest.json")))
                {
                    return candidate;
                }
            }
        }

        return primaryDir;
    }

    public string? ResolveExtensionAssetPath(string extensionId, string? relativePath)
    {
        if (!PluginSecurityGuard.IsValidPluginId(extensionId)) return null;

        var extDir = ResolveExtensionDirectory(extensionId);
        if (!Directory.Exists(extDir)) return null;

        var cleanRel = string.IsNullOrWhiteSpace(relativePath) ? "index.html" : relativePath.Trim().TrimStart('/', '\\');
        if (cleanRel.Contains("..")) return null;

        var candidates = new[]
        {
            Path.Combine(extDir, "ui", cleanRel),
            Path.Combine(extDir, "assets", cleanRel),
            Path.Combine(extDir, cleanRel)
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                var full = Path.GetFullPath(c);
                var fullExtDir = Path.GetFullPath(extDir);
                if (full.StartsWith(fullExtDir, StringComparison.OrdinalIgnoreCase))
                {
                    return full;
                }
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<ExtensionInstance>> ListExtensionsAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_baseExtensionsDir))
        {
            try { Directory.CreateDirectory(_baseExtensionsDir); } catch { }
            EnsureDefaultExtensionsCreated();
        }
        else if (!Directory.EnumerateFileSystemEntries(_baseExtensionsDir).Any())
        {
            EnsureDefaultExtensionsCreated();
        }

        var list = new List<ExtensionInstance>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var searchDirs = new List<string>();
        if (Directory.Exists(_baseExtensionsDir))
        {
            searchDirs.AddRange(Directory.GetDirectories(_baseExtensionsDir));
        }
        foreach (var fallback in _fallbackExtensionsDirs)
        {
            if (Directory.Exists(fallback))
            {
                searchDirs.AddRange(Directory.GetDirectories(fallback));
            }
        }

        foreach (var dir in searchDirs)
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var manifest = ParseManifest(json, dir);
                if (manifest == null) continue;
                if (!seenIds.Add(manifest.Id)) continue;

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
                    var now = DateTime.UtcNow;
                    if (_statusCache.TryGetValue(manifest.Id, out var cached) && now < cached.ExpireAt)
                    {
                        status = cached.Status;
                        statusMsg = cached.Message;
                        pid = cached.Pid;
                    }
                    else
                    {
                        (status, statusMsg, pid) = await ProbeExtensionStatusAsync(dir, cancellationToken);
                        _statusCache[manifest.Id] = (status, statusMsg, pid, now.AddSeconds(15));
                    }
                }

                var installGuideText = ResolveInstallGuide(manifest, missingRunners, missingBinaries);
                var configDict = ReadExtensionConfig(dir);
                var source = await GetSourceMetadataAsync(manifest.Id, cancellationToken);
                var hasUi = manifest.Ui != null || File.Exists(Path.Combine(dir, "ui", "index.html")) || File.Exists(Path.Combine(dir, "assets", "index.html"));
                int? port = manifest.Port;
                string? accessUrl = (port.HasValue && port.Value > 0) ? $"/ext/{manifest.Id}/" : null;

                list.Add(new ExtensionInstance
                {
                    Manifest = manifest,
                    Status = status,
                    StatusMessage = statusMsg,
                    Pid = pid,
                    Port = port,
                    AccessUrl = accessUrl,
                    CurrentConfig = configDict,
                    MissingRunners = missingRunners,
                    MissingBinaries = missingBinaries,
                    InstallGuideText = installGuideText,
                    IsEnabled = isEnabled,
                    HasUi = hasUi,
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

    public async Task<ExtensionInstance?> GetExtensionAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(extensionId)) return null;
        var list = await ListExtensionsAsync(cancellationToken);
        return list.FirstOrDefault(x => string.Equals(x.Manifest.Id, extensionId, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ServiceActionResult> ExecuteExtensionActionAsync(ExtensionActionRequest request, CancellationToken cancellationToken = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(request.ExtensionId))
        {
            return new ServiceActionResult { Success = false, Message = $"非法扩展 ID: {request.ExtensionId}" };
        }
        if (!PluginSecurityGuard.IsValidActionName(request.Action))
        {
            return new ServiceActionResult { Success = false, Message = $"非法动作名称: {request.Action}" };
        }

        string dir;
        try
        {
            dir = ResolveExtensionDirectory(request.ExtensionId);
        }
        catch (Exception ex)
        {
            return new ServiceActionResult { Success = false, Message = ex.Message };
        }

        if (!Directory.Exists(dir))
        {
            return new ServiceActionResult { Success = false, Message = $"未找到扩展包: {request.ExtensionId}" };
        }

        if (File.Exists(Path.Combine(dir, ".disabled")))
        {
            return new ServiceActionResult { Success = false, Message = $"扩展服务 '{request.ExtensionId}' 已被显式禁用，禁止执行任何操作。" };
        }

        // 每次执行动作均使对应实例的状态缓存失效
        _statusCache.TryRemove(request.ExtensionId, out _);

        if (request.Action.Equals("config_set", StringComparison.OrdinalIgnoreCase))
        {
            if (request.ConfigValues != null)
            {
                var configPath = Path.Combine(dir, "config.json");
                var json = SerializeDictionary(request.ConfigValues);
                await File.WriteAllTextAsync(configPath, json, cancellationToken);
            }

            var reloadHooks = new[] { "config_reload.ps1", "config_reload.sh", "config_reload.js", "config_reload.py", "config_reload.bat" };
            var hooksSubdir = Path.Combine(dir, "hooks");
            var hasReload = reloadHooks.Any(h => File.Exists(Path.Combine(dir, h)) || File.Exists(Path.Combine(hooksSubdir, h)));

            if (hasReload)
            {
                var (reloadExit, reloadStdout, reloadStderr) = await RunHookScriptAsync(dir, "config_reload", null, cancellationToken);
                if (reloadExit != 0)
                {
                    return new ServiceActionResult
                    {
                        Success = false,
                        Message = $"配置已保存，但重载配置生效失败 (退出码 {reloadExit}): {(string.IsNullOrWhiteSpace(reloadStderr) ? reloadStdout : reloadStderr)}",
                        Output = reloadStdout
                    };
                }
            }
            return new ServiceActionResult { Success = true, Message = "配置已更新" };
        }

        var effectiveTarget = request.Target ?? (request.ActionParams != null && request.ActionParams.TryGetValue("target", out var tVal) ? tVal : null);
        var (exitCode, stdout, stderr) = await RunHookScriptAsync(dir, request.Action, request.ActionParams, cancellationToken, effectiveTarget);

        // A03: 非零退出码必须判定为失败，严禁被 stdout 中的 success:true 掩盖
        if (exitCode != 0)
        {
            return new ServiceActionResult
            {
                Success = false,
                Message = $"操作失败 (退出码 {exitCode}): {(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr)}",
                Output = stdout
            };
        }

        // 解析规范协议响应
        ExtensionHookResponse? hookResp = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                hookResp = JsonSerializer.Deserialize(stdout.Trim(), ServiceJsonContext.Default.ExtensionHookResponse);
            }
        }
        catch (JsonException ex)
        {
            return new ServiceActionResult
            {
                Success = false,
                Message = $"协议错误 (ProtocolError): 钩子返回非规范 JSON 响应 ({ex.Message})",
                Output = stdout
            };
        }

        if (hookResp == null)
        {
            return new ServiceActionResult
            {
                Success = false,
                Message = "协议错误 (ProtocolError): 钩子未返回有效响应协议",
                Output = stdout
            };
        }

        var isSucceeded = string.Equals(hookResp.Status, "succeeded", StringComparison.OrdinalIgnoreCase);
        return new ServiceActionResult
        {
            Success = isSucceeded,
            Message = hookResp.Message ?? hookResp.Error ?? (isSucceeded ? "操作成功完成" : "操作返回失败状态"),
            Output = stdout
        };
    }

    public async Task<string> GetExtensionLogsAsync(string extensionId, int lines = 100, CancellationToken cancellationToken = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(extensionId))
        {
            return "未找到该扩展插件目录。";
        }

        var dir = ResolveExtensionDirectory(extensionId);
        if (!Directory.Exists(dir))
        {
            return "未找到该扩展插件目录。";
        }

        var logPath = Path.Combine(dir, "service.log");
        if (!File.Exists(logPath))
        {
            var (exitCode, stdout, _) = await RunHookScriptAsync(dir, "logs", null, cancellationToken);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                return stdout;
            }
            return "暂无持久化日志记录文件 (service.log)。";
        }

        try
        {
            // 内存有界读取：读取文件尾部最多 1 MiB，避免大日志导致内存暴涨
            const long maxReadBytes = 1024 * 1024;
            var fileInfo = new FileInfo(logPath);
            var fileLength = fileInfo.Length;
            var readOffset = Math.Max(0, fileLength - maxReadBytes);

            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(readOffset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
            var content = await reader.ReadToEndAsync(cancellationToken);

            var allLines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var tailLines = allLines.TakeLast(Math.Max(10, lines));
            return string.Join(Environment.NewLine, tailLines);
        }
        catch (Exception ex)
        {
            return $"读取日志失败: {ex.Message}";
        }
    }

    #region 内部工具与探针实现







    private static ExtensionManifest? ParseManifest(string json, string dir)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("manifestVersion", out var pMv) || pMv.GetInt32() != 1)
            {
                return null;
            }

            var hostApi = root.TryGetProperty("hostApiVersion", out var pHav) ? pHav.GetString() ?? "1" : "1";
            if (hostApi != "1")
            {
                return null;
            }

            var id = root.TryGetProperty("id", out var pId) ? pId.GetString() ?? Path.GetFileName(dir) : Path.GetFileName(dir);
            if (!PluginSecurityGuard.IsValidPluginId(id))
            {
                return null;
            }
            var name = root.TryGetProperty("name", out var pName) ? pName.GetString() ?? id : id;
            var cat = root.TryGetProperty("category", out var pCat) ? pCat.GetString() ?? "custom" : "custom";
            var ver = root.TryGetProperty("version", out var pVer) ? pVer.GetString() ?? "1.0.0" : "1.0.0";
            var desc = root.TryGetProperty("description", out var pDesc) ? pDesc.GetString() : null;
            var author = root.TryGetProperty("author", out var pAuthor) ? pAuthor.GetString() : null;
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
                    if (!PluginSecurityGuard.IsValidActionName(action)) continue;
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

            var caps = new ExtensionCapabilities();
            if (root.TryGetProperty("capabilities", out var pCaps) && pCaps.ValueKind == JsonValueKind.Object)
            {
                var life = !pCaps.TryGetProperty("lifecycle", out var pLife) || pLife.GetBoolean();
                var conf = pCaps.TryGetProperty("config", out var pConf) && pConf.GetBoolean();
                var logs = !pCaps.TryGetProperty("logs", out var pLogs) || pLogs.GetBoolean();
                caps = new ExtensionCapabilities { Lifecycle = life, Config = conf, Logs = logs };
            }

            ExtensionUiConfig? uiConfig = null;
            if (root.TryGetProperty("ui", out var pUi) && pUi.ValueKind == JsonValueKind.Object)
            {
                var entry = pUi.TryGetProperty("entry", out var pEnt) ? pEnt.GetString() ?? "index.html" : "index.html";
                var title = pUi.TryGetProperty("title", out var pTit) ? pTit.GetString() : null;
                var uIcon = pUi.TryGetProperty("icon", out var pUIc) ? pUIc.GetString() : null;
                var mode = pUi.TryGetProperty("mode", out var pMod) ? pMod.GetString() ?? "webview" : "webview";
                uiConfig = new ExtensionUiConfig { Entry = entry, Title = title, Icon = uIcon, Mode = mode };
            }

            int? port = null;
            if (root.TryGetProperty("port", out var pPort) && pPort.TryGetInt32(out var parsedPort))
            {
                port = parsedPort;
            }

            var isEnabled = !File.Exists(Path.Combine(dir, ".disabled"));

            return new ExtensionManifest
            {
                ManifestVersion = 1,
                HostApiVersion = hostApi,
                Id = id,
                Name = name,
                Category = cat,
                Version = ver,
                Author = author,
                Description = desc,
                Icon = icon,
                IsEnabled = isEnabled,
                Capabilities = caps,
                SupportedPlatforms = platforms,
                Dependencies = new ExtensionDependencies { Runners = runners, Binaries = binaries },
                InstallGuide = installGuide,
                CustomActions = customActions,
                ConfigForm = formFields,
                Ui = uiConfig,
                Target = target,
                Port = port
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
