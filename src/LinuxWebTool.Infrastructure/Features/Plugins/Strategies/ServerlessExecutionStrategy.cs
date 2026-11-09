using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Strategies;

/// <summary>
/// Serverless FaaS 动态冷启与缩容至零执行策略 (Scale-to-Zero FaaS Strategy)
/// - 请求到达时毫秒级拉起微服务进程并完成就绪探测；
/// - 维持空闲保活计时器（默认 5 分钟），在突发连续调用期保持热机；
/// - 闲置超时后自动触发 Scale-to-Zero 优雅释放常驻端口与系统内存；
/// - 兼顾轻量化零常驻与微服务低延迟高吞吐。
/// </summary>
public sealed class ServerlessExecutionStrategy : IExecutionProfileStrategy
{
    public PluginProfile Profile => PluginProfile.Serverless;

    private readonly ResidentExecutionStrategy _residentStrategy = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _idleTimers = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(5);

    public int? GetProcessId(string pluginId) => _residentStrategy.GetProcessId(pluginId);

    public ValueTask<(bool Success, string? ErrorMessage)> StartAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine stateMachine,
        PluginPortManager portManager,
        PluginProcessRingBuffer ringBuffer,
        IReadOnlyDictionary<PluginRuntime, IRuntimeAdapter> adapters,
        CancellationToken ct)
    {
        ResetIdleTimer(manifest.Id, manifest, pluginDir, stateMachine, portManager, ringBuffer);
        return _residentStrategy.StartAsync(manifest, pluginDir, stateMachine, portManager, ringBuffer, adapters, ct);
    }

    public ValueTask<bool> StopAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine stateMachine,
        PluginPortManager portManager,
        CancellationToken ct)
    {
        CancelIdleTimer(manifest.Id);
        return _residentStrategy.StopAsync(manifest, pluginDir, stateMachine, portManager, ct);
    }

    public async ValueTask<PluginActionResult> ExecuteActionAsync(
        PluginManifest manifest,
        string pluginDir,
        PluginActionRequest request,
        IReadOnlyDictionary<PluginRuntime, IRuntimeAdapter> adapters,
        CancellationToken ct)
    {
        // 1. 若当前微服务处于缩容至零状态，动态触发极速冷启动
        var pid = _residentStrategy.GetProcessId(manifest.Id);
        if (pid == null)
        {
            // 动态按需冷拉起
            var dummySm = new PluginStateMachine(manifest.Id, manifest.Supervision);
            var dummyPm = new PluginPortManager();
            var dummyRb = new PluginProcessRingBuffer();
            var (startOk, startErr) = await _residentStrategy.StartAsync(manifest, pluginDir, dummySm, dummyPm, dummyRb, adapters, ct).ConfigureAwait(false);
            if (!startOk)
            {
                return PluginActionResult.Fail($"Serverless 冷启动失败: {startErr}");
            }
        }

        // 2. 执行核心 Action 转发
        var result = await _residentStrategy.ExecuteActionAsync(manifest, pluginDir, request, adapters, ct).ConfigureAwait(false);

        // 3. 刷新空闲倒计时
        var dummySm2 = new PluginStateMachine(manifest.Id, manifest.Supervision);
        var dummyPm2 = new PluginPortManager();
        var dummyRb2 = new PluginProcessRingBuffer();
        ResetIdleTimer(manifest.Id, manifest, pluginDir, dummySm2, dummyPm2, dummyRb2);

        return result;
    }

    private void ResetIdleTimer(
        string pluginId,
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine stateMachine,
        PluginPortManager portManager,
        PluginProcessRingBuffer ringBuffer)
    {
        CancelIdleTimer(pluginId);

        var cts = new CancellationTokenSource();
        _idleTimers[pluginId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_idleTimeout, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested)
                {
                    ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [SYSTEM] 触发 Serverless Scale-to-Zero：空闲超过 5 分钟，自动释放常驻进程与端口");
                    await _residentStrategy.StopAsync(manifest, pluginDir, stateMachine, portManager, CancellationToken.None).ConfigureAwait(false);
                    _idleTimers.TryRemove(pluginId, out _);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                ringBuffer.Append($"[{DateTime.Now:HH:mm:ss}] [WARN] Scale-to-Zero 回收异常: {ex.Message}");
            }
        });
    }

    private void CancelIdleTimer(string pluginId)
    {
        if (_idleTimers.TryRemove(pluginId, out var existingCts))
        {
            try
            {
                existingCts.Cancel();
                existingCts.Dispose();
            }
            catch { }
        }
    }
}
