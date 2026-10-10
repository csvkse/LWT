using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;

internal static class EasyTierNetworkGuard
{
    public static string? GetVirtualNetwork(string toml)
    {
        string? address = null;
        var dhcp = false;
        foreach (var line in toml.Split('\n'))
        {
            var text = line.Trim();
            if (text.StartsWith('[')) break; // IPv4 and DHCP must be top-level TOML fields.
            if (Regex.IsMatch(text, "^\"[^\"]*\\\\[^\"]*\"\\s*="))
                throw new InvalidOperationException("安全检查不支持转义 TOML 键名，请使用 ipv4/dhcp 等普通键名");
            var assignment = Regex.Match(text, "^[\"']?(ipv4|dhcp)[\"']?\\s*=\\s*(.*)$");
            if (!assignment.Success) continue;
            var value = assignment.Groups[2].Value;
            if (assignment.Groups[1].Value == "dhcp")
            {
                var boolean = value.Split('#', 2)[0].Trim();
                if (boolean is not ("true" or "false")) throw new InvalidOperationException("无法安全解析原始 TOML 的 dhcp，请使用 true 或 false");
                dhcp = boolean == "true";
            }
            else
            {
                var quoted = Regex.Match(value, "^[\"']([0-9./]*)[\"']\\s*(?:#.*)?$");
                if (!quoted.Success) throw new InvalidOperationException("无法安全解析原始 TOML 的 ipv4，请使用 IPv4/CIDR 字面地址");
                address = quoted.Groups[1].Value;
            }
        }
        if (dhcp) return ""; // Address is not known until the native engine negotiates it.
        return string.IsNullOrWhiteSpace(address) ? null : address;
    }

    public static void Validate(string toml, string[] active, string[] local)
    {
        var candidate = GetVirtualNetwork(toml);
        if (candidate is null) return; // Relay-only node has no virtual IPv4 network.
        if (candidate.Length == 0)
        {
            if (active.Length > 0) throw new InvalidOperationException("已有虚拟网络运行，无法验证 DHCP 网段冲突；请指定不重叠的静态 IPv4 后启动");
            return; // Preserve single-node DHCP; its runtime address still needs verification.
        }
        var network = Parse(candidate);
        foreach (var other in active)
        {
            if (string.IsNullOrWhiteSpace(other)) throw new InvalidOperationException("运行节点的 DHCP 网段未知，拒绝启动另一个虚拟网络");
            if (Overlaps(network, Parse(other))) throw new InvalidOperationException($"虚拟网段 {candidate} 与运行节点 {other} 重叠，拒绝启动");
        }
        foreach (var other in local)
            if (Overlaps(network, Parse(other))) throw new InvalidOperationException($"虚拟网段 {candidate} 与本机网络 {other} 重叠，拒绝启动");
    }

    public static string[] GetLocalNetworks(IEnumerable<string> managedDevices, IEnumerable<string?> managedAddresses)
    {
        var excluded = managedDevices.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var addresses = managedAddresses.Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!.Split('/')[0]).ToHashSet(StringComparer.Ordinal);
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => !excluded.Contains(n.Name) && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(n => !n.GetIPProperties().UnicastAddresses.Any(a => addresses.Contains(a.Address.ToString())))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => $"{a.Address}/{a.PrefixLength}").ToArray();
    }

    private static (uint Address, int Prefix) Parse(string value)
    {
        var parts = value.Split('/');
        if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException($"无效的虚拟 IPv4 网段：{value}");
        var prefix = 24;
        if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > 32))
            throw new InvalidOperationException($"无效的 IPv4 掩码：{value}");
        var bytes = ip.GetAddressBytes();
        return (((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3], prefix);
    }

    private static bool Overlaps((uint Address, int Prefix) left, (uint Address, int Prefix) right)
    {
        var prefix = Math.Min(left.Prefix, right.Prefix);
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return (left.Address & mask) == (right.Address & mask);
    }
}
