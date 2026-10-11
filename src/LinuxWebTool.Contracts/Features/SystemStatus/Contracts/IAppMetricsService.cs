namespace LinuxWebTool.Contracts.Features.SystemStatus.Contracts;

/// <summary>应用系统指标收集与快照服务契约。</summary>
public interface IAppMetricsService
{
    void RecordRequest(string method, int statusCode, double elapsedMs);
    void IncrementActiveRequests();
    void DecrementActiveRequests();
    void SetActiveTerminalSessions(int count);
    AppMetricsSnapshot GetSnapshot();
}
