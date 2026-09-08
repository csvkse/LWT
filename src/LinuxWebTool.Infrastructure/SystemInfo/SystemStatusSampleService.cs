using LinuxWebTool.Contracts.Interfaces;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.SystemInfo;

/// <summary>
/// 后台采样服务：
/// - 整机（CPU/内存/负载/根分区/网络总和）：按 SamplingIntervalSeconds（默认 60s）恒定采样，
///   供趋势图高频曲线。
/// - 磁盘/网络/进程：两级采样。基线层按 BaselineIntervalMinutes（默认 30min）记录一次全量；
///   任一资源越阈值（CPU/内存/磁盘/网卡）进入异常窗口后按 AlarmIntervalMinutes（默认 10min）
///   记录全量，连续低于阈值一段时间后退出异常窗口回到基线层。
/// 启动时先各做一次采样并清理所有表旧于 RetentionDays 天的数据。
/// </summary>
public sealed class SystemStatusSampleService(
    ISystemStatusProvider statusProvider,
    SystemStatusStore statusStore,
    SystemStatusDiskStore diskStore,
    SystemStatusNetStore netStore,
    SystemStatusProcessStore processStore,
    SystemStatusOptions options,
    ILogger<SystemStatusSampleService> logger) : BackgroundService
{
    private static readonly TimeSpan ControlTick = TimeSpan.FromSeconds(30);

    private DateTime _nextSystemSample = DateTime.MinValue;
    private DateTime _nextBaseline = DateTime.MinValue;
    private DateTime _nextAlarm = DateTime.MinValue;
    private bool _inAlarmWindow;

    /// <summary>本轮资源采样的确定性触发节奏（基线 / 异常 / 空闲），供整机采样判断是否联动。</summary>
    private bool _resourcesDueThisTick;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retention = TimeSpan.FromDays(options.RetentionDays);
        try
        {
            await ClearOlderThanAsync(DateTime.Now - retention);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理过期系统快照失败");
        }

        await SampleOnceAsync(SystemSample.All);

        using var timer = new PeriodicTimer(ControlTick);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await TryTickAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private async Task ClearOlderThanAsync(DateTime cutoff)
    {
        await statusStore.ClearOlderThan(cutoff);
        await diskStore.ClearOlderThan(cutoff);
        await netStore.ClearOlderThan(cutoff);
        await processStore.ClearOlderThan(cutoff);
    }

    private async Task TryTickAsync()
    {
        var now = DateTime.Now;
        _resourcesDueThisTick = false;

        // 整机：恒定频率采样。
        var systemDue = now >= _nextSystemSample;
        if (systemDue)
        {
            _nextSystemSample = now.AddSeconds(options.SamplingIntervalSeconds);
        }

        // 资源：两级采样（基线 / 异常 / 空闲）。
        var inAlarm = await IsInAlarmAsync();
        if (inAlarm)
        {
            if (!_inAlarmWindow)
            {
                _inAlarmWindow = true;
                _nextAlarm = now.AddMinutes(options.AlarmIntervalMinutes);
                _resourcesDueThisTick = true;
                logger.LogInformation("系统状态进入异常窗口（CPU>={Cpu}% 或 内存>={Mem}%）",
                    options.CpuAlarmThreshold, options.MemAlarmThreshold);
            }
            else if (now >= _nextAlarm)
            {
                _nextAlarm = now.AddMinutes(options.AlarmIntervalMinutes);
                _resourcesDueThisTick = true;
            }
        }
        else
        {
            if (_inAlarmWindow)
            {
                _inAlarmWindow = false;
                _nextBaseline = now.AddMinutes(options.BaselineIntervalMinutes);
                logger.LogInformation("系统状态退出异常窗口，回到基线采样（{Min} 分钟/次）", options.BaselineIntervalMinutes);
            }
            else if (now >= _nextBaseline)
            {
                _nextBaseline = now.AddMinutes(options.BaselineIntervalMinutes);
                _resourcesDueThisTick = true;
            }
        }

        // 采集：整机与资源可独立触发。资源触发时用已采集的整机指标，无需重复采集。
        if (systemDue || _resourcesDueThisTick)
        {
            var mode = (systemDue, _resourcesDueThisTick) switch
            {
                (true, true) => SystemSample.All,
                (true, false) => SystemSample.SystemOnly,
                _ => SystemSample.ResourcesOnly,
            };
            await SampleOnceAsync(mode);
        }
    }

    private async Task<bool> IsInAlarmAsync()
    {
        // 复用最近整机快照判断，避免每个 tick 重复昂贵采集。
        return await statusStore.IsRecentAboveAsync(
            TimeSpan.FromMinutes(5),
            options.CpuAlarmThreshold,
            options.MemAlarmThreshold,
            options.DiskAlarmThreshold,
            options.NetAlarmThresholdBps);
    }

    private async Task SampleOnceAsync(SystemSample mode)
    {
        try
        {
            var now = DateTime.Now;
            var status = await statusProvider.GetStatusAsync();

            if (mode is SystemSample.All or SystemSample.SystemOnly)
            {
                await statusStore.InsertAsync(new SystemStatusSnapshot
                {
                    CpuUsage = status.Cpu.UsagePercent,
                    Load1 = status.Cpu.Load1,
                    MemUsage = status.Memory.UsagePercent,
                    DiskRootUsage = FindRootUsage(status),
                    NetSentBps = status.Networks.Sum(n => n.SentBytesPerSec),
                    NetRecvBps = status.Networks.Sum(n => n.RecvBytesPerSec),
                }, now);
            }

            if (mode is SystemSample.All or SystemSample.ResourcesOnly)
            {
                var diskRows = status.Disks
                    .Select(d => new SystemStatusDiskSnapshot
                    {
                        Time = now,
                        Mount = d.Mount,
                        FileSystem = d.FileSystem,
                        UsagePercent = d.UsagePercent,
                        TotalBytes = d.TotalBytes,
                        UsedBytes = d.UsedBytes,
                        FreeBytes = d.FreeBytes,
                    })
                    .ToList();
                await diskStore.InsertAsync(diskRows);

                var netRows = status.Networks
                    .Select(n => new SystemStatusNetSnapshot
                    {
                        Time = now,
                        Name = n.Name,
                        SentBytesPerSec = n.SentBytesPerSec,
                        RecvBytesPerSec = n.RecvBytesPerSec,
                    })
                    .ToList();
                await netStore.InsertAsync(netRows);

                var processRows = status.TopCpuProcesses
                    .Concat(status.TopMemProcesses)
                    .GroupBy(p => p.Pid)
                    .Select(g => g.First())
                    .Select(p => new SystemStatusProcessSnapshot
                    {
                        Time = now,
                        Pid = p.Pid,
                        Name = p.Name,
                        CpuPercent = p.CpuPercent,
                        MemPercent = p.MemPercent,
                        MemBytes = p.MemBytes,
                        DiskReadBps = p.DiskReadBps,
                        DiskWriteBps = p.DiskWriteBps,
                        NetSentBps = p.NetSentBps,
                        NetRecvBps = p.NetRecvBps,
                    })
                    .ToList();
                await processStore.InsertAsync(processRows);
            }

            logger.LogDebug("系统快照已采样（{Mode}）：CPU {Cpu}%，内存 {Mem}%", mode, status.Cpu.UsagePercent, status.Memory.UsagePercent);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "系统状态采样失败");
        }
    }

    private static double FindRootUsage(SystemStatusResult status)
    {
        var root = status.Disks.FirstOrDefault(d => d.Mount == "/")
            ?? status.Disks.FirstOrDefault(d => d.Mount.EndsWith('\\') || d.Mount.Equals("C:\\", StringComparison.OrdinalIgnoreCase))
            ?? status.Disks.FirstOrDefault();
        return root?.UsagePercent ?? 0;
    }
}

/// <summary>一次采样的范围。</summary>
internal enum SystemSample
{
    /// <summary>整机 + 资源（全量）。</summary>
    All,
    /// <summary>仅整机（恒定高频）。</summary>
    SystemOnly,
    /// <summary>仅资源（磁盘 / 网络 / 进程，两级采样触发）。</summary>
    ResourcesOnly,
}
