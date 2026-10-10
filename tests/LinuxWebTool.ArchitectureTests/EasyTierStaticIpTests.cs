using LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class EasyTierStaticIpTests
{
    [Theory]
    [InlineData("10.126.127.1/24", true, false)]
    [InlineData("10.126.127.1/24", false, false)]
    [InlineData(null, true, true)]
    [InlineData("   ", true, true)]
    [InlineData(null, false, false)]
    public void GeneratedConfig_UsesStaticIpBeforeDhcp(string? ipv4, bool dhcp, bool expectedDhcp)
    {
        var entity = new EasyTierNodeEntity
        {
            InstanceName = "static-ip-test",
            VirtualIpv4 = ipv4,
            EnableDhcp = dhcp
        };
        var request = new CreateEasyTierNodeRequest(
            entity.InstanceName, entity.NetworkName, null, ipv4, dhcp,
            null, null, null, null, null, false);

        foreach (var toml in new[] { EasyTierConfigGenerator.GenerateToml(entity), EasyTierConfigGenerator.GenerateToml(request) })
        {
            // 地址和 DHCP 必须在首个 TOML 表之前，作为内核的顶层配置。
            var root = toml.Split("[network_identity]", StringSplitOptions.None)[0];
            Assert.Contains($"dhcp = {expectedDhcp.ToString().ToLowerInvariant()}", root);
            if (!string.IsNullOrWhiteSpace(ipv4))
                Assert.Contains($"ipv4 = \"{ipv4}\"", root);
            else
                Assert.DoesNotContain("ipv4 =", root);
        }
    }

    [Fact]
    public void RawTomlOverride_PreservesExplicitDhcpChoice()
    {
        const string raw = "inst_name = \"raw\"\nipv4 = \"10.126.127.1/24\"\ndhcp = true";
        var entity = new EasyTierNodeEntity { VirtualIpv4 = "10.126.127.1/24", RawTomlOverride = raw };
        var request = new CreateEasyTierNodeRequest("raw", "default", null, entity.VirtualIpv4, true,
            null, null, null, null, raw, false);

        Assert.Equal(raw, EasyTierConfigGenerator.GenerateToml(entity));
        Assert.Equal(raw, EasyTierConfigGenerator.GenerateToml(request));
    }
}
