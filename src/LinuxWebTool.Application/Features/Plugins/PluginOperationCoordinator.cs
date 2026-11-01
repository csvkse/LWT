using System.Collections.Concurrent;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Shared.Support;

namespace LinuxWebTool.Application.Features.Plugins;

/// <summary>
/// 插件异步操作协调器：
/// - 控制排队上限与全局并发度 (并发 4，队列 16，排队期限 2 秒)；
/// - 幂等键防重判定与调用者安全授权；
/// - 调度执行、协作取消与生命周期跟踪。
/// </summary>
public sealed class PluginOperationCoordinator : IPluginOperationCoordinator
{
    private const int MaxConcurrency = 4;
    private const int MaxQueueCapacity = 16;
    private static readonly TimeSpan QueueWaitTimeout = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _concurrencySemaphore = new(MaxConcurrency, MaxConcurrency);
    private int _queuedCount = 0;

    private readonly IPluginOperationStore _store;
    private readonly IPluginArtifactStore _artifactStore;
    private readonly INativeCommandExecutor _commandExecutor;
    private readonly Func<string, string?> _pluginDirResolver;
    private readonly Func<string, string, PluginCustomAction?>? _actionResolver;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCts = new();

    public PluginOperationCoordinator(
        IPluginOperationStore store,
        IPluginArtifactStore artifactStore,
        INativeCommandExecutor commandExecutor,
        Func<string, string?> pluginDirResolver,
        Func<string, string, PluginCustomAction?>? actionResolver = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        _commandExecutor = commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
        _pluginDirResolver = pluginDirResolver ?? throw new ArgumentNullException(nameof(pluginDirResolver));
        _actionResolver = actionResolver;
    }

    public async ValueTask<OperationAccepted> SubmitAsync(CommandInvocation invocation, CancellationToken ct)
    {
        if (invocation == null) throw new ArgumentNullException(nameof(invocation));
        if (!PluginSecurityGuard.IsValidPluginId(invocation.PluginIdentity))
        {
            throw new SecurityException($"非法插件标识: '{invocation.PluginIdentity}'");
        }
        if (!PluginSecurityGuard.IsValidActionName(invocation.Action))
        {
            throw new SecurityException($"非法动作名称: '{invocation.Action}'");
        }

        var caller = invocation.Caller ?? "anonymous";
        var inputDigest = ComputeInputDigest(invocation.Input);

        // 1. 幂等键检查
        if (!string.IsNullOrEmpty(invocation.IdempotencyKey))
        {
            var existing = await _store.GetByIdempotencyKeyAsync(caller, invocation.PluginIdentity, invocation.Action, invocation.IdempotencyKey, ct).ConfigureAwait(false);
            if (existing != null)
            {
                if (existing.InputDigest != null && existing.InputDigest != inputDigest)
                {
                    throw new InvalidOperationException($"幂等键 '{invocation.IdempotencyKey}' 冲突：调用参数与已提交操作不一致。");
                }

                return new OperationAccepted
                {
                    OperationId = existing.OperationId,
                    StatusUrl = $"/api/PluginOperations/{existing.OperationId}",
                    Status = existing.Status
                };
            }
        }

        // 2. 队列容量检查
        if (Interlocked.Increment(ref _queuedCount) > MaxQueueCapacity)
        {
            Interlocked.Decrement(ref _queuedCount);
            throw new InvalidOperationException($"操作队列已满 (当前并发中: {MaxConcurrency}，排队中: {MaxQueueCapacity})，请稍后再试。");
        }

        var operationId = Guid.NewGuid().ToString("N");
        var operationDto = new PluginOperationDto
        {
            OperationId = operationId,
            Caller = caller,
            PluginIdentity = invocation.PluginIdentity,
            Action = invocation.Action,
            Status = "queued",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            IdempotencyKey = invocation.IdempotencyKey,
            InputDigest = inputDigest
        };

        await _store.SaveAsync(operationDto, ct).ConfigureAwait(false);

        var opCts = new CancellationTokenSource();
        _activeCts[operationId] = opCts;

        // 3. 异步启动执行任务
        _ = Task.Run(async () =>
        {
            try
            {
                // 等待并发槽位
                var acquired = await _concurrencySemaphore.WaitAsync(QueueWaitTimeout, opCts.Token).ConfigureAwait(false);
                Interlocked.Decrement(ref _queuedCount);

                if (!acquired)
                {
                    await _store.UpdateStatusAsync(operationId, "failed", null, null, new PluginActionResult
                    {
                        Status = "failed",
                        ErrorCode = "QueueTimeout",
                        Error = "排队等待空闲并发槽位超时。"
                    }, CancellationToken.None).ConfigureAwait(false);
                    return;
                }

                try
                {
                    if (opCts.IsCancellationRequested)
                    {
                        await _store.UpdateStatusAsync(operationId, "cancelled", null, "在排队时已被取消", new PluginActionResult
                        {
                            Status = "cancelled",
                            Error = "操作在排队调度时已被取消。",
                            EffectUncertain = false
                        }, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    await _store.UpdateStatusAsync(operationId, "running", 0.0, "开始执行...", null, CancellationToken.None).ConfigureAwait(false);

                    var workingDir = _pluginDirResolver(invocation.PluginIdentity);
                    if (string.IsNullOrEmpty(workingDir) || !Directory.Exists(workingDir))
                    {
                        await _store.UpdateStatusAsync(operationId, "failed", null, null, new PluginActionResult
                        {
                            Status = "failed",
                            ErrorCode = "PluginNotFound",
                            Error = $"未找到插件根目录: '{invocation.PluginIdentity}'"
                        }, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    // 获取动作命令定义
                    var customAction = _actionResolver?.Invoke(invocation.PluginIdentity, invocation.Action);
                    var cmdDef = customAction?.Command ?? new NativeCommandDefinition
                    {
                        CommandId = invocation.Action,
                        ProgramRef = invocation.Action
                    };

                    var observer = new CoordinatorProgressObserver(async (pct, msg) =>
                    {
                        await _store.UpdateStatusAsync(operationId, "running", pct, msg, null, CancellationToken.None).ConfigureAwait(false);
                    });

                    var result = await _commandExecutor.ExecuteAsync(cmdDef, invocation, workingDir, operationId, observer, opCts.Token).ConfigureAwait(false);

                    await _store.UpdateStatusAsync(operationId, result.Status, result.Status == "succeeded" ? 100.0 : null, result.Message, result, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    _concurrencySemaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                await _store.UpdateStatusAsync(operationId, "cancelled", null, "已取消", new PluginActionResult
                {
                    Status = "cancelled",
                    Error = "操作已被调用者取消。",
                    EffectUncertain = true
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _store.UpdateStatusAsync(operationId, "failed", null, null, new PluginActionResult
                {
                    Status = "failed",
                    ErrorCode = "UnhandledException",
                    Error = $"执行过程中发生未捕获异常: {ex.Message}",
                    EffectUncertain = true
                }, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _activeCts.TryRemove(operationId, out _);
                opCts.Dispose();
            }
        });

        return new OperationAccepted
        {
            OperationId = operationId,
            StatusUrl = $"/api/PluginOperations/{operationId}",
            Status = "queued"
        };
    }

    public async ValueTask<PluginOperationDto?> GetAsync(string operationId, string caller, CancellationToken ct)
    {
        var op = await _store.GetByIdAsync(operationId, ct).ConfigureAwait(false);
        if (op == null) return null;

        // 权限校验：非同一调用者且非管理员时隐藏细节
        if (!string.IsNullOrEmpty(caller) && caller != "admin" && op.Caller != caller)
        {
            return null;
        }

        return op;
    }

    public async ValueTask<PluginOperationDto?> CancelAsync(string operationId, string caller, CancellationToken ct)
    {
        var op = await _store.GetByIdAsync(operationId, ct).ConfigureAwait(false);
        if (op == null) return null;

        if (!string.IsNullOrEmpty(caller) && caller != "admin" && op.Caller != caller)
        {
            throw new SecurityException("无权取消属于其他调用者的操作。");
        }

        if (_activeCts.TryGetValue(operationId, out var cts))
        {
            try { cts.Cancel(); } catch { }
        }
        else if (op.Status == "queued")
        {
            await _store.UpdateStatusAsync(operationId, "cancelled", null, "排队中已取消", new PluginActionResult
            {
                Status = "cancelled",
                Error = "操作在排队时已被取消。",
                EffectUncertain = false
            }, ct).ConfigureAwait(false);
        }

        return await _store.GetByIdAsync(operationId, ct).ConfigureAwait(false);
    }

    private static string ComputeInputDigest(JsonElement input)
    {
        var raw = input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText();
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    private sealed class CoordinatorProgressObserver : ICommandProgressObserver
    {
        private readonly Func<double?, string?, ValueTask> _callback;

        public CoordinatorProgressObserver(Func<double?, string?, ValueTask> callback)
        {
            _callback = callback;
        }

        public ValueTask OnProgressAsync(double? percent, string? message, CancellationToken ct)
            => _callback(percent, message);
    }
}
