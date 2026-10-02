using System.Collections.Concurrent;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>
/// 挂载操作协调器：启动重挂、运行期健康恢复和用户手动操作共用同一把挂载点锁。
/// StartupReady 只表示“配置清单已注册、健康监控可以启动”；具体路径仍可能由启动服务重试。
/// </summary>
public sealed class MountOperationCoordinator
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _configurationLocks = new(StringComparer.Ordinal);

    public async Task<T> RunWithConfigurationLockAsync<T>(string backend, Guid id, Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _configurationLocks.GetOrAdd($"{backend}:{id:N}", _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try { return await action(); }
        finally { semaphore.Release(); }
    }
    private readonly ConcurrentDictionary<string, byte> _startupRunning = new(StringComparer.Ordinal);
    private readonly TaskCompletionSource _startupReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitStartupReadyAsync(CancellationToken cancellationToken = default) =>
        _startupReady.Task.WaitAsync(cancellationToken);

    public void MarkStartupReady() => _startupReady.TrySetResult();

    public void BeginStartup(string localPath) => _startupRunning[NormalizePath(localPath)] = 0;

    public void EndStartup(string localPath) => _startupRunning.TryRemove(NormalizePath(localPath), out _);

    public bool IsStartupRunning(string localPath) => _startupRunning.ContainsKey(NormalizePath(localPath));

    public async Task<T> RunWithMountLockAsync<T>(
        string localPath,
        Func<Task<T>> action,
        CancellationToken cancellationToken = default)
    {
        var semaphore = _locks.GetOrAdd(NormalizePath(localPath), _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            semaphore.Release();
        }
    }

    public Task RunWithMountLockAsync(
        string localPath,
        Func<Task> action,
        CancellationToken cancellationToken = default) =>
        RunWithMountLockAsync<object?>(
            localPath,
            async () =>
            {
                await action();
                return null;
            },
            cancellationToken);

    public async Task<T?> TryRunWithMountLockAsync<T>(string path, Func<Task<T>> action, CancellationToken ct) where T : class
    {
        var semaphore = _locks.GetOrAdd(NormalizePath(path), _ => new SemaphoreSlim(1, 1));
        if (!await semaphore.WaitAsync(0, ct)) return null;
        try { return await action(); }
        finally { semaphore.Release(); }
    }

    public static string NormalizePath(string path)
    {
        var value = path.Trim();
        return (value.StartsWith('/') ? "/" : "") + string.Join('/', value.Split('/', StringSplitOptions.RemoveEmptyEntries));
    }
}
