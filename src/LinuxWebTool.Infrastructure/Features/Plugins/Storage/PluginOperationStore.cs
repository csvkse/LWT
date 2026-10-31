using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Storage;

/// <summary>
/// 插件异步操作记录存储仓储：
/// - 维护操作状态机 (queued -> starting -> running -> 终态)；
/// - 提供基于调用者与幂等键的高效精确查询；
/// - 自动执行有界配额与历史记录过期清理。
/// </summary>
public sealed class PluginOperationStore : IPluginOperationStore
{
    private readonly ConcurrentDictionary<string, PluginOperationDto> _operations = new();
    private readonly ConcurrentDictionary<string, string> _idempotencyIndex = new();

    public ValueTask SaveAsync(PluginOperationDto operation, CancellationToken ct)
    {
        _operations[operation.OperationId] = operation;

        if (!string.IsNullOrEmpty(operation.IdempotencyKey))
        {
            var idxKey = BuildIdempotencyIndexKey(operation.Caller, operation.PluginIdentity, operation.Action, operation.IdempotencyKey);
            _idempotencyIndex[idxKey] = operation.OperationId;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<PluginOperationDto?> GetByIdAsync(string operationId, CancellationToken ct)
    {
        if (_operations.TryGetValue(operationId, out var op))
        {
            return ValueTask.FromResult<PluginOperationDto?>(op);
        }
        return ValueTask.FromResult<PluginOperationDto?>(null);
    }

    public ValueTask<PluginOperationDto?> GetByIdempotencyKeyAsync(
        string caller,
        string pluginIdentity,
        string action,
        string idempotencyKey,
        CancellationToken ct)
    {
        var idxKey = BuildIdempotencyIndexKey(caller, pluginIdentity, action, idempotencyKey);
        if (_idempotencyIndex.TryGetValue(idxKey, out var opId) && _operations.TryGetValue(opId, out var op))
        {
            return ValueTask.FromResult<PluginOperationDto?>(op);
        }
        return ValueTask.FromResult<PluginOperationDto?>(null);
    }

    public ValueTask UpdateStatusAsync(
        string operationId,
        string status,
        double? progress,
        string? progressMessage,
        PluginActionResult? result,
        CancellationToken ct)
    {
        if (_operations.TryGetValue(operationId, out var existing))
        {
            var updated = existing with
            {
                Status = status,
                ProgressPercent = progress ?? existing.ProgressPercent,
                ProgressMessage = progressMessage ?? existing.ProgressMessage,
                Result = result ?? existing.Result,
                StartedAtUtc = status == "running" && existing.StartedAtUtc == null ? DateTimeOffset.UtcNow : existing.StartedAtUtc,
                FinishedAtUtc = (status is "succeeded" or "failed" or "cancelled" or "timedOut" or "unknown" or "interrupted")
                    ? DateTimeOffset.UtcNow
                    : existing.FinishedAtUtc,
                EffectUncertain = result?.EffectUncertain ?? existing.EffectUncertain
            };
            _operations[operationId] = updated;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CleanExpiredRecordsAsync(TimeSpan maxAge, int maxCount, CancellationToken ct)
    {
        var threshold = DateTimeOffset.UtcNow - maxAge;
        var expiredKeys = _operations
            .Where(kv => kv.Value.CreatedAtUtc < threshold &&
                         kv.Value.Status is "succeeded" or "failed" or "cancelled" or "timedOut")
            .Select(kv => kv.Key)
            .ToList();

        foreach (var k in expiredKeys)
        {
            _operations.TryRemove(k, out _);
        }

        // 若依然超过最大保留数量，剔除最早的终态记录
        if (_operations.Count > maxCount)
        {
            var excess = _operations
                .Where(kv => kv.Value.Status is "succeeded" or "failed" or "cancelled" or "timedOut")
                .OrderBy(kv => kv.Value.CreatedAtUtc)
                .Take(_operations.Count - maxCount)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var k in excess)
            {
                _operations.TryRemove(k, out _);
            }
        }

        return ValueTask.CompletedTask;
    }

    private static string BuildIdempotencyIndexKey(string caller, string pluginIdentity, string action, string idempotencyKey)
        => $"{caller}:{pluginIdentity}:{action}:{idempotencyKey}";
}
