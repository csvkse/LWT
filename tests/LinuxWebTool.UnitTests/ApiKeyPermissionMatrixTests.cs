using LinuxWebTool.Application.Features.Security;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class ApiKeyPermissionMatrixTests
{
    [Theory]
    [InlineData(true, false, "api", true)]
    [InlineData(true, false, "API", true)]
    [InlineData(true, false, "mcp", false)]
    [InlineData(false, true, "mcp", true)]
    [InlineData(false, true, "unknown", false)]
    public void CanAccessChannel_EvaluatesAccurately(bool allowApi, bool allowMcp, string channel, bool expected)
    {
        var result = ApiKeyPermissionMatrix.CanAccessChannel(allowApi, allowMcp, channel);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("terminal", true, false, false, false, false, true)]
    [InlineData("Terminal", false, false, false, false, false, false)]
    [InlineData("schedules", false, true, false, false, false, true)]
    [InlineData("schedule", false, true, false, false, false, true)]
    [InlineData("files", false, false, true, false, false, true)]
    [InlineData("file", false, false, true, false, false, true)]
    [InlineData("transcode", false, false, false, true, false, true)]
    [InlineData("gateway", false, false, false, false, true, true)]
    [InlineData("unsupported_module", true, true, true, true, true, false)]
    public void CanExecuteModule_EvaluatesAccurately(
        string module, bool allowTerm, bool allowSched, bool allowFiles, bool allowTrans, bool allowGate, bool expected)
    {
        var result = ApiKeyPermissionMatrix.CanExecuteModule(
            allowTerm, allowSched, allowFiles, allowTrans, allowGate, module);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("terminal_create", true, false, false, false, false, false, true)]
    [InlineData("terminal_create", false, false, false, false, false, false, false)]
    [InlineData("command_run", true, false, false, false, false, false, true)]
    [InlineData("command_run", false, false, false, false, false, false, false)]
    [InlineData("workflow_run", false, true, false, false, false, false, true)]
    [InlineData("workflow_run", false, false, false, false, false, false, false)]
    [InlineData("schedule_list", false, true, false, false, false, false, true)]
    [InlineData("file_read", false, false, true, false, false, false, true)]
    [InlineData("transcode_submit", false, false, false, true, false, false, true)]
    [InlineData("gateway_status", false, false, false, false, true, false, true)]
    [InlineData("remote_exec", false, false, false, false, false, false, false)]
    [InlineData("remote_exec", false, false, false, false, false, true, true)]
    [InlineData("remote_list_hosts", false, false, false, false, false, true, true)]
    [InlineData("system_info", true, true, true, true, true, true, true)] // 基础工具不受业务模块开关限制
    public void IsMcpToolPermitted_EvaluatesToolPrefixes(
        string tool, bool allowTerm, bool allowSched, bool allowFiles, bool allowTrans, bool allowGate, bool allowRemote, bool expected)
    {
        var result = ApiKeyPermissionMatrix.IsMcpToolPermitted(
            tool, allowTerm, allowSched, allowFiles, allowTrans, allowGate, allowRemote);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(false, null, "host-1", false)]
    [InlineData(true, null, "host-1", true)]
    [InlineData(true, "*", "host-1", true)]
    [InlineData(true, "host-1,host-2", "host-1", true)]
    [InlineData(true, "host-1,host-2", "host-2", true)]
    [InlineData(true, "host-1,host-2", "HOST-2", true)] // 大小写不敏感
    [InlineData(true, "host-1,host-2", "host-3", false)]
    [InlineData(true, "[\"host-1\", \"host-2\"]", "host-1", true)]
    [InlineData(true, "[\"host-1\", \"host-2\"]", "host-3", false)]
    public void CanAccessRemoteHost_EvaluatesCustomServerScopeAccurately(
        bool allowRemote, string? allowedHostIds, string targetHostId, bool expected)
    {
        var result = ApiKeyPermissionMatrix.CanAccessRemoteHost(allowRemote, allowedHostIds, targetHostId);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null, "command_run", true)]
    [InlineData("*", "command_run", true)]
    [InlineData("command_run,workflow_run", "command_run", true)]
    [InlineData("command_run,workflow_run", "workflow_run", true)]
    [InlineData("command_run,workflow_run", "WORKFLOW_RUN", true)] // 大小写不敏感
    [InlineData("command_run,workflow_run", "schedule_delete", false)]
    [InlineData("[\"command_run\", \"workflow_run\"]", "command_run", true)]
    [InlineData("[\"command_run\", \"workflow_run\"]", "schedule_create", false)]
    [InlineData("", "command_run", true)]
    [InlineData("   ", "command_run", true)]
    public void IsToolAllowed_EvaluatesAllowedToolsFilter(string? allowedTools, string toolName, bool expected)
    {
        var result = ApiKeyPermissionMatrix.IsToolAllowed(allowedTools, toolName);
        Assert.Equal(expected, result);
    }

    [Theory]
    // 模块未开启：即便在 AllowedTools 中也拒绝
    [InlineData(false, false, false, false, false, false, "command_run", "command_run", "commands", false)]
    [InlineData(false, false, false, false, false, false, "workflow_run", "workflow_run", "workflows", false)]
    // 模块已开启且 AllowedTools 包含该工具：允许
    [InlineData(true, false, false, false, false, false, "command_run", "command_run", "commands", true)]
    [InlineData(false, true, false, false, false, false, "workflow_run", "workflow_run", "workflows", true)]
    // 模块已开启但 AllowedTools 未包含该工具：拒绝
    [InlineData(true, false, false, false, false, false, "command_list", "command_delete", "commands", false)]
    [InlineData(false, true, false, false, false, false, "workflow_list", "workflow_toggle", "workflows", false)]
    // 模块已开启且 AllowedTools 为 null (全量模式)：允许
    [InlineData(true, false, false, false, false, false, null, "command_delete", "commands", true)]
    [InlineData(false, true, false, false, false, false, null, "workflow_toggle", "workflows", true)]
    public void IsToolPermitted_EvaluatesTwoLevelSecurityMatrix(
        bool allowTerm, bool allowSched, bool allowFiles, bool allowTrans, bool allowGate, bool allowRemote,
        string? allowedTools, string toolName, string moduleName, bool expected)
    {
        var result = ApiKeyPermissionMatrix.IsToolPermitted(
            allowTerm, allowSched, allowFiles, allowTrans, allowGate, allowRemote,
            allowedTools, toolName, moduleName);
        Assert.Equal(expected, result);
    }
}
