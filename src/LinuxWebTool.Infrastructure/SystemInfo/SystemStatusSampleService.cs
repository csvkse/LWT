using LinuxWebTool.Contracts.Interfaces;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;

namespace LinuxWebTool.Infrastructure.SystemInfo;

/// <summary>
/// 后台采样服务：每 60 秒采集一次系统状态写入快照表（历史曲线数据源）；
/// 启动时先采一次并清理 7 天前的旧快照。
/// </summary>
public sealed class SystemStatusSampleService(
    ISystemStatusProvider statusProvider,
    SystemStatusStore statusStore,
    ILogger<SystemStatusSampleService> logger) : BackgroundService
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await statusStore.ClearOlderThan(DateTime.Now - Retention);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "清理过期系统快照失败");
        }

        await SampleOnceAsync();

        using var timer = new PeriodicTimer(SampleInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SampleOnceAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
    }

    private async Task SampleOnceAsync()
    {
        try
        {
            var status = await statusProvider.GetStatusAsync();
            await statusStore.InsertAsync(new SystemStatusSnapshot
            {
                CpuUsage = status.Cpu.UsagePercent,
                Load1 = status.Cpu.Load1,
                MemUsage = status.Memory.UsagePercent,
                DiskRootUsage = FindRootUsage(status),
                NetSentBps = status.Networks.Sum(n => n.SentBytesPerSec),
                NetRecvBps = status.Networks.Sum(n => n.RecvBytesPerSec),
            });
            logger.LogDebug("系统快照已采样：CPU {Cpu}%，内存 {Mem}%", status.Cpu.UsagePercent, status.Memory.UsagePercent);
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
