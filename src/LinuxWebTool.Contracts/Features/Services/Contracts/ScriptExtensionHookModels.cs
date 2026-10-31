namespace LinuxWebTool.Contracts.Features.Services.Contracts;

/// <summary>
/// Hook 标准输入请求协议模型 (v1)。
/// </summary>
public sealed record ExtensionHookRequest
{
    public int ProtocolVersion { get; init; } = 1;
    public string OperationId { get; init; } = "";
    public string Action { get; init; } = "";
    public Dictionary<string, string> Parameters { get; init; } = [];
    public Dictionary<string, string> Config { get; init; } = [];
    public string Target { get; init; } = "container";
    public string Timestamp { get; init; } = "";
}

/// <summary>
/// Hook 标准输出响应协议模型 (v1)。
/// </summary>
public sealed record ExtensionHookResponse
{
    public int ProtocolVersion { get; init; } = 1;
    public string? OperationId { get; init; }
    public string Status { get; init; } = "succeeded"; // succeeded, failed
    public string? Message { get; init; }
    public string? Error { get; init; }
    public System.Text.Json.JsonElement? Data { get; init; }
}
