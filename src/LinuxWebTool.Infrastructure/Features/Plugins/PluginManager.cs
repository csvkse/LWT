using System.Collections.Concurrent;
using System.Text.Json;
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

        return await strategy.StartAsync(manifest, dir, sm, _portManager, ringBuffer, _adapters, ct);
    }

    public async ValueTask<bool> StopAsync(string id, CancellationToken ct = default)
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

        return await strategy.StopAsync(manifest, dir, sm, _portManager, ct);
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

        var cleanRel = string.IsNullOrWhiteSpace(relativePath) ? "index.html" : relativePath.Trim().TrimStart('/', '\\');
        if (cleanRel.Contains("..")) return null;

        var candidates = new[]
        {
            Path.Combine(pluginDir, "ui", cleanRel),
            Path.Combine(pluginDir, "assets", cleanRel),
            Path.Combine(pluginDir, cleanRel)
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

        var state = isDisabled ? PluginState.Disabled : sm.State;
        if (!isDisabled && missingReason != null)
        {
            state = PluginState.MissingRuntime;
        }

        int? allocatedPort = _portManager.GetPort(manifest.Id);
        int? pid = manifest.Profile == PluginProfile.Resident ? _residentStrategy.GetProcessId(manifest.Id) : null;

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
            ProcessId = pid,
            AccessUrl = accessUrl,
            StatusMessage = sm.LastError ?? (isDisabled ? "已显式禁用" : null),
            CrashCount = sm.FastCrashCount,
            StartedAt = sm.StartedAt.HasValue ? new DateTimeOffset(sm.StartedAt.Value, TimeSpan.Zero) : null,
            IsMissingRuntime = missingReason != null,
            MissingRuntimeMessage = missingReason,
            EffectiveInstallGuide = missingReason != null && adapter != null ? adapter.ResolveInstallGuide("linux") : null
        };
    }

    #region IHostedService
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _supervisorCts = new CancellationTokenSource();
        _supervisorTask = RunSupervisorLoopAsync(_supervisorCts.Token);
        return Task.CompletedTask;
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

    private async Task RunSupervisorLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(5000, ct);

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
}
