using LinuxWebTool.Infrastructure.Security;
using LinuxWebTool.Infrastructure.Support;

namespace LinuxWebTool.WebHost.Composition;

/// <summary>
/// 启动初始化：在端口监听前强制构建管理员凭据（生成 / 加载 admin.json 并打印口令日志），
/// 避免懒加载导致「启动后 data 目录为空、找不到口令」的困惑。
/// </summary>
public sealed class StartupInitializerService(AdminCredentialService adminCredential, DataPaths dataPaths, ILogger<StartupInitializerService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("数据目录（数据库 / 凭据 / 密钥 / 日志）: {DataDir}", dataPaths.Root);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
