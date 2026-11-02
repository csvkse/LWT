using System;
using LinuxWebTool.Infrastructure.Features.Plugins;
using Xunit;

namespace LinuxWebTool.UnitTests;

[Collection("EnvironmentTests")]
public sealed class PluginSafeModeGuardTests : IDisposable
{
    private static readonly string[] MonitoredVars =
    [
        "LWT_SAFE_MODE",
        "LWT_PLUGINS_SAFE_MODE",
        "SAFE_MODE",
        "LWT_PLUGINS_DISABLE_AUTO_START",
        "DISABLE_AUTO_START"
    ];

    public PluginSafeModeGuardTests()
    {
        ClearVars();
    }

    public void Dispose()
    {
        ClearVars();
    }

    private static void ClearVars()
    {
        foreach (var v in MonitoredVars)
        {
            Environment.SetEnvironmentVariable(v, null);
        }
    }

    [Theory]
    [InlineData("LWT_SAFE_MODE", "true", true)]
    [InlineData("LWT_SAFE_MODE", "1", true)]
    [InlineData("LWT_SAFE_MODE", "yes", true)]
    [InlineData("LWT_SAFE_MODE", "on", true)]
    [InlineData("LWT_SAFE_MODE", "TRUE", true)]
    [InlineData("LWT_SAFE_MODE", "false", false)]
    [InlineData("LWT_SAFE_MODE", "0", false)]
    [InlineData("LWT_SAFE_MODE", "no", false)]
    [InlineData("LWT_SAFE_MODE", "off", false)]
    [InlineData("LWT_SAFE_MODE", "", false)]
    [InlineData("LWT_PLUGINS_SAFE_MODE", "1", true)]
    [InlineData("SAFE_MODE", "true", true)]
    public void IsSafeModeEnabled_EvaluatesTruthyValues(string varName, string value, bool expected)
    {
        ClearVars();
        Environment.SetEnvironmentVariable(varName, value);
        try
        {
            Assert.Equal(expected, PluginSafeModeGuard.IsSafeModeEnabled());
        }
        finally
        {
            ClearVars();
        }
    }

    [Theory]
    [InlineData("LWT_PLUGINS_DISABLE_AUTO_START", "true", true)]
    [InlineData("LWT_PLUGINS_DISABLE_AUTO_START", "1", true)]
    [InlineData("DISABLE_AUTO_START", "yes", true)]
    [InlineData("DISABLE_AUTO_START", "false", false)]
    public void IsAutoStartDisabled_EvaluatesTruthyValues(string varName, string value, bool expected)
    {
        ClearVars();
        Environment.SetEnvironmentVariable(varName, value);
        try
        {
            Assert.Equal(expected, PluginSafeModeGuard.IsAutoStartDisabled());
        }
        finally
        {
            ClearVars();
        }
    }

    [Fact]
    public void GetSystemMode_WhenSafeModeEnabled_ReturnsSafeModeStatus()
    {
        ClearVars();
        Environment.SetEnvironmentVariable("LWT_SAFE_MODE", "true");
        try
        {
            var status = PluginSafeModeGuard.GetSystemMode();

            Assert.True(status.IsSafeMode);
            Assert.True(status.IsAutoStartDisabled);
            Assert.Equal("SafeMode", status.Mode);
            Assert.Contains("安全排查模式", status.Message);
        }
        finally
        {
            ClearVars();
        }
    }

    [Fact]
    public void GetSystemMode_WhenAutoStartDisabledOnly_ReturnsNoAutoStartStatus()
    {
        ClearVars();
        Environment.SetEnvironmentVariable("LWT_PLUGINS_DISABLE_AUTO_START", "true");
        try
        {
            var status = PluginSafeModeGuard.GetSystemMode();

            Assert.False(status.IsSafeMode);
            Assert.True(status.IsAutoStartDisabled);
            Assert.Equal("NoAutoStart", status.Mode);
            Assert.Contains("禁用插件开机自启", status.Message);
        }
        finally
        {
            ClearVars();
        }
    }

    [Fact]
    public void GetSystemMode_WhenNormal_ReturnsNormalStatus()
    {
        ClearVars();
        var status = PluginSafeModeGuard.GetSystemMode();

        Assert.False(status.IsSafeMode);
        Assert.False(status.IsAutoStartDisabled);
        Assert.Equal("Normal", status.Mode);
        Assert.Contains("系统运行正常", status.Message);
    }
}
