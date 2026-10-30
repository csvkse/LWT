namespace LinuxWebTool.Contracts.Features.Services.Contracts;

/// <summary>
/// OpenSSH 服务端配置模型。
/// </summary>
public sealed record SshServiceConfig
{
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    public int Port { get; init; } = 22;
    public string? PermitRootLogin { get; init; } // yes, no, prohibit-password
    public bool PasswordAuthentication { get; init; } = true;
    public bool PubkeyAuthentication { get; init; } = true;
    public string? ConfigFilePath { get; init; }
    public IReadOnlyList<string> AuthorizedKeys { get; init; } = [];
}

/// <summary>
/// 更新 OpenSSH 配置请求。
/// </summary>
public sealed record UpdateSshConfigRequest
{
    public int Port { get; init; } = 22;
    public string? PermitRootLogin { get; init; }
    public bool PasswordAuthentication { get; init; }
    public bool PubkeyAuthentication { get; init; }
    public IReadOnlyList<string>? AuthorizedKeys { get; init; }
}

/// <summary>
/// Samba 共享段定义。
/// </summary>
public sealed record SambaShareItem
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool ReadOnly { get; init; }
    public bool GuestOk { get; init; }
    public string? ValidUsers { get; init; }
    public string? Comment { get; init; }
}

/// <summary>
/// Samba 配置全景模型。
/// </summary>
public sealed record SambaServiceConfig
{
    public bool IsInstalled { get; init; }
    public bool IsRunning { get; init; }
    public string? Workgroup { get; init; }
    public string? ConfigFilePath { get; init; }
    public IReadOnlyList<SambaShareItem> Shares { get; init; } = [];
}

/// <summary>
/// 网络接口详细信息。
/// </summary>
public sealed record NetworkInterfaceDetail
{
    public required string Name { get; init; }
    public string? MacAddress { get; init; }
    public bool IsUp { get; init; }
    public bool IsLoopback { get; init; }
    public int Mtu { get; init; }
    public IReadOnlyList<string> Ipv4Addresses { get; init; } = [];
    public IReadOnlyList<string> Ipv6Addresses { get; init; } = [];
    public string? Gateway { get; init; }
}

/// <summary>
/// DNS 解析配置。
/// </summary>
public sealed record DnsResolverConfig
{
    public IReadOnlyList<string> Nameservers { get; init; } = [];
    public IReadOnlyList<string> SearchDomains { get; init; } = [];
    public string? SourceDescription { get; init; }
}

/// <summary>
/// 系统防火墙开放规则。
/// </summary>
public sealed record FirewallPortRule
{
    public required string RuleId { get; init; }
    public int Port { get; init; }
    public string Protocol { get; init; } = "tcp"; // tcp, udp, any
    public string Action { get; init; } = "allow"; // allow, deny
    public string? FromAddress { get; init; }
    public string? Comment { get; init; }
}

/// <summary>
/// 系统防火墙状态画像。
/// </summary>
public sealed record FirewallStatusInfo
{
    public bool IsInstalled { get; init; }
    public bool IsActive { get; init; }
    public string? BackendType { get; init; } // ufw, firewalld, iptables, windows
    public IReadOnlyList<FirewallPortRule> Rules { get; init; } = [];
}

/// <summary>
/// 修改防火墙规则请求。
/// </summary>
public sealed record UpdateFirewallRuleRequest
{
    public int Port { get; init; }
    public string Protocol { get; init; } = "tcp";
    public string Action { get; init; } = "allow"; // allow, delete
    public string? FromAddress { get; init; }
    public string? Comment { get; init; }
}
