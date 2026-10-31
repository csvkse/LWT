using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Infrastructure.Features.Workers;
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
         * @mode service
         * @port 8920
         * @target host
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
        Assert.Equal("node", meta.Runtime);
        Assert.Equal(WorkerServiceMode.Service, meta.Mode);
        Assert.Equal(8920, meta.Port);
        Assert.Equal("host", meta.Target);
        Assert.Equal("1.2.3", meta.Version);
        Assert.Equal("*/5 * * * *", meta.Schedule);
    }

    [Fact]
    public void Parse_PythonLineComments_ExtractsMetadata()
    {
        const string code = """
        # @name: 内存巡检与僵尸进程清理
        # @description: 自动回收无响应进程
        # @runtime: python
        # @mode: daemon
        # @target: host
        import os
        import time
        print("daemon running")
        """;

        var meta = SingleFileHeaderParser.Parse(code, "zombie_cleaner.py");

        Assert.Equal("内存巡检与僵尸进程清理", meta.Name);
        Assert.Equal("自动回收无响应进程", meta.Description);
        Assert.Equal("python", meta.Runtime);
        Assert.Equal(WorkerServiceMode.Daemon, meta.Mode);
        Assert.Equal("host", meta.Target);
    }

    [Fact]
    public void Parse_BashScript_InfersRuntimeAndDefaultDaemon()
    {
        const string code = """
        #!/usr/bin/env bash
        # @name 备份宿主配置
        # @target host
        tar -czf /backup/etc.tar.gz /etc
        """;

        var meta = SingleFileHeaderParser.Parse(code, "backup.sh");

        Assert.Equal("备份宿主配置", meta.Name);
        Assert.Equal("bash", meta.Runtime);
        Assert.Equal("host", meta.Target);
        Assert.Equal(WorkerServiceMode.Daemon, meta.Mode);
    }

    [Fact]
    public void Parse_NoAnnotations_InfersFromExtensionAndListen()
    {
        const string code = """
        const express = require('express');
        const app = express();
        app.listen(3000);
        """;

        var meta = SingleFileHeaderParser.Parse(code, "my-service.js");

        Assert.Equal("my-service", meta.Name);
        Assert.Equal("node", meta.Runtime);
        Assert.Equal("container", meta.Target);
        Assert.Equal(WorkerServiceMode.Service, meta.Mode); // Inferred from .listen(
    }
}
