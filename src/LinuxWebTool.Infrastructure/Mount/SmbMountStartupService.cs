using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.SystemInfo;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>
/// 应用启动时重放 SMB 挂载（不写 /etc/fstab，避免破坏宿主机引导配置）：
/// 1. 全部启用条目注册状态页白名单 + 确保凭据文件存在；
/// 2. AutoMount 条目未挂载时自动重挂（覆盖容器重启丢 mount namespace 的场景）。
/// </summary>
public sealed class SmbMountStartupService(
    SmbMountStore store,
    SmbMountService mountService,
    ILogger<SmbMountStartupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等待数据库 / 网络就绪，避开启动尖峰
        try
        {
            await Task.Delay(2000, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        List<SmbMount> mounts;
        try
        {
            mounts = (await store.GetAllAsync()).ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SMB 挂载配置读取失败，跳过启动重挂");
            return;
        }

        foreach (var mount in mounts.Where(m => m.Enabled))
        {
            // 白名单注册（无论当前是否挂载，命中后才会在状态页展示）
            SystemStatusProvider.ManagedMountPoints[mount.LocalPath.Trim().TrimEnd('/')] = 0;
            try
            {
                mountService.EnsureCredentialFile(mount);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SMB 凭据文件写入失败：{Name}", mount.Name);
            }
        }

        foreach (var mount in mounts.Where(m => m.Enabled && m.AutoMount))
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            if (mountService.GetStatus(mount) == SmbMountStatus.Mounted)
            {
                logger.LogInformation("SMB 已挂载，跳过重挂：{Name} → {LocalPath}", mount.Name, mount.LocalPath);
                continue;
            }

            var (success, message) = await mountService.MountAsync(mount);
            if (success)
            {
                logger.LogInformation("SMB 自动重挂成功：{Name} → {LocalPath}", mount.Name, mount.LocalPath);
            }
            else
            {
                logger.LogWarning("SMB 自动重挂失败：{Name} → {LocalPath}，原因：{Message}", mount.Name, mount.LocalPath, message);
            }
        }
    }
}
