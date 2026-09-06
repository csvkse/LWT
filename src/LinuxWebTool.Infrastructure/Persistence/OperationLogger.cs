namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>操作日志写入接口（控制器 / 服务层在关键动作后调用）。</summary>
public interface IOperationLogger
{
    Task LogAsync(string action, string targetType, string targetName, string? detail = null, bool success = true, string? clientIp = null);
}

/// <summary>操作日志默认实现：写入 SQLite operation_log 表。</summary>
public class OperationLogger(OperationLogStore store, ILogger<OperationLogger> logger) : IOperationLogger
{
    public async Task LogAsync(string action, string targetType, string targetName, string? detail = null, bool success = true, string? clientIp = null)
    {
        try
        {
            await store.InsertAsync(new OperationLog
            {
                Action = action,
                TargetType = targetType,
                TargetName = targetName,
                Detail = detail,
                ClientIp = clientIp,
                Success = success,
            });
        }
        catch (Exception ex)
        {
            // 审计日志写失败不应阻断业务，但必须留下程序日志。
            logger.LogError(ex, "操作日志写入失败：{Action} {TargetType} {TargetName}", action, targetType, targetName);
        }
    }
}
