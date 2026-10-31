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

    [Fact]
    public void ScheduleTask_And_SaveScheduleRequest_CarryExecutionTarget()
    {
        var task = new LinuxWebTool.Infrastructure.Shared.Persistence.Entities.ScheduleTask();
        Assert.Equal("container", task.ExecutionTarget);

        task.ExecutionTarget = "host";
        Assert.Equal("host", task.ExecutionTarget);

        var reqDefault = new SaveScheduleRequest(
            Name: "Backup",
            CommandId: Guid.NewGuid(),
            CronExpression: "0 0 * * *",
            Enabled: true,
            GroupId: null,
            IsPinned: false,
            TimeoutSeconds: 60,
            Arguments: null
        );
        Assert.Equal("container", reqDefault.ExecutionTarget);

        var reqHost = new SaveScheduleRequest(
            Name: "Host Clean",
            CommandId: Guid.NewGuid(),
            CronExpression: "0 0 * * *",
            Enabled: true,
            GroupId: null,
            IsPinned: false,
            TimeoutSeconds: 60,
            Arguments: null,
            ExecutionTarget: "host"
        );
        Assert.Equal("host", reqHost.ExecutionTarget);
    }

    [Fact]
    public void ScheduleItemResponse_CarriesExecutionTarget()
    {
        var resp = new LinuxWebTool.WebHost.Shared.Routes.ScheduleItemResponse(
            Id: Guid.NewGuid(),
            Name: "Test",
            CommandId: Guid.NewGuid(),
            CommandName: "ls",
            CommandText: "ls -l",
            ScriptType: 0,
            CronExpression: "* * * * *",
            Enabled: true,
            GroupId: null,
            GroupName: null,
            IsPinned: false,
            SortOrder: 0,
            TimeoutSeconds: 30,
            Arguments: null,
            LastRunTime: null,
            NextRunTime: null,
            CreateTime: DateTime.UtcNow,
            UpdateTime: DateTime.UtcNow,
            ExecutionTarget: "host"
        );

        Assert.Equal("host", resp.ExecutionTarget);
    }

    [Fact]
    public void CrossPlatformPtyEngine_HostTarget_BuildsDanglingFreeShellArgs()
    {
        var resolveShellMethod = typeof(LinuxWebTool.Infrastructure.Features.Terminal.Platform.CrossPlatformPtyEngine)
            .GetMethod("ResolveShell", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(resolveShellMethod);

        var result = resolveShellMethod.Invoke(null, [null, null, "host", "/var/log"]);
        Assert.NotNull(result);
        Assert.True(result is ValueTuple<string, IReadOnlyList<string>>);
    }
}
