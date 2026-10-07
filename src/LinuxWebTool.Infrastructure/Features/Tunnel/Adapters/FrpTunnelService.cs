using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace LinuxWebTool.Infrastructure.Features.Tunnel.Adapters;

/// <summary>
/// FRP 穿透客户端后台生命周期托管服务
/// </summary>
public sealed class FrpTunnelService(
    FrpTunnelEngine engine,
    FrpTunnelConfigStore configStore,
    ILogger<FrpTunnelService> logger) : BackgroundService
{
    public FrpTunnelEngine Engine => engine;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var config = await configStore.GetConfigAsync();
            if (config.AutoStart)
            {
                logger.LogInformation("[FRP] 检测到自启动配置已开启，正在启动穿透长连接...");
                await engine.StartAsync(stoppingToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[FRP] 自启动失败");
        }

        // 保持后台服务存活直至宿主关闭
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException) { }
        finally
        {
            await engine.StopAsync();
        }
    }
}
