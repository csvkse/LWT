using System.Diagnostics;
using LinuxWebTool.Contracts.Features.SystemStatus.Contracts;

namespace LinuxWebTool.Infrastructure.Features.SystemInfo.Platform;

/// <summary>
/// 原生 AOT 友好的高性能应用度量与性能统计服务。
/// 使用原子计数与定长环形缓冲计算吞吐与延迟分位数。
/// </summary>
public sealed class AppMetricsService : IAppMetricsService
{
    private const int SampleCapacity = 1024;
    private readonly double[] _recentDurations = new double[SampleCapacity];
    private int _sampleIndex;
    private int _sampleCount;
    private readonly object _sampleLock = new();

    private long _totalRequests;
    private int _activeRequests;
    private long _status2xx;
    private long _status3xx;
    private long _status4xx;
    private long _status5xx;
    private double _totalDurationMs;
    private double _maxDurationMs;
    private int _activeTerminalSessions;
    private readonly long _startTimestamp = Stopwatch.GetTimestamp();

    public void IncrementActiveRequests()
    {
        Interlocked.Increment(ref _activeRequests);
    }

    public void DecrementActiveRequests()
    {
        Interlocked.Decrement(ref _activeRequests);
    }

    public void SetActiveTerminalSessions(int count)
    {
        Volatile.Write(ref _activeTerminalSessions, Math.Max(0, count));
    }

    public void RecordRequest(string method, int statusCode, double elapsedMs)
    {
        Interlocked.Increment(ref _totalRequests);

        if (statusCode is >= 200 and < 300) Interlocked.Increment(ref _status2xx);
        else if (statusCode is >= 300 and < 400) Interlocked.Increment(ref _status3xx);
        else if (statusCode is >= 400 and < 500) Interlocked.Increment(ref _status4xx);
        else if (statusCode >= 500) Interlocked.Increment(ref _status5xx);

        lock (_sampleLock)
        {
            _totalDurationMs += elapsedMs;
            if (elapsedMs > _maxDurationMs) _maxDurationMs = elapsedMs;

            _recentDurations[_sampleIndex] = elapsedMs;
            _sampleIndex = (_sampleIndex + 1) % SampleCapacity;
            if (_sampleCount < SampleCapacity) _sampleCount++;
        }
    }

    public AppMetricsSnapshot GetSnapshot()
    {
        double avg = 0;
        double p50 = 0;
        double p95 = 0;
        double max = 0;

        lock (_sampleLock)
        {
            max = _maxDurationMs;
            if (_totalRequests > 0)
            {
                avg = _totalDurationMs / _totalRequests;
            }

            if (_sampleCount > 0)
            {
                var copy = new double[_sampleCount];
                Array.Copy(_recentDurations, copy, _sampleCount);
                Array.Sort(copy);

                p50 = GetPercentile(copy, 0.50);
                p95 = GetPercentile(copy, 0.95);
            }
        }

        var uptime = (long)Stopwatch.GetElapsedTime(_startTimestamp).TotalSeconds;

        return new AppMetricsSnapshot
        {
            TotalRequests = Volatile.Read(ref _totalRequests),
            ActiveRequests = Math.Max(0, Volatile.Read(ref _activeRequests)),
            Status2xx = Volatile.Read(ref _status2xx),
            Status3xx = Volatile.Read(ref _status3xx),
            Status4xx = Volatile.Read(ref _status4xx),
            Status5xx = Volatile.Read(ref _status5xx),
            AverageDurationMs = Math.Round(avg, 2),
            P50DurationMs = Math.Round(p50, 2),
            P95DurationMs = Math.Round(p95, 2),
            MaxDurationMs = Math.Round(max, 2),
            ActiveTerminalSessions = Volatile.Read(ref _activeTerminalSessions),
            UptimeSeconds = uptime
        };
    }

    private static double GetPercentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        var idx = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        idx = Math.Clamp(idx, 0, sorted.Length - 1);
        return sorted[idx];
    }
}
