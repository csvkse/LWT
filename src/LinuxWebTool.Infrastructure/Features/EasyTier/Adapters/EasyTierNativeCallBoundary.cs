namespace LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;

/// <summary>One native call at a time on a dedicated thread. Timed-out work retains its slot.</summary>
internal sealed class EasyTierNativeCallBoundary
{
    private readonly object gate = new();
    private Task? active;
    public bool IsBusy { get { lock (gate) return active is { IsCompleted: false }; } }

    public async Task<T> RunAsync<T>(Func<T> operation, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<T> task;
        lock (gate)
        {
            if (active is { IsCompleted: false })
                throw new InvalidOperationException("上次 EasyTier 原生调用尚未完成，暂停新操作，请检查节点和内核状态");
            task = Task.Factory.StartNew(operation, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            active = task;
            // Observe late faults even if the HTTP caller has already timed out or cancelled.
            _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        return await task.WaitAsync(timeout, ct);
    }
}
