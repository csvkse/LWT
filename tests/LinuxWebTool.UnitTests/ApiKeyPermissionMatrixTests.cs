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
    [InlineData("terminal_create", true, false, false, false, false, true)]
    [InlineData("terminal_create", false, false, false, false, false, false)]
    [InlineData("schedule_list", false, true, false, false, false, true)]
    [InlineData("file_read", false, false, true, false, false, true)]
    [InlineData("transcode_submit", false, false, false, true, false, true)]
    [InlineData("gateway_status", false, false, false, false, true, true)]
    [InlineData("system_info", true, true, true, true, true, true)] // 基础工具不受业务模块开关限制
    public void IsMcpToolPermitted_EvaluatesToolPrefixes(
        string tool, bool allowTerm, bool allowSched, bool allowFiles, bool allowTrans, bool allowGate, bool expected)
    {
        var result = ApiKeyPermissionMatrix.IsMcpToolPermitted(
            tool, allowTerm, allowSched, allowFiles, allowTrans, allowGate);
        Assert.Equal(expected, result);
    }
}
