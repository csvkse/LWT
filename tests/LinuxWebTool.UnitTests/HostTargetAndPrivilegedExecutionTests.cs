using LinuxWebTool.Contracts.Features.Commands.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Terminal.Contracts;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Contracts.Shared.Contracts;
using Xunit;

namespace LinuxWebTool.UnitTests;

public class HostTargetAndPrivilegedExecutionTests
{
    [Fact]
    public void ShellRequest_TargetDefaultsToContainer_CanBeSetToHost()
    {
        var defaultReq = new ShellRequest { CommandText = "whoami" };
        Assert.Equal("container", defaultReq.Target);

        var hostReq = new ShellRequest { CommandText = "whoami", Target = "host" };
        Assert.Equal("host", hostReq.Target);
    }

    [Fact]
    public void PtyStartOptions_TargetDefaultsToContainer_CanBeSetToHost()
    {
        var defaultOpt = new PtyStartOptions(Executable: "/bin/bash");
        Assert.Equal("container", defaultOpt.Target);

        var hostOpt = new PtyStartOptions(Executable: "/bin/bash", Target: "host");
        Assert.Equal("host", hostOpt.Target);
    }

    [Fact]
    public void CommandExecutionContext_CarriesExecutionTarget()
    {
        var ctx = new CommandExecutionContext(
            CommandText: "ls -la",
            ScriptType: ScriptType.Command,
            Arguments: null,
            TimeoutSeconds: 30,
            Target: ExecutionTarget.Local,
            ExecutionTarget: "host"
        );

        Assert.Equal("host", ctx.ExecutionTarget);
    }

    [Fact]
    public void ExtensionManifest_And_Instance_SupportTarget()
    {
        var manifest = new ExtensionManifest
        {
            Id = "ssh-access-guard",
            Name = "SSH 访问安全卫士",
            Target = "host"
        };
        Assert.Equal("host", manifest.Target);

        var instance = new ExtensionInstance
        {
            Manifest = manifest,
            Status = "Running",
            Target = "host"
        };
        Assert.Equal("host", instance.Target);
    }

    [Fact]
    public void WorkerQuickDeployRequest_HoldsRequiredFields()
    {
        var req = new WorkerQuickDeployRequest
        {
            FileName = "worker.js",
            Content = "console.log('hi');",
            Target = "host",
            AutoStart = true
        };

        Assert.Equal("worker.js", req.FileName);
        Assert.Equal("console.log('hi');", req.Content);
        Assert.Equal("host", req.Target);
        Assert.True(req.AutoStart);
    }
}
