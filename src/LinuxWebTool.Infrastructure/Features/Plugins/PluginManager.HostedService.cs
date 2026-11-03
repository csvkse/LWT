using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager : IHostedService
{
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
            NotifyPluginStateOrManifestChanged();
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
