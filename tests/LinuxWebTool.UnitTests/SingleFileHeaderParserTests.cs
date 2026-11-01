using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class SingleFileHeaderParserTests
{
    [Fact]
    public void Parse_JsDoc_ExtractsAllAnnotations()
    {
        const string code = """
        /**
         * @name 网络上下行测速服务
         * @description 5分钟自动探测网速并上报
         * @runtime node
         * @profile resident
         * @port 8920
         * @version 1.2.3
         * @schedule */5 * * * *
         */
        const http = require('http');
        const server = http.createServer((req, res) => res.end('ok'));
        server.listen(8920);
        """;

        var meta = SingleFileHeaderParser.Parse(code, "speedtest.js");

        Assert.Equal("网络上下行测速服务", meta.Name);
        Assert.Equal("5分钟自动探测网速并上报", meta.Description);
        Assert.Equal(PluginRuntime.Node, meta.Runtime);
        Assert.Equal(PluginProfile.Resident, meta.Profile);
        Assert.Equal(8920, meta.Port);
        Assert.Equal("1.2.3", meta.Version);
        Assert.Equal("*/5 * * * *", meta.RawTags["schedule"]);
    }

    [Fact]
    public void Parse_PythonLineComments_ExtractsMetadata()
    {
        const string code = """
        # @name: 内存巡检与僵尸进程清理
        # @description: 自动回收无响应进程
        # @runtime: python
        # @profile: oneshot
        import os
        import time
        print("cleaner running")
        """;

        var meta = SingleFileHeaderParser.Parse(code, "zombie_cleaner.py");

        Assert.Equal("内存巡检与僵尸进程清理", meta.Name);
        Assert.Equal("自动回收无响应进程", meta.Description);
        Assert.Equal(PluginRuntime.Python, meta.Runtime);
        Assert.Equal(PluginProfile.OneShot, meta.Profile);
    }
}
