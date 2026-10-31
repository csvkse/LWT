using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Services.Contracts;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Systemd;

/// <summary>
/// Systemd 单元智能分类器实现（策略模式 + 规则链）。
/// 纯内存微秒级启发式匹配，杜绝外部进程开销。
/// </summary>
public sealed partial class SystemdUnitCategorizer : ISystemdUnitCategorizer
{
    // 应用与中间件高频关键词集合
    private static readonly HashSet<string> AppKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "docker", "podman", "containerd", "dockerd",
        "nginx", "caddy", "apache", "apache2", "httpd", "lighttpd",
        "mysql", "mysqld", "mariadb", "postgres", "postgresql", "redis", "redis-server", "memcached", "mongodb", "mongod",
        "rabbitmq", "kafka", "elasticsearch", "clickhouse",
        "node", "dotnet", "python", "java", "ruby", "golang",
        "easytier", "frp", "frpc", "frps",
        "prometheus", "grafana", "jenkins", "gitlab", "gitea", "vaultwarden",
        "homeassistant", "alist", "qbittorrent", "aria2", "transmission",
        "plex", "emby", "jellyfin", "samba", "smbd", "nfs", "vsftpd", "pure-ftpd",
        "mssql", "wamp", "xampp"
    };

    // 网络与安全高频关键词集合
    private static readonly HashSet<string> NetworkKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssh", "sshd", "ufw", "firewalld", "iptables", "nftables",
        "networkmanager", "systemd-networkd", "systemd-resolved", "resolved",
        "wpa_supplicant", "dnsmasq", "named", "bind9",
        "tailscale", "wireguard", "openvpn", "v2ray", "xray", "clash", "sing-box"
    };

    // 存储与硬件高频关键词集合
    private static readonly HashSet<string> StorageKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "udev", "systemd-udevd", "smartd", "smartmontools", "multipath", "multipathd",
        "lvm", "lvm2", "fstrim", "udisks", "udisks2", "mdadm", "zfs", "btrfs",
        "cryptsetup", "acpid", "thermald", "lm-sensors", "sensors", "hdparm"
    };

    // 系统底层核心高频关键词集合
    private static readonly HashSet<string> SystemCoreKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "dbus", "cron", "crond", "polkit", "accounts-daemon", "rsyslog", "syslog",
        "kerneloops", "apport", "irqbalance", "unattended-upgrades", "upower",
        "snapd", "atd", "auditd", "systemd-logind", "systemd-journald", "systemd-timesyncd"
    };

    [GeneratedRegex(@"^(systemd-|getty@|serial-getty@|emergency|rescue|initrd)", RegexOptions.IgnoreCase)]
    private static partial Regex SystemCorePrefixRegex();

    [GeneratedRegex(@"\b(web|api|app|bot|daemon|worker|server|proxy|agent)\b", RegexOptions.IgnoreCase)]
    private static partial Regex GenericAppSemanticRegex();

    public (string Category, string Scope) Categorize(string unitName, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(unitName))
        {
            return ("other", "system");
        }

        var cleanName = unitName.Trim();
        var scope = cleanName.Contains("@user", StringComparison.OrdinalIgnoreCase) ? "user" : "system";

        // 去掉 .service / .socket / .timer 等后缀获取基础服务标识
        var baseName = cleanName;
        var dotIdx = cleanName.LastIndexOf('.');
        if (dotIdx > 0)
        {
            baseName = cleanName[..dotIdx];
        }

        var desc = description ?? string.Empty;

        // 策略 1: 判定是否为已知常见应用 (App / MiddleWare / DB / User Service)
        if (MatchKeywords(baseName, desc, AppKeywords))
        {
            return ("app", scope);
        }

        // 策略 2: 判定是否为网络与安全服务 (Network & Security)
        if (MatchKeywords(baseName, desc, NetworkKeywords))
        {
            return ("network", scope);
        }

        // 策略 3: 判定是否为存储与硬件设备服务 (Storage & Hardware)
        if (MatchKeywords(baseName, desc, StorageKeywords))
        {
            return ("storage", scope);
        }

        // 策略 4: 判定是否为系统核心服务 (System Core)
        if (SystemCorePrefixRegex().IsMatch(cleanName) || MatchKeywords(baseName, desc, SystemCoreKeywords))
        {
            return ("system", scope);
        }

        // 策略 5: 语义启发式匹配（包含 api, app, worker, bot, proxy, web 等）
        if (GenericAppSemanticRegex().IsMatch(baseName) || GenericAppSemanticRegex().IsMatch(desc))
        {
            return ("app", scope);
        }

        // 策略 6: 默认归为其它 (other) 或普通系统服务
        return ("other", scope);
    }

    private static bool MatchKeywords(string baseName, string desc, HashSet<string> keywords)
    {
        // 1. 完全或前缀匹配 baseName，如 "docker" 或 "docker-ce" 或 "nginx-custom"
        foreach (var kw in keywords)
        {
            if (baseName.Equals(kw, StringComparison.OrdinalIgnoreCase)) return true;
            if (baseName.StartsWith(kw + "-", StringComparison.OrdinalIgnoreCase)) return true;
            if (baseName.StartsWith(kw + "_", StringComparison.OrdinalIgnoreCase)) return true;
            if (baseName.StartsWith(kw + "@", StringComparison.OrdinalIgnoreCase)) return true;
        }

        // 2. 描述中词边界匹配
        if (!string.IsNullOrEmpty(desc))
        {
            foreach (var kw in keywords)
            {
                if (desc.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
