using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Config;

/// <summary>
/// Samba (SMB) 共享文件夹深度 CRUD、高危路径安全防护守卫、VFS 回收站编排、磁盘容量探测与在线会话审计分部实现。
/// </summary>
public sealed partial class SystemServiceConfigAdapter
{
    /// <summary>
    /// 校验共享物理路径安全性，拦截系统根目录与高危核心系统目录。
    /// </summary>
    public static (bool IsSafe, string? ErrorMessage) ValidateSharePathSafety(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, "物理挂载路径不能为空");
        }

        var normalized = path.Trim().Replace('\\', '/');
        if (!normalized.StartsWith('/'))
        {
            return (false, "物理挂载路径必须为以 '/' 开头的绝对路径");
        }

        // 去除末尾冗余的连续斜杠
        while (normalized.Length > 1 && normalized.EndsWith('/'))
        {
            normalized = normalized[..^1];
        }

        // 1. 严禁直接共享根目录
        if (normalized == "/")
        {
            return (false, "严禁将宿主机根目录 [/] 直接作为网络共享暴露");
        }

        // 2. 高危系统目录黑名单拦截
        string[] criticalRoots = ["/etc", "/boot", "/sys", "/proc", "/dev", "/bin", "/sbin", "/lib", "/lib64", "/root", "/var/log"];
        foreach (var critical in criticalRoots)
        {
            if (normalized.Equals(critical, StringComparison.OrdinalIgnoreCase) ||
                normalized.StartsWith(critical + "/", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"共享路径 [{path}] 属于宿主机高危系统目录，已被安全策略拦截");
            }
        }

        return (true, null);
    }

    public async Task<SambaShareItem?> GetSambaShareAsync(string shareName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shareName)) return null;
        var cfg = await GetSambaConfigAsync(cancellationToken);
        return cfg.Shares.FirstOrDefault(s => s.Name.Equals(shareName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ServiceActionResult> CreateSambaShareAsync(SaveSingleSambaShareRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || !Regex.IsMatch(request.Name.Trim(), @"^[a-zA-Z0-9_\-\$]+$"))
        {
            return new ServiceActionResult { Success = false, Message = "共享名称格式不合法（仅允许 1~32 位字母、数字、下划线、横杠及 $）" };
        }

        var (isSafe, errMsg) = ValidateSharePathSafety(request.Path);
        if (!isSafe)
        {
            return new ServiceActionResult { Success = false, Message = errMsg };
        }

        var currentConfig = await GetSambaConfigAsync(cancellationToken);
        if (currentConfig.Shares.Any(s => s.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return new ServiceActionResult { Success = false, Message = $"已存在同名共享 [{request.Name.Trim()}]" };
        }

        var newShare = new SambaShareItem
        {
            Name = request.Name.Trim(),
            Path = request.Path.Trim(),
            ReadOnly = request.ReadOnly,
            GuestOk = request.GuestOk,
            Browseable = request.Browseable,
            ValidUsers = request.ValidUsers?.Trim(),
            WriteList = request.WriteList?.Trim(),
            ReadList = request.ReadList?.Trim(),
            EnableRecycleBin = request.EnableRecycleBin,
            EnableAppleFruit = request.EnableAppleFruit,
            ForceUser = request.ForceUser?.Trim(),
            Comment = request.Comment?.Trim()
        };

        var updatedShares = currentConfig.Shares.Concat([newShare]).ToList();
        return await SaveSambaConfigAsync(new SaveSambaConfigRequest
        {
            Workgroup = currentConfig.Workgroup,
            Shares = updatedShares
        }, cancellationToken);
    }

    public async Task<ServiceActionResult> UpdateSambaShareAsync(string shareName, SaveSingleSambaShareRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shareName))
        {
            return new ServiceActionResult { Success = false, Message = "共享名称不能为空" };
        }

        var (isSafe, errMsg) = ValidateSharePathSafety(request.Path);
        if (!isSafe)
        {
            return new ServiceActionResult { Success = false, Message = errMsg };
        }

        var currentConfig = await GetSambaConfigAsync(cancellationToken);
        var existingIndex = -1;
        for (var i = 0; i < currentConfig.Shares.Count; i++)
        {
            if (currentConfig.Shares[i].Name.Equals(shareName.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                existingIndex = i;
                break;
            }
        }

        if (existingIndex < 0)
        {
            return new ServiceActionResult { Success = false, Message = $"未找到共享 [{shareName.Trim()}]" };
        }

        var targetName = string.IsNullOrWhiteSpace(request.Name) ? shareName.Trim() : request.Name.Trim();
        if (!targetName.Equals(shareName.Trim(), StringComparison.OrdinalIgnoreCase) &&
            currentConfig.Shares.Any(s => s.Name.Equals(targetName, StringComparison.OrdinalIgnoreCase)))
        {
            return new ServiceActionResult { Success = false, Message = $"已存在同名共享 [{targetName}]，无法重命名" };
        }

        var updatedShare = new SambaShareItem
        {
            Name = targetName,
            Path = request.Path.Trim(),
            ReadOnly = request.ReadOnly,
            GuestOk = request.GuestOk,
            Browseable = request.Browseable,
            ValidUsers = request.ValidUsers?.Trim(),
            WriteList = request.WriteList?.Trim(),
            ReadList = request.ReadList?.Trim(),
            EnableRecycleBin = request.EnableRecycleBin,
            EnableAppleFruit = request.EnableAppleFruit,
            ForceUser = request.ForceUser?.Trim(),
            Comment = request.Comment?.Trim()
        };

        var updatedShares = currentConfig.Shares.ToList();
        updatedShares[existingIndex] = updatedShare;

        return await SaveSambaConfigAsync(new SaveSambaConfigRequest
        {
            Workgroup = currentConfig.Workgroup,
            Shares = updatedShares
        }, cancellationToken);
    }

    public async Task<ServiceActionResult> DeleteSambaShareAsync(string shareName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(shareName))
        {
            return new ServiceActionResult { Success = false, Message = "共享名称不能为空" };
        }

        var currentConfig = await GetSambaConfigAsync(cancellationToken);
        var remainingShares = currentConfig.Shares
            .Where(s => !s.Name.Equals(shareName.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (remainingShares.Count == currentConfig.Shares.Count)
        {
            return new ServiceActionResult { Success = false, Message = $"未找到共享 [{shareName.Trim()}]" };
        }

        var res = await SaveSambaConfigAsync(new SaveSambaConfigRequest
        {
            Workgroup = currentConfig.Workgroup,
            Shares = remainingShares
        }, cancellationToken);

        if (res.Success)
        {
            res = new ServiceActionResult { Success = true, Message = $"共享 [{shareName.Trim()}] 已成功删除并重载服务" };
        }
        return res;
    }

    public async Task<IReadOnlyList<SambaSessionItem>> GetSambaSessionsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return [];

        try
        {
            // 单次调用获取 smbstatus -b 与 smbstatus -S
            var res = await ExecuteHostCommandAsync("smbstatus -b 2>/dev/null; echo '---SHARES_DELIMITER---'; smbstatus -S 2>/dev/null", 4, cancellationToken);
            if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput))
            {
                return [];
            }

            var parts = res.StandardOutput.Split("---SHARES_DELIMITER---", 2, StringSplitOptions.TrimEntries);
            var briefPart = parts.Length > 0 ? parts[0] : "";
            var sharePart = parts.Length > 1 ? parts[1] : "";

            // 解析 sharePart 映射: pid -> (ShareName, ConnectedAt)
            var shareMap = new Dictionary<int, (string ShareName, string ConnectedAt)>();
            if (!string.IsNullOrWhiteSpace(sharePart))
            {
                var sLines = sharePart.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var line in sLines)
                {
                    if (line.StartsWith("Service", StringComparison.OrdinalIgnoreCase) || line.StartsWith('-') || line.StartsWith("Locked", StringComparison.OrdinalIgnoreCase)) continue;
                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 4 && int.TryParse(tokens[1], out var pId))
                    {
                        var sName = tokens[0];
                        var connAt = string.Join(' ', tokens.Skip(3));
                        shareMap[pId] = (sName, connAt);
                    }
                }
            }

            // 解析 briefPart: PID, Username, Group, Machine, Protocol Version
            var sessions = new List<SambaSessionItem>();
            if (!string.IsNullOrWhiteSpace(briefPart))
            {
                var bLines = briefPart.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                foreach (var line in bLines)
                {
                    if (line.StartsWith("Samba", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith("PID", StringComparison.OrdinalIgnoreCase) ||
                        line.StartsWith('-')) continue;

                    var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length >= 4 && int.TryParse(tokens[0], out var pid))
                    {
                        var user = tokens[1];
                        var machine = tokens[3];
                        var proto = tokens.Length > 4 ? tokens[^1] : "SMB";

                        shareMap.TryGetValue(pid, out var shInfo);

                        sessions.Add(new SambaSessionItem
                        {
                            Pid = pid,
                            Username = user,
                            Machine = machine,
                            ClientIp = machine.Split('(')[0].Trim(),
                            ProtocolVersion = proto,
                            ShareName = shInfo.ShareName ?? "IPC$",
                            ConnectedAt = shInfo.ConnectedAt
                        });
                    }
                }
            }

            return sessions;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to inspect Samba sessions via smbstatus");
            return [];
        }
    }

    private async Task ProbeShareDisksAsync(List<SambaShareItem> shares, CancellationToken cancellationToken)
    {
        for (var i = 0; i < shares.Count; i++)
        {
            var s = shares[i];
            if (string.IsNullOrWhiteSpace(s.Path)) continue;

            var physicalDir = ResolveHostPath(s.Path);
            var exists = Directory.Exists(physicalDir);
            long? freeBytes = null;
            long? totalBytes = null;

            if (exists && OperatingSystem.IsLinux())
            {
                var dfRes = await ExecuteHostCommandAsync($"df -kP '{s.Path}' 2>/dev/null", 3, cancellationToken);
                if (dfRes.ExitCode == 0)
                {
                    var dfLines = dfRes.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (dfLines.Length > 1)
                    {
                        var tokens = dfLines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length >= 4 && long.TryParse(tokens[1], out var totalK) && long.TryParse(tokens[3], out var availK))
                        {
                            totalBytes = totalK * 1024;
                            freeBytes = availK * 1024;
                        }
                    }
                }
            }

            shares[i] = s with
            {
                DirectoryExists = exists,
                FreeSpaceBytes = freeBytes,
                TotalSpaceBytes = totalBytes
            };
        }
    }
}
