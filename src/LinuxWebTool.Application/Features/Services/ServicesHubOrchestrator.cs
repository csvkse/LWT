using LinuxWebTool.Contracts.Features.Services.Contracts;

namespace LinuxWebTool.Application.Features.Services;

/// <summary>
/// 服务中枢应用层门面调度器实现：
/// 统一承载环境画像探测、子模块调度与安全守卫。
/// </summary>
public sealed class ServicesHubOrchestrator : IServicesHubOrchestrator
{
    private readonly IServiceEnvironmentDetector _envDetector;

    public ServicesHubOrchestrator(IServiceEnvironmentDetector envDetector)
    {
        _envDetector = envDetector;
    }

    public Task<ServiceEnvironmentInfo> GetEnvironmentAsync(CancellationToken cancellationToken = default)
    {
        return _envDetector.DetectAsync(cancellationToken);
    }
}
