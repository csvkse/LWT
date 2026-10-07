
namespace LinuxWebTool.Application.Features.Commands;

/// <summary>
/// 命令执行与保存用例流程服务（纯业务编排逻辑）：
/// - 请求参数合法性严格校验；
/// - 统一超时时间约束守卫（默认 30s，上下限 [1, 3600]s）；
/// - 位置参数拆分与安全防护。
/// </summary>
public static class CommandWorkflowService
{
    public const int DefaultTimeoutSeconds = 30;
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3600;

    public static (bool IsValid, string? ErrorMessage) ValidateSaveRequest(SaveCommandRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (false, "指令名称不能为空");
        }

        if (string.IsNullOrWhiteSpace(request.CommandText))
        {
            return (false, "指令内容不能为空");
        }

        if (request.TimeoutSeconds.HasValue &&
            (request.TimeoutSeconds.Value < MinTimeoutSeconds || request.TimeoutSeconds.Value > MaxTimeoutSeconds))
        {
            return (false, $"超时秒数超出有效范围 [{MinTimeoutSeconds}, {MaxTimeoutSeconds}]");
        }

        return (true, null);
    }

    public static int ClampTimeout(int? requestedTimeout)
    {
        if (!requestedTimeout.HasValue || requestedTimeout.Value <= 0)
        {
            return DefaultTimeoutSeconds;
        }

        return Math.Clamp(requestedTimeout.Value, MinTimeoutSeconds, MaxTimeoutSeconds);
    }
}
