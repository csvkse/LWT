namespace LinuxWebTool.WebHost.Features.Mcp.Engine;

/// <summary>
/// MCP 工具单次执行结果模型
/// </summary>
public sealed class McpToolExecutionResult
{
    public bool IsError { get; }
    public string Content { get; }

    private McpToolExecutionResult(bool isError, string content)
    {
        IsError = isError;
        Content = content;
    }

    public static McpToolExecutionResult Success(string text) => new(false, text);
    public static McpToolExecutionResult Error(string message) => new(true, message);
}
