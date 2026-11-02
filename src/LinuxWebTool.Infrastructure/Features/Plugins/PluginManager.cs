using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using LinuxWebTool.Infrastructure.Features.Plugins.Strategies;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

/// <summary>
/// 统一插件中枢管理器（负责常驻微服务与脚本扩展的一体化生命周期编排与调度）。
/// </summary>
public sealed partial class PluginManager : IPluginManager, IHostedService
{
    private readonly string _pluginsRoot;
    private readonly ILogger<PluginManager>? _logger;
    private readonly ConcurrentDictionary<PluginRuntime, IRuntimeAdapter> _adapters = new();
    private readonly ConcurrentDictionary<string, PluginStateMachine> _stateMachines = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, PluginProcessRingBuffer> _logBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly PluginPortManager _portManager = new();
    private readonly ResidentExecutionStrategy _residentStrategy = new();
    private readonly OneShotExecutionStrategy _oneShotStrategy = new();

    private CancellationTokenSource? _supervisorCts;
    private Task? _supervisorTask;

    public PluginManager(IHostEnvironment hostEnv, ILogger<PluginManager>? logger = null)
        : this(hostEnv.ContentRootPath, logger)
    {
    }

    public PluginManager(ILogger<PluginManager>? logger, string baseDir)
        : this(baseDir, logger)
    {
    }

    public PluginManager(string baseDir, ILogger<PluginManager>? logger = null)
    {
        _pluginsRoot = Path.Combine(baseDir, "data", "plugins");
        _logger = logger;

        RegisterAdapter(new NodeRuntimeAdapter());
        RegisterAdapter(new PythonRuntimeAdapter());
        RegisterAdapter(new DotNetRuntimeAdapter());
        RegisterAdapter(new ShellRuntimeAdapter());
        RegisterAdapter(new PowerShellRuntimeAdapter());
        RegisterAdapter(new NativeRuntimeAdapter());
        RegisterAdapter(new GoRuntimeAdapter());

        EnsureDirectoryExists();
        EnsureDefaultPluginsCreated();
    }

    private void RegisterAdapter(IRuntimeAdapter adapter)
    {
        _adapters[adapter.RuntimeType] = adapter;
    }

    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_pluginsRoot))
        {
            Directory.CreateDirectory(_pluginsRoot);
        }
    }

    public async ValueTask<IReadOnlyList<PluginInstanceDto>> GetAllAsync(PluginProfile? profileFilter = null, CancellationToken ct = default)
    {
        EnsureDirectoryExists();
        var dirs = Directory.GetDirectories(_pluginsRoot);
        var result = new List<PluginInstanceDto>(dirs.Length);

        foreach (var dir in dirs)
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath)) continue;

            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, ct);
                var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
                if (manifest == null) continue;

                if (profileFilter.HasValue && manifest.Profile != profileFilter.Value)
                {
                    continue;
                }

                result.Add(BuildInstanceDto(manifest, dir));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "读取插件清单异常: {Dir}", dir);
            }
        }

        return result;
    }

    public async ValueTask<PluginInstanceDto?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return null;

        var dir = Path.Combine(_pluginsRoot, id);
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath)) return null;

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
        if (manifest == null) return null;

        return BuildInstanceDto(manifest, dir);
    }

    public async ValueTask<bool> StartAsync(string id, CancellationToken ct = default)
    {
        var (success, _) = await StartWithResultAsync(id, ct);
        return success;
    }

    public async ValueTask<(bool Success, string? ErrorMessage)> StartWithResultAsync(string id, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return (false, "非法插件 ID");

        var dir = Path.Combine(_pluginsRoot, id);
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath)) return (false, "插件清单不存在");

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
        if (manifest == null) return (false, "解析插件清单失败");

        if (File.Exists(Path.Combine(dir, ".disabled")))
        {
            return (false, "插件已被显式禁用");
        }

        var sm = GetOrCreateStateMachine(manifest);
        var ringBuffer = GetOrCreateLogBuffer(id);
        var strategy = GetStrategy(manifest.Profile);

        // 清理人工停止标记
        var manualStopFlag = Path.Combine(dir, ".manual_stop");
        if (File.Exists(manualStopFlag))
        {
            try { File.Delete(manualStopFlag); } catch { }
        }

        return await strategy.StartAsync(manifest, dir, sm, _portManager, ringBuffer, _adapters, ct);
    }

    public ValueTask<bool> StopAsync(string id, CancellationToken ct = default) => StopAsync(id, isManual: false, ct);

    public async ValueTask<bool> StopAsync(string id, bool isManual, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return false;

        var dir = Path.Combine(_pluginsRoot, id);
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath)) return false;

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
        if (manifest == null) return false;

        var sm = GetOrCreateStateMachine(manifest);
        var strategy = GetStrategy(manifest.Profile);

        var stopped = await strategy.StopAsync(manifest, dir, sm, _portManager, ct);
        if (stopped && isManual)
        {
            try
            {
                var manualStopPath = Path.Combine(dir, ".manual_stop");
                await File.WriteAllTextAsync(manualStopPath, DateTimeOffset.UtcNow.ToString("O"), ct);
                _logger?.LogInformation("插件 '{Id}' 已记录人工停止标记 (.manual_stop)", id);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "为插件 '{Id}' 写入 .manual_stop 失败", id);
            }
        }

        return stopped;
    }

    public async ValueTask<bool> RestartAsync(string id, CancellationToken ct = default)
    {
        await StopAsync(id, ct);
        await Task.Delay(500, ct);
        return await StartAsync(id, ct);
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return false;

        await StopAsync(id, ct);

        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }

        _stateMachines.TryRemove(id, out _);
        _logBuffers.TryRemove(id, out _);
        _portManager.ReleasePort(id);

        return true;
    }

    public async ValueTask<PluginInstanceDto> CreateOrUpdateAsync(PluginCreateOrUpdateRequest request, CancellationToken ct = default)
    {
        var manifest = request.Manifest;
        if (!PluginSecurityGuard.IsValidPluginId(manifest.Id))
        {
            throw new ArgumentException($"非法插件 ID: {manifest.Id}");
        }

        var dir = Path.Combine(_pluginsRoot, manifest.Id);
        Directory.CreateDirectory(dir);

        var manifestJson = JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest);
        await File.WriteAllTextAsync(Path.Combine(dir, "plugin.json"), manifestJson, ct);

        if (!string.IsNullOrWhiteSpace(request.InitialCode) && !string.IsNullOrWhiteSpace(manifest.Entrypoint))
        {
            var entryPath = Path.Combine(dir, manifest.Entrypoint);
            var entryDir = Path.GetDirectoryName(entryPath);
            if (!string.IsNullOrEmpty(entryDir) && !Directory.Exists(entryDir))
            {
                Directory.CreateDirectory(entryDir);
            }
            await File.WriteAllTextAsync(entryPath, request.InitialCode, ct);
        }

        return BuildInstanceDto(manifest, dir);
    }

    public async ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return false;

        var dir = Path.Combine(_pluginsRoot, id);
        if (!Directory.Exists(dir)) return false;

        var flagPath = Path.Combine(dir, ".disabled");
        if (enabled)
        {
            if (File.Exists(flagPath)) File.Delete(flagPath);
        }
        else
        {
            await StopAsync(id, ct);
            await File.WriteAllTextAsync(flagPath, DateTime.UtcNow.ToString("O"), ct);
        }

        return true;
    }

    public string? ResolvePluginAssetPath(string id, string? relativePath)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return null;

        var pluginDir = Path.Combine(_pluginsRoot, id);
        if (!Directory.Exists(pluginDir)) return null;

        var decoded = string.IsNullOrWhiteSpace(relativePath) ? "index.html" : System.Net.WebUtility.UrlDecode(relativePath.Trim()).TrimStart('/', '\\').Replace('\\', '/');
        if (decoded.Contains("..")) return null;

        var stripped = decoded.StartsWith("ui/", StringComparison.OrdinalIgnoreCase) ? decoded[3..] : decoded;

        var candidates = new[]
        {
            Path.Combine(pluginDir, "ui", stripped),
            Path.Combine(pluginDir, "assets", stripped),
            Path.Combine(pluginDir, decoded),
            Path.Combine(pluginDir, "ui", decoded)
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                var full = Path.GetFullPath(c);
                var fullPluginDir = Path.GetFullPath(pluginDir);
                if (full.StartsWith(fullPluginDir, StringComparison.OrdinalIgnoreCase))
                {
                    return full;
                }
            }
        }

        return null;
    }

    private IExecutionProfileStrategy GetStrategy(PluginProfile profile) =>
        profile == PluginProfile.Resident ? _residentStrategy : _oneShotStrategy;

    private PluginStateMachine GetOrCreateStateMachine(PluginManifest manifest)
    {
        return _stateMachines.GetOrAdd(manifest.Id, _ => new PluginStateMachine(manifest.Id, manifest.Supervision));
    }

    private PluginProcessRingBuffer GetOrCreateLogBuffer(string pluginId)
    {
        return _logBuffers.GetOrAdd(pluginId, _ => new PluginProcessRingBuffer(1000));
    }

    private PluginInstanceDto BuildInstanceDto(PluginManifest manifest, string dir)
    {
        var isDisabled = File.Exists(Path.Combine(dir, ".disabled"));
        var sm = GetOrCreateStateMachine(manifest);

        string? missingReason = null;
        if (_adapters.TryGetValue(manifest.Runtime, out var adapter))
        {
            adapter.CheckAvailable(out missingReason);
        }

        var rawDeps = manifest.GetUnifiedDependencies();
        var evaluatedDeps = new List<PluginDependencyConfig>(rawDeps.Count);
        string? missingDepMsg = null;
        foreach (var dep in rawDeps)
        {
            var cmd = !string.IsNullOrWhiteSpace(dep.Probe?.Command) ? dep.Probe.Command : dep.Id;
            var isInstalled = CheckBinaryAvailable(cmd, dep.Probe?.Args);
            evaluatedDeps.Add(dep with { IsInstalled = isInstalled });
            if (dep.Required && !isInstalled && missingDepMsg == null)
            {
                missingDepMsg = $"缺少必要二进制依赖: {dep.Name}";
            }
        }

        var state = isDisabled ? PluginState.Disabled : sm.State;
        if (!isDisabled)
        {
            if (missingReason != null)
            {
                state = PluginState.MissingRuntime;
            }
            else if (missingDepMsg != null && state is PluginState.Stopped or PluginState.MissingDependencies)
            {
                state = PluginState.MissingDependencies;
            }
        }

        var portAlloc = _portManager.GetAllocation(manifest.Id);
        int? allocatedPort = portAlloc?.Port ?? _portManager.GetPort(manifest.Id);
        int? pid = manifest.Profile == PluginProfile.Resident ? _residentStrategy.GetProcessId(manifest.Id) : null;
        string? internalSecret = manifest.Profile == PluginProfile.Resident ? _residentStrategy.GetInternalSecret(manifest.Id) : null;

        string? accessUrl = null;
        if (manifest.Network?.Mode == "path")
        {
            accessUrl = manifest.Network.PathPrefix ?? $"/plugins/{manifest.Id}";
        }
        else if (manifest.Network?.Mode == "port" && allocatedPort.HasValue)
        {
            accessUrl = $"http://localhost:{allocatedPort.Value}";
        }

        return new PluginInstanceDto
        {
            Manifest = manifest with { IsEnabled = !isDisabled },
            State = state,
            AllocatedPort = allocatedPort,
            IsPortAvoided = portAlloc?.WasAvoided ?? false,
            ConfiguredPort = manifest.Network?.Port,
            PortConflictReason = portAlloc?.ConflictReason,
            ProcessId = pid,
            AccessUrl = accessUrl,
            InternalSecret = internalSecret,
            StatusMessage = sm.LastError ?? (isDisabled ? "已显式禁用" : (missingReason ?? missingDepMsg)),
            CrashCount = sm.FastCrashCount,
            StartedAt = sm.StartedAt.HasValue ? new DateTimeOffset(sm.StartedAt.Value, TimeSpan.Zero) : null,
            IsMissingRuntime = missingReason != null,
            MissingRuntimeMessage = missingReason,
            IsMissingDependencies = missingDepMsg != null,
            MissingDependenciesMessage = missingDepMsg,
            EffectiveInstallGuide = missingReason != null && adapter != null ? adapter.ResolveInstallGuide(DetectCurrentDistroKey()) : null,
            Dependencies = evaluatedDeps
        };
    }

    public ValueTask<PluginPortCheckResult> CheckPortAsync(string id, int port, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id))
        {
            return ValueTask.FromResult(new PluginPortCheckResult
            {
                Port = port,
                IsAvailable = false,
                Message = "非法插件 ID",
                RecommendedPort = _portManager.GetRecommendedPort()
            });
        }

        var (isAvail, occupiedBy) = _portManager.CheckPort(port, id);
        var recommended = isAvail ? port : _portManager.GetRecommendedPort(port);
        var msg = isAvail
            ? $"端口 {port} 当前空闲可用"
            : $"端口 {port} 当前不可用: {occupiedBy}";

        return ValueTask.FromResult(new PluginPortCheckResult
        {
            Port = port,
            IsAvailable = isAvail,
            OccupiedBy = occupiedBy,
            RecommendedPort = recommended,
            Message = msg
        });
    }

    public async ValueTask<PluginPortSwitchResult> SwitchPortAsync(string id, int newPort, bool restartIfRunning = true, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id))
        {
            return new PluginPortSwitchResult { Success = false, Message = "非法插件 ID" };
        }

        if (newPort < 1024 || newPort > 65535)
        {
            return new PluginPortSwitchResult { Success = false, Message = "目标端口超出有效范围 (1024-65535)" };
        }

        var dir = Path.Combine(_pluginsRoot, id);
        var manifestPath = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            return new PluginPortSwitchResult { Success = false, Message = $"未找到插件定义: {id}" };
        }

        var json = await File.ReadAllTextAsync(manifestPath, ct);
        var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
        if (manifest == null)
        {
            return new PluginPortSwitchResult { Success = false, Message = "解析插件配置清单失败" };
        }

        var (isAvail, occupiedBy) = _portManager.CheckPort(newPort, id);
        if (!isAvail)
        {
            return new PluginPortSwitchResult
            {
                Success = false,
                PreviousPort = manifest.Network?.Port ?? 0,
                NewPort = newPort,
                Message = $"目标端口 {newPort} 已被占用 ({occupiedBy})，请选择其他端口或推荐端口"
            };
        }

        var prevPort = manifest.Network?.Port ?? 0;
        var updatedNetwork = (manifest.Network ?? new PluginNetworkConfig { Mode = "port" }) with { Port = newPort };

        var updatedArgs = manifest.Arguments != null
            ? manifest.Arguments.Select(a =>
            {
                if (prevPort > 0 && a.Contains($":{prevPort}"))
                {
                    return a.Replace($":{prevPort}", $":{newPort}");
                }
                return a;
            }).ToList()
            : [];

        var updatedManifest = manifest with
        {
            Network = updatedNetwork,
            Arguments = updatedArgs
        };

        var newJson = JsonSerializer.Serialize(updatedManifest, PluginJsonContext.Default.PluginManifest);
        await File.WriteAllTextAsync(manifestPath, newJson, ct);

        var sm = GetOrCreateStateMachine(manifest);
        var wasRunning = sm.State == PluginState.Running;
        var wasRestarted = false;

        if (wasRunning && restartIfRunning)
        {
            await StopAsync(id, ct);
            await Task.Delay(300, ct);
            var (startOk, err) = await StartWithResultAsync(id, ct);
            wasRestarted = startOk;
            if (!startOk)
            {
                return new PluginPortSwitchResult
                {
                    Success = true,
                    PreviousPort = prevPort,
                    NewPort = newPort,
                    WasRestarted = false,
                    Message = $"端口已切换至 {newPort}，但自动重启失败: {err}"
                };
            }
        }

        return new PluginPortSwitchResult
        {
            Success = true,
            PreviousPort = prevPort,
            NewPort = newPort,
            WasRestarted = wasRestarted,
            Message = wasRestarted
                ? $"端口已成功切换至 {newPort}，微服务已热重载运行。"
                : $"端口已成功保存为 {newPort}。"
        };
    }

    #region IHostedService
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (PluginSafeModeGuard.IsSafeModeEnabled())
        {
            _logger?.LogWarning("🛡️ [SAFE_MODE] 检测到环境变量已激活纯净安全模式 (LWT_SAFE_MODE=true)！已跳过所有插件的开机自启动与后台自动恢复。");
        }
        else if (PluginSafeModeGuard.IsAutoStartDisabled())
        {
            _logger?.LogInformation("🛡️ [NO_AUTO_START] 检测到环境变量禁用插件自启 (LWT_PLUGINS_DISABLE_AUTO_START=true)！跳过开机自启动。");
        }
        else
        {
            await ExecuteStartupAutoStartAsync(cancellationToken);
        }

        _supervisorCts = new CancellationTokenSource();
        _supervisorTask = RunSupervisorLoopAsync(_supervisorCts.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_supervisorCts != null)
        {
            _supervisorCts.Cancel();
            if (_supervisorTask != null)
            {
                await Task.WhenAny(_supervisorTask, Task.Delay(2000, cancellationToken));
            }
            _supervisorCts.Dispose();
        }

        foreach (var (id, _) in _stateMachines)
        {
            try { await StopAsync(id, cancellationToken); } catch { }
        }
    }

    private async Task ExecuteStartupAutoStartAsync(CancellationToken ct)
    {
        try
        {
            _logger?.LogInformation("开始执行常驻微服务开机健康探针与自启编排...");
            var plugins = await GetAllAsync(PluginProfile.Resident, ct);
            foreach (var p in plugins)
            {
                // 1. 禁用守卫：若存在 .disabled 标记，绝对跳过自启
                if (p.State == PluginState.Disabled || !p.IsEnabled)
                {
                    continue;
                }

                // 1.1 人工停止守卫：若存在 .manual_stop 标记，表示上次为用户手动停止，跳过开机自启
                var pluginDir = Path.Combine(_pluginsRoot, p.Manifest.Id);
                if (File.Exists(Path.Combine(pluginDir, ".manual_stop")))
                {
                    _logger?.LogInformation("插件 '{Id}' 记录了用户最后手动停止意图 (.manual_stop)，跳过开机自启", p.Manifest.Id);
                    continue;
                }

                // 2. 自启配置策略：Resident 默认自启，显式指定 false 则跳过
                var shouldAutoStart = p.Manifest.Supervision?.AutoStart ?? true;
                if (!shouldAutoStart)
                {
                    continue;
                }

                // 3. 依赖前置探针：检查解释器运行时
                if (!_adapters.TryGetValue(p.Manifest.Runtime, out var adapter))
                {
                    var reason = $"未找到对应运行时适配器: {p.Manifest.Runtime}";
                    _logger?.LogWarning("插件 '{Id}' 开机自启前置检查未通过，跳过拉起: {Reason}", p.Manifest.Id, reason);
                    var sm = GetOrCreateStateMachine(p.Manifest);
                    sm.MarkMissingRuntime(reason);
                    continue;
                }
                if (!adapter.CheckAvailable(out var missingRuntimeReason, out _))
                {
                    var reason = missingRuntimeReason ?? "缺少必要运行时环境";
                    _logger?.LogWarning("插件 '{Id}' 开机自启前置检查未通过，跳过拉起: {Reason}", p.Manifest.Id, reason);
                    var sm = GetOrCreateStateMachine(p.Manifest);
                    sm.MarkMissingRuntime(reason);
                    continue;
                }

                // 4. 依赖前置探针：检查必需的二进制工具
                var missingBinary = p.Dependencies.FirstOrDefault(d => d.Required && !d.IsInstalled);
                if (missingBinary != null)
                {
                    var binMsg = $"缺少必要二进制依赖: {missingBinary.Name}";
                    _logger?.LogWarning("插件 '{Id}' 开机自启前置检查未通过，跳过拉起: {Reason}", p.Manifest.Id, binMsg);
                    var sm = GetOrCreateStateMachine(p.Manifest);
                    sm.MarkMissingDependencies(binMsg);
                    continue;
                }

                // 5. 正式拉起
                _logger?.LogInformation("开机健康探针通过，自动拉起常驻微服务: {Id}", p.Manifest.Id);
                var (success, errorMsg) = await StartWithResultAsync(p.Manifest.Id, ct);
                if (!success)
                {
                    _logger?.LogWarning("插件 '{Id}' 开机自启失败: {Error}", p.Manifest.Id, errorMsg);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "执行插件开机自启流程异常");
        }
    }

    private async Task RunSupervisorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct);

                // 在纯净安全模式下，挂起后台自动拉起，避免崩溃雪崩
                if (PluginSafeModeGuard.IsSafeModeEnabled())
                {
                    continue;
                }

                var plugins = await GetAllAsync(PluginProfile.Resident, ct);
                foreach (var p in plugins)
                {
                    if (p.Manifest.Supervision?.AutoRestart == true && p.State is PluginState.Backoff or PluginState.Crashed)
                    {
                        _logger?.LogInformation("守护进程自动拉起异常常驻插件: {Id}", p.Manifest.Id);
                        await StartAsync(p.Manifest.Id, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "插件中枢守护监控轮询异常");
            }
        }
    }
    #endregion

    public string? GetInternalSecret(string id) => _residentStrategy.GetInternalSecret(id);
}
