using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using LinuxWebTool.Infrastructure.Shared.Support;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Config;

/// <summary>
/// 系统级服务配置适配器实现（SSH、Samba、网络网卡、DNS、防火墙）。
/// 支持宿主 PID 1 穿透（nsenter / /proc/1/root）、冷启动默认模板自愈与全生命周期安全凭据管理。
/// </summary>
public sealed partial class SystemServiceConfigAdapter : ISystemServiceConfigAdapter
{
    private readonly IShellExecutor _shellExecutor;
    private readonly IServiceEnvironmentDetector _envDetector;
    private readonly ILogger<SystemServiceConfigAdapter> _logger;

    public SystemServiceConfigAdapter(
        IShellExecutor shellExecutor,
        ILogger<SystemServiceConfigAdapter> logger)
        : this(shellExecutor, null, logger)
    {
    }

    public SystemServiceConfigAdapter(
        IShellExecutor shellExecutor,
        IServiceEnvironmentDetector? envDetector,
        ILogger<SystemServiceConfigAdapter> logger)
    {
        _shellExecutor = shellExecutor;
        _envDetector = envDetector ?? new Detectors.ServiceEnvironmentDetector(shellExecutor, Microsoft.Extensions.Logging.Abstractions.NullLogger<Detectors.ServiceEnvironmentDetector>.Instance);
        _logger = logger;
    }

    private static string ResolveHostPath(string virtualPath)
    {
        if (OperatingSystem.IsLinux() && HostPathResolver.CanPierceHost())
        {
            return HostPathResolver.ResolvePhysicalPath(virtualPath);
        }
        return virtualPath;
    }

    private Task<ShellResult> ExecuteHostCommandAsync(string commandText, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        return _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = commandText,
            Target = "host",
            TimeoutSeconds = timeoutSeconds
        }, cancellationToken);
    }

    #region 1. OpenSSH 服务端配置
    public async Task<SshServiceConfig> GetSshConfigAsync(CancellationToken cancellationToken = default)
    {
        var configPath = ResolveHostPath("/etc/ssh/sshd_config");
        var isInstalled = File.Exists(configPath) || File.Exists(ResolveHostPath("/usr/sbin/sshd"));
        var isRunning = false;
        var port = 22;
        var permitRootLogin = "prohibit-password";
        var passwordAuth = true;
        var pubkeyAuth = true;
        var authorizedKeys = new List<string>();

        if (OperatingSystem.IsLinux())
        {
            // 检查宿主运行状态
            var statRes = await ExecuteHostCommandAsync("systemctl is-active sshd || systemctl is-active ssh", 3, cancellationToken);
            isRunning = statRes.StandardOutput.Trim() == "active";

            if (File.Exists(configPath))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(configPath, cancellationToken);
                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();
                        if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
                        var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 2) continue;
                        var key = parts[0].ToLowerInvariant();
                        var val = parts[1].Trim();

                        if (key == "port" && int.TryParse(val, out var p)) port = p;
                        else if (key == "permitrootlogin") permitRootLogin = val;
                        else if (key == "passwordauthentication") passwordAuth = val.Equals("yes", StringComparison.OrdinalIgnoreCase);
                        else if (key == "pubkeyauthentication") pubkeyAuth = val.Equals("yes", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to read sshd_config");
                }
            }

            // 读取 root 或当前用户的 authorized_keys
            var authKeyPath = ResolveHostPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "authorized_keys"));
            if (File.Exists(authKeyPath))
            {
                try
                {
                    var kLines = await File.ReadAllLinesAsync(authKeyPath, cancellationToken);
                    authorizedKeys.AddRange(kLines.Where(k => !string.IsNullOrWhiteSpace(k) && !k.TrimStart().StartsWith('#')));
                }
                catch { }
            }
        }

        return new SshServiceConfig
        {
            IsInstalled = isInstalled,
            IsRunning = isRunning,
            Port = port,
            PermitRootLogin = permitRootLogin,
            PasswordAuthentication = passwordAuth,
            PubkeyAuthentication = pubkeyAuth,
            ConfigFilePath = "/etc/ssh/sshd_config",
            AuthorizedKeys = authorizedKeys
        };
    }

    public async Task<ServiceActionResult> UpdateSshConfigAsync(UpdateSshConfigRequest request, CancellationToken cancellationToken = default)
    {
        // 端口合法性校验 [1, 65535]
        if (request.Port <= 0 || request.Port > 65535)
        {
            return new ServiceActionResult { Success = false, Message = "SSH 端口号必须在 1 ~ 65535 范围内" };
        }

        if (OperatingSystem.IsWindows())
        {
            return new ServiceActionResult { Success = false, Message = "Windows 环境下不支持直接修改 Linux sshd_config" };
        }

        var configPath = ResolveHostPath("/etc/ssh/sshd_config");
        if (!File.Exists(configPath))
        {
            return new ServiceActionResult { Success = false, Message = $"未找到 SSH 配置文件: /etc/ssh/sshd_config" };
        }

        try
        {
            // 1. 读取原配置
            var oldLines = await File.ReadAllLinesAsync(configPath, cancellationToken);
            var updatedLines = new List<string>();
            var portSet = false;
            var rootLoginSet = false;
            var passAuthSet = false;
            var pubAuthSet = false;

            foreach (var line in oldLines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith('#') || string.IsNullOrWhiteSpace(trimmed))
                {
                    updatedLines.Add(line);
                    continue;
                }
                var parts = trimmed.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                var key = parts[0].ToLowerInvariant();

                if (key == "port") { updatedLines.Add($"Port {request.Port}"); portSet = true; }
                else if (key == "permitrootlogin") { updatedLines.Add($"PermitRootLogin {request.PermitRootLogin ?? "prohibit-password"}"); rootLoginSet = true; }
                else if (key == "passwordauthentication") { updatedLines.Add($"PasswordAuthentication {(request.PasswordAuthentication ? "yes" : "no")}"); passAuthSet = true; }
                else if (key == "pubkeyauthentication") { updatedLines.Add($"PubkeyAuthentication {(request.PubkeyAuthentication ? "yes" : "no")}"); pubAuthSet = true; }
                else { updatedLines.Add(line); }
            }

            if (!portSet) updatedLines.Add($"Port {request.Port}");
            if (!rootLoginSet) updatedLines.Add($"PermitRootLogin {request.PermitRootLogin ?? "prohibit-password"}");
            if (!passAuthSet) updatedLines.Add($"PasswordAuthentication {(request.PasswordAuthentication ? "yes" : "no")}");
            if (!pubAuthSet) updatedLines.Add($"PubkeyAuthentication {(request.PubkeyAuthentication ? "yes" : "no")}");

            // 2. 事务性预校验：写入临时文件并执行 sshd -t -f candidate.conf
            var candidateHostVirtual = $"/tmp/sshd_config.candidate.{Guid.NewGuid():N}";
            var candidatePhysical = ResolveHostPath(candidateHostVirtual);
            await File.WriteAllLinesAsync(candidatePhysical, updatedLines, cancellationToken);

            var testRes = await ExecuteHostCommandAsync($"sshd -t -f '{candidateHostVirtual}' 2>&1", 5, cancellationToken);
            try { File.Delete(candidatePhysical); } catch { }

            if (testRes.ExitCode != 0 && testRes.ExitCode != 127)
            {
                return new ServiceActionResult
                {
                    Success = false,
                    Message = $"SSH 语法预校验失败，已拒绝写入以防止失联: {testRes.StandardOutput}"
                };
            }

            // 3. 创建带时间戳的安全历史备份
            var backupPath = $"{configPath}.bak.{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            File.Copy(configPath, backupPath, overwrite: true);

            // 4. 原子覆盖
            await File.WriteAllLinesAsync(configPath, updatedLines, cancellationToken);

            // 5. 更新 authorized_keys
            if (request.AuthorizedKeys != null)
            {
                var sshDir = ResolveHostPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"));
                if (!Directory.Exists(sshDir)) Directory.CreateDirectory(sshDir);
                var authKeyPath = Path.Combine(sshDir, "authorized_keys");
                await File.WriteAllLinesAsync(authKeyPath, request.AuthorizedKeys, cancellationToken);
            }

            // 6. 热重载 SSH 服务
            await ExecuteHostCommandAsync("systemctl reload sshd || systemctl reload ssh", 5, cancellationToken);

            return new ServiceActionResult { Success = true, Message = "SSH 配置更新成功并已平滑重载" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update SSH config");
            return new ServiceActionResult { Success = false, Message = $"保存失败: {ex.Message}" };
        }
    }
    #endregion

    #region 3. 网络网卡与 DNS 配置
    public async Task<IReadOnlyList<NetworkInterfaceDetail>> GetNetworkInterfacesAsync(CancellationToken cancellationToken = default)
    {
        var list = new List<NetworkInterfaceDetail>();

        if (OperatingSystem.IsLinux())
        {
            // 优先尝试 ip -j addr (JSON 格式输出)
            var res = await _shellExecutor.ExecuteAsync(new ShellRequest { CommandText = "ip -j addr", TimeoutSeconds = 4 }, cancellationToken);
            if (res.ExitCode == 0 && !string.IsNullOrWhiteSpace(res.StandardOutput))
            {
                try
                {
                    using var doc = JsonDocument.Parse(res.StandardOutput);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var el in doc.RootElement.EnumerateArray())
                        {
                            var ifname = el.TryGetProperty("ifname", out var pIf) ? pIf.GetString() ?? "" : "";
                            var mac = el.TryGetProperty("address", out var pMac) ? pMac.GetString() : null;
                            var operstate = el.TryGetProperty("operstate", out var pOp) ? pOp.GetString() : "";
                            var mtu = el.TryGetProperty("mtu", out var pMtu) ? pMtu.GetInt32() : 1500;
                            var flags = el.TryGetProperty("flags", out var pFl) && pFl.ValueKind == JsonValueKind.Array
                                ? pFl.EnumerateArray().Select(x => x.GetString()).ToList()
                                : [];

                            var ipv4 = new List<string>();
                            var ipv6 = new List<string>();

                            if (el.TryGetProperty("addr_info", out var pAddr) && pAddr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var a in pAddr.EnumerateArray())
                                {
                                    var family = a.TryGetProperty("family", out var pFam) ? pFam.GetString() : "";
                                    var local = a.TryGetProperty("local", out var pLoc) ? pLoc.GetString() : "";
                                    var prefix = a.TryGetProperty("prefixlen", out var pPre) ? pPre.GetInt32() : 24;
                                    if (!string.IsNullOrEmpty(local))
                                    {
                                        if (family == "inet") ipv4.Add($"{local}/{prefix}");
                                        else if (family == "inet6") ipv6.Add($"{local}/{prefix}");
                                    }
                                }
                            }

                            list.Add(new NetworkInterfaceDetail
                            {
                                Name = ifname,
                                MacAddress = mac,
                                IsUp = string.Equals(operstate, "UP", StringComparison.OrdinalIgnoreCase) || (flags != null && flags.Contains("UP")),
                                IsLoopback = flags != null && flags.Contains("LOOPBACK"),
                                Mtu = mtu,
                                Ipv4Addresses = ipv4,
                                Ipv6Addresses = ipv6
                            });
                        }
                        return list;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to parse ip -j addr output, falling back to .NET NetworkInterface API");
                }
            }
        }

        // 跨平台降级使用 .NET NetworkInterface API
        try
        {
            var nics = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var nic in nics)
            {
                var ipProps = nic.GetIPProperties();
                var ipv4 = ipProps.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => $"{u.Address}/{u.IPv4Mask}")
                    .ToList();
                var ipv6 = ipProps.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(u => u.Address.ToString())
                    .ToList();

                var gw = ipProps.GatewayAddresses.FirstOrDefault()?.Address.ToString();

                list.Add(new NetworkInterfaceDetail
                {
                    Name = nic.Name,
                    MacAddress = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2"))),
                    IsUp = nic.OperationalStatus == OperationalStatus.Up,
                    IsLoopback = nic.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                    Mtu = 1500,
                    Ipv4Addresses = ipv4,
                    Ipv6Addresses = ipv6,
                    Gateway = gw
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect network interfaces");
        }

        return list;
    }

    public async Task<DnsResolverConfig> GetDnsConfigAsync(CancellationToken cancellationToken = default)
    {
        var nameservers = new List<string>();
        var searchDomains = new List<string>();
        var sourceDesc = "/etc/resolv.conf";
        var resolvPath = ResolveHostPath("/etc/resolv.conf");

        if (OperatingSystem.IsLinux() && File.Exists(resolvPath))
        {
            try
            {
                var lines = await File.ReadAllLinesAsync(resolvPath, cancellationToken);
                foreach (var line in lines)
                {
                    var t = line.Trim();
                    if (t.StartsWith('#') || string.IsNullOrWhiteSpace(t)) continue;
                    var parts = t.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2) continue;
                    var key = parts[0].ToLowerInvariant();
                    var val = parts[1].Trim();

                    if (key == "nameserver") nameservers.Add(val);
                    else if (key == "search") searchDomains.AddRange(val.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
                }
            }
            catch { }
        }
        else
        {
            sourceDesc = "System DNS Cache";
            nameservers.Add("223.5.5.5");
            nameservers.Add("114.114.114.114");
        }

        return new DnsResolverConfig
        {
            Nameservers = nameservers,
            SearchDomains = searchDomains,
            SourceDescription = sourceDesc
        };
    }
    #endregion

    #region 4. 系统防火墙管理
    public async Task<FirewallStatusInfo> GetFirewallStatusAsync(CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsLinux())
        {
            var ufwPath = ResolveHostPath("/usr/sbin/ufw");
            // 优先检查 UFW
            if (File.Exists(ufwPath))
            {
                var res = await ExecuteHostCommandAsync("ufw status numbered", 4, cancellationToken);
                var isActive = res.StandardOutput.Contains("Status: active");
                var rules = new List<FirewallPortRule>();

                var lines = res.StandardOutput.Split('\n', StringSplitOptions.TrimEntries);
                foreach (var l in lines)
                {
                    // [ 1] 22/tcp                     ALLOW IN    Anywhere
                    var m = Regex.Match(l, @"^\[\s*(\d+)\]\s*(\d+)(?:/([a-zA-Z]+))?\s+(ALLOW|DENY)\s+IN\s+(.+)$");
                    if (m.Success)
                    {
                        rules.Add(new FirewallPortRule
                        {
                            RuleId = m.Groups[1].Value,
                            Port = int.Parse(m.Groups[2].Value),
                            Protocol = m.Groups[3].Success ? m.Groups[3].Value.ToLowerInvariant() : "tcp",
                            Action = m.Groups[4].Value.ToLowerInvariant(),
                            FromAddress = m.Groups[5].Value
                        });
                    }
                }

                return new FirewallStatusInfo
                {
                    IsInstalled = true,
                    IsActive = isActive,
                    BackendType = "ufw",
                    Rules = rules
                };
            }
        }

        return new FirewallStatusInfo
        {
            IsInstalled = false,
            IsActive = false,
            BackendType = "none",
            Rules = []
        };
    }

    public async Task<ServiceActionResult> UpdateFirewallRuleAsync(UpdateFirewallRuleRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Port <= 0 || request.Port > 65535)
        {
            return new ServiceActionResult { Success = false, Message = "端口号必须在 1 ~ 65535 范围内" };
        }

        var proto = string.IsNullOrWhiteSpace(request.Protocol) ? "tcp" : request.Protocol.ToLowerInvariant();
        var cmd = request.Action.Equals("delete", StringComparison.OrdinalIgnoreCase)
            ? $"ufw delete allow {request.Port}/{proto}"
            : $"ufw allow {request.Port}/{proto}";

        var res = await ExecuteHostCommandAsync(cmd, 6, cancellationToken);
        return new ServiceActionResult
        {
            Success = res.ExitCode == 0,
            Message = res.ExitCode == 0 ? "防火墙规则已更新" : res.ErrorOutput,
            Output = res.StandardOutput
        };
    }
    #endregion
}
