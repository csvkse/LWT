using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;
using Xunit;

namespace LinuxWebTool.UnitTests;

public sealed class PluginAutoStartAndGatingTests
{
    [Fact]
    public void PluginInstanceDto_AutoStart_DefaultsToTrueForResident()
    {
        var residentManifest = new PluginManifest
        {
            Id = "resident-service",
            Name = "Resident Service",
            Profile = PluginProfile.Resident,
            Supervision = new PluginSupervisionConfig()
        };

        var residentDto = new PluginInstanceDto
        {
            Manifest = residentManifest,
            State = PluginState.Stopped
        };

        Assert.True(residentDto.AutoStart);
    }

    [Fact]
    public void PluginInstanceDto_AutoStart_DefaultsToFalseForOneShot()
    {
        var oneShotManifest = new PluginManifest
        {
            Id = "oneshot-tool",
            Name = "OneShot Tool",
            Profile = PluginProfile.OneShot,
            Supervision = new PluginSupervisionConfig()
        };

        var oneShotDto = new PluginInstanceDto
        {
            Manifest = oneShotManifest,
            State = PluginState.Stopped
        };

        Assert.False(oneShotDto.AutoStart);
    }

    [Fact]
    public void PluginInstanceDto_AutoStart_RespectsExplicitFalseConfig()
    {
        var manifest = new PluginManifest
        {
            Id = "resident-manual",
            Name = "Resident Manual",
            Profile = PluginProfile.Resident,
            Supervision = new PluginSupervisionConfig
            {
                AutoStart = false
            }
        };

        var dto = new PluginInstanceDto
        {
            Manifest = manifest,
            State = PluginState.Stopped
        };

        Assert.False(dto.AutoStart);
    }

    [Fact]
    public void PluginStateMachine_MarkMissingDependencies_TransitionsCorrectly()
    {
        var sm = new PluginStateMachine("test-guard");
        Assert.Equal(PluginState.Stopped, sm.State);

        sm.MarkMissingDependencies("缺少必要二进制依赖: iptables");

        Assert.Equal(PluginState.MissingDependencies, sm.State);
        Assert.Equal("缺少必要二进制依赖: iptables", sm.LastError);
    }

    [Fact]
    public void PluginActionRequest_SupportsTargetProperty()
    {
        var request = new PluginActionRequest
        {
            PluginId = "ssh-access-guard",
            Action = "status",
            Target = "host"
        };

        Assert.Equal("host", request.Target);
    }

    [Fact]
    public void PluginActionResult_StructuredJsonParsing_PopulatesData()
    {
        var jsonOutput = """
        {
            "status": "active",
            "activeSessions": 2,
            "bannedCount": 5
        }
        """;

        JsonElement parsed;
        using (var doc = JsonDocument.Parse(jsonOutput))
        {
            parsed = doc.RootElement.Clone();
        }

        var result = new PluginActionResult
        {
            Status = "succeeded",
            ExitCode = 0,
            Data = parsed,
            Message = jsonOutput
        };
        Assert.Equal("succeeded", result.Status);
        Assert.NotNull(result.Data);

        var element = Assert.IsType<JsonElement>(result.Data);
        Assert.Equal("active", element.GetProperty("status").GetString());
        Assert.Equal(2, element.GetProperty("activeSessions").GetInt32());
        Assert.Equal(5, element.GetProperty("bannedCount").GetInt32());
    }
}
