using System;
using System.Threading;
using System.Threading.Tasks;
namespace LinuxWebTool.Infrastructure.Features.Tunnel.Adapters;

internal enum TunnelRequestState
{
    Init,
    Connecting,
    Streaming,
    Completed,
    Aborted,
    Errored
}

/// <summary>
/// 隧道单请求/单流生命周期会话与背压流控模型
/// </summary>
internal sealed class TunnelRequestSession : IDisposable
{
    public string RequestId { get; }
    public string Method { get; }
    public string Path { get; }
    public TunnelRequestState State { get; private set; } = TunnelRequestState.Init;
    public CancellationTokenSource Cts { get; }

    public string? CanonicalMediaKey { get; set; }

    // 滑动窗口流控 (BDP 理论支撑高码率 4K/蓝光原盘)
    public long TotalBytesSent { get; private set; }
    public int Credit { get; private set; } = 4 * 1024 * 1024; // 初始 4MB
    public const int MaxCredit = 16 * 1024 * 1024;             // 上限 16MB
    public const int FastStartBurstBytes = 16 * 1024 * 1024;   // 起播 16MB 突发免限流，极速喂饱播放器

    private TaskCompletionSource? _creditTcs;
    private readonly object _lock = new();

    public TunnelRequestSession(string requestId, string method, string path, CancellationToken parentToken)
    {
        RequestId = requestId;
        Method = method;
        Path = path;
        Cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
    }

    public void AddCredit(int bytes)
    {
        lock (_lock)
        {
            Credit = Math.Min(Credit + bytes, MaxCredit);
            if (Credit > 0 && _creditTcs != null)
            {
                var tcs = _creditTcs;
                _creditTcs = null;
                tcs.TrySetResult();
            }
        }
    }

    public async ValueTask ConsumeCreditAsync(int bytes, CancellationToken token)
    {
        TaskCompletionSource? waitTcs = null;
        lock (_lock)
        {
            Credit -= bytes;
            TotalBytesSent += bytes;

            // 起播阶段 (前 16MB)：优先极速填满播放器缓冲区，不等待 ACK 避免首屏起播卡顿
            if (TotalBytesSent < FastStartBurstBytes)
            {
                return;
            }

            if (Credit <= 0)
            {
                _creditTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                waitTcs = _creditTcs;
            }
        }

        if (waitTcs != null)
        {
            // 超时自愈：按会话平滑补充 512KB，严禁因边缘 ACK 丢失死锁
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            try
            {
                await waitTcs.Task.WaitAsync(linked.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !token.IsCancellationRequested)
            {
                lock (_lock)
                {
                    Credit = Math.Max(Credit, 1024 * 1024);
                    _creditTcs = null;
                }
            }
        }
    }

    public void SetState(TunnelRequestState state)
    {
        lock (_lock)
        {
            if (State is TunnelRequestState.Completed or TunnelRequestState.Aborted or TunnelRequestState.Errored) return;
            State = state;
        }
    }

    public void Abort()
    {
        lock (_lock)
        {
            if (State is TunnelRequestState.Completed or TunnelRequestState.Aborted or TunnelRequestState.Errored) return;
            State = TunnelRequestState.Aborted;
            try { Cts.Cancel(); } catch { }
            _creditTcs?.TrySetCanceled();
            _creditTcs = null;
        }
    }

    public void Dispose()
    {
        Cts.Dispose();
    }
}
