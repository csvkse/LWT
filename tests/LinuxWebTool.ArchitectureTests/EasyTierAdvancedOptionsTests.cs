using LinuxWebTool.Contracts.Features.EasyTier.Contracts;
using LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using Xunit;

namespace LinuxWebTool.ArchitectureTests;

public sealed class EasyTierAdvancedOptionsTests
{
    [Fact]
    public void RoutingMode_Auto_OmitsRoutesFromToml()
    {
        var req = new CreateEasyTierNodeRequest(
            InstanceName: "test-auto",
            NetworkName: "vnet",
            NetworkSecret: null,
            VirtualIpv4: "10.126.127.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: [],
            ProxyNetworks: [],
            Routes: [],
            RawTomlOverride: null,
            AutoStart: false,
            RoutingMode: "auto");

        var toml = EasyTierConfigGenerator.GenerateToml(req);
        Assert.DoesNotContain("routes =", toml);
    }

    [Fact]
    public void RoutingMode_VirtualOnly_EmitsEmptyRoutesBeforeAnySubTable()
    {
        var req = new CreateEasyTierNodeRequest(
            InstanceName: "test-vo",
            NetworkName: "vnet",
            NetworkSecret: null,
            VirtualIpv4: "10.126.127.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: ["tcp://public.easytier.top:11010"],
            ProxyNetworks: [],
            Routes: ["10.0.0.0/8"], // Should be suppressed in virtual_only mode
            RawTomlOverride: null,
            AutoStart: false,
            RoutingMode: "virtual_only");

        var toml = EasyTierConfigGenerator.GenerateToml(req);
        Assert.Contains("routes = []", toml);
        Assert.DoesNotContain("10.0.0.0/8", toml);

        // Verify top-level scope: routes = [] must appear before any table header
        var lines = toml.Split('\n').Select(l => l.Trim()).ToList();
        var routesIndex = lines.FindIndex(l => l.StartsWith("routes = []"));
        var firstTableIndex = lines.FindIndex(l => l.StartsWith("["));
        Assert.True(routesIndex >= 0);
        Assert.True(firstTableIndex > routesIndex, "routes = [] must appear before any TOML [table]");
    }

    [Fact]
    public void RoutingMode_Manual_EmitsSpecifiedRoutesBeforeAnySubTable()
    {
        var req = new CreateEasyTierNodeRequest(
            InstanceName: "test-manual",
            NetworkName: "vnet",
            NetworkSecret: null,
            VirtualIpv4: "10.126.127.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: [],
            ProxyNetworks: [],
            Routes: ["10.0.0.0/8", "192.168.100.0/24"],
            RawTomlOverride: null,
            AutoStart: false,
            RoutingMode: "manual");

        var toml = EasyTierConfigGenerator.GenerateToml(req);
        Assert.Contains("routes = [\"10.0.0.0/8\", \"192.168.100.0/24\"]", toml);

        var lines = toml.Split('\n').Select(l => l.Trim()).ToList();
        var routesIndex = lines.FindIndex(l => l.StartsWith("routes ="));
        var firstTableIndex = lines.FindIndex(l => l.StartsWith("["));
        Assert.True(routesIndex >= 0);
        Assert.True(firstTableIndex > routesIndex, "routes = [...] must appear before any TOML [table]");
    }

    [Fact]
    public void SubnetMapping_ValidMatchingPrefixLengths_GeneratesSubnetMappingToml()
    {
        var req = new CreateEasyTierNodeRequest(
            InstanceName: "test-subnet",
            NetworkName: "vnet",
            NetworkSecret: null,
            VirtualIpv4: "10.126.127.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: [],
            ProxyNetworks: [],
            Routes: [],
            RawTomlOverride: null,
            AutoStart: false,
            ProxyNetworkRules:
            [
                new EasyTierProxyNetworkItem("192.168.1.0/24", "10.200.1.0/24"),
                new EasyTierProxyNetworkItem("192.168.2.0/24", null)
            ]);

        var toml = EasyTierConfigGenerator.GenerateToml(req);
        Assert.Contains("[[proxy_network]]", toml);
        Assert.Contains("cidr = \"192.168.1.0/24\"", toml);
        Assert.Contains("mapped_cidr = \"10.200.1.0/24\"", toml);
        Assert.Contains("cidr = \"192.168.2.0/24\"", toml);

        // Validation passes
        var valid = EasyTierConfigGenerator.ValidateConfig(toml, out var err);
        Assert.True(valid, err);
    }

    [Theory]
    [InlineData("192.168.1.0/24", "10.200.0.0/16")]
    [InlineData("192.168.1.0/24", "10.200.1.0/25")]
    public void SubnetMapping_MismatchedPrefixLengths_ThrowsArgumentException(string cidr, string mappedCidr)
    {
        var req = new CreateEasyTierNodeRequest(
            InstanceName: "test-subnet-fail",
            NetworkName: "vnet",
            NetworkSecret: null,
            VirtualIpv4: "10.126.127.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: [],
            ProxyNetworks: [],
            Routes: [],
            RawTomlOverride: null,
            AutoStart: false,
            ProxyNetworkRules: [new EasyTierProxyNetworkItem(cidr, mappedCidr)]);

        var ex = Assert.Throws<ArgumentException>(() => EasyTierConfigGenerator.GenerateToml(req));
        Assert.Contains("掩码前缀长度必须一致", ex.Message);
    }

    [Fact]
    public void SubnetMapping_IdenticalCidr_ThrowsArgumentException()
    {
        var req = new CreateEasyTierNodeRequest(
            InstanceName: "test-subnet-same",
            NetworkName: "vnet",
            NetworkSecret: null,
            VirtualIpv4: "10.126.127.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: [],
            ProxyNetworks: [],
            Routes: [],
            RawTomlOverride: null,
            AutoStart: false,
            ProxyNetworkRules: [new EasyTierProxyNetworkItem("192.168.1.0/24", "192.168.1.0/24")]);

        var ex = Assert.Throws<ArgumentException>(() => EasyTierConfigGenerator.GenerateToml(req));
        Assert.Contains("不能与原始 CIDR 完全相同", ex.Message);
    }

    [Fact]
    public void AdvancedConfig_EmitsAllFlagsProperly()
    {
        var req = new CreateEasyTierNodeRequest(
            InstanceName: "test-adv",
            NetworkName: "vnet",
            NetworkSecret: "secret123",
            VirtualIpv4: "10.126.127.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: [],
            ProxyNetworks: [],
            Routes: [],
            RawTomlOverride: null,
            AutoStart: false,
            Hostname: "nas-backup",
            VirtualIpv6: "fd00:1::1/64",
            MappedListeners: ["tcp://203.0.113.1:11010", "udp://203.0.113.1:11010"],
            AdvancedConfig: new EasyTierAdvancedConfig(
                DevName: "et_test0",
                Mtu: 1420,
                EnableIpv6: true,
                LatencyFirst: true,
                BindDevice: true, // Note: In EasyTier Rust, bind_device is boolean
                P2pMode: "p2p_only",
                DefaultProtocol: "udp",
                EnableUpnp: false,
                EnableTcpHolePunching: false,
                EnableUdpHolePunching: false,
                EnableSymHolePunching: false));

        var toml = EasyTierConfigGenerator.GenerateToml(req);

        // Top level properties
        Assert.Contains("hostname = \"nas-backup\"", toml);
        Assert.Contains("ipv6 = \"fd00:1::1/64\"", toml);
        Assert.Contains("mapped_listeners = [\"tcp://203.0.113.1:11010\", \"udp://203.0.113.1:11010\"]", toml);

        // [flags] properties
        Assert.Contains("[flags]", toml);
        Assert.Contains("dev_name = \"et_test0\"", toml);
        Assert.Contains("mtu = 1420", toml);
        Assert.Contains("enable_ipv6 = true", toml);
        Assert.Contains("latency_first = true", toml);
        Assert.Contains("bind_device = true", toml);
        Assert.Contains("p2p_only = true", toml);
        Assert.Contains("default_protocol = \"udp\"", toml);
        Assert.Contains("disable_upnp = true", toml);
        Assert.Contains("disable_tcp_hole_punching = true", toml);
        Assert.Contains("disable_udp_hole_punching = true", toml);
        Assert.Contains("disable_sym_hole_punching = true", toml);
    }

    [Theory]
    [InlineData("p2p_only", "p2p_only = true")]
    [InlineData("disable_p2p", "disable_p2p = true")]
    [InlineData("lazy_p2p", "lazy_p2p = true")]
    [InlineData("need_p2p", "need_p2p = true")]
    public void AdvancedConfig_P2pModes_AreMutuallyExclusive(string p2pMode, string expectedFlag)
    {
        var req = new CreateEasyTierNodeRequest(
            InstanceName: "test-p2p",
            NetworkName: "vnet",
            NetworkSecret: null,
            VirtualIpv4: "10.126.127.1/24",
            EnableDhcp: false,
            Listeners: ["tcp://0.0.0.0:11010"],
            Peers: [],
            ProxyNetworks: [],
            Routes: [],
            RawTomlOverride: null,
            AutoStart: false,
            AdvancedConfig: new EasyTierAdvancedConfig(P2pMode: p2pMode));

        var toml = EasyTierConfigGenerator.GenerateToml(req);
        Assert.Contains(expectedFlag, toml);

        var allFlags = new[] { "p2p_only = true", "disable_p2p = true", "lazy_p2p = true", "need_p2p = true" };
        foreach (var flag in allFlags.Where(f => f != expectedFlag))
        {
            Assert.DoesNotContain(flag, toml);
        }
    }

    [Fact]
    public void ErrorMessageSanitization_StripsTempPathsAndSecrets()
    {
        const string rawError = "Error parsing config at /tmp/easytier_check_a1b2c3d4.toml: line 5 column 1: secret: network_secret = \"super_sensitive_password\" failed";
        var sanitized = EasyTierConfigGenerator.SanitizeErrorMessage(rawError);

        Assert.DoesNotContain("/tmp/easytier_check_a1b2c3d4.toml", sanitized);
        Assert.DoesNotContain("super_sensitive_password", sanitized);
        Assert.Contains("[配置文件]", sanitized);
        Assert.Contains("network_secret = \"******\"", sanitized);
    }
}
