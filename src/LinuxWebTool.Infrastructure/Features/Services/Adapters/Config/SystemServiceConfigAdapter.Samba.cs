using System.Text;
using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Infrastructure.Shared.Support;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Config;

/// <summary>
/// Samba (SMB) 服务端配置适配器分部实现：
/// 负责宿主 PID 1 穿透、冷启动默认模板自愈、Samba 访问用户与凭据安全管理及宿主环境自动安装。
/// </summary>
public sealed partial class SystemServiceConfigAdapter
{
    private const string DefaultSambaGlobalTemplate = """
        [global]
           workgroup = WORKGROUP
           server string = LinuxWebTool SMB
           server role = standalone server
           security = user
           map to guest = Bad User
           dns proxy = no
           log file = /var/log/samba/%m.log
           max log size = 50
           load printers = no
           printing = bsd
           printcap name = /dev/null
           disable spoolss = yes
           create mask = 0775
           directory mask = 0775
        """;

    #region Samba 服务端配置（宿主穿透、冷启动自愈与凭据治理）
    public async Task<SambaServiceConfig> GetSambaConfigAsync(CancellationToken cancellationToken = default)
    {
        var configPath = ResolveHostPath("/etc/samba/smb.conf");
        var isPierced = HostPathResolver.CanPierceHost();
        var isInstalled = false;
        var isRunning = false;
        var workgroup = "WORKGROUP";
        var shares = new List<SambaShareItem>();
        var users = new List<SambaUserItem>();

        if (OperatingSystem.IsLinux())
        {
            // 检查宿主是否安装 Samba（测试二进制 smbd 或 pdbedit 是否存在）
            var binRes = await ExecuteHostCommandAsync("which smbd || which samba", 3, cancellationToken);
            isInstalled = binRes.ExitCode == 0 || File.Exists(configPath) || File.Exists(ResolveHostPath("/usr/sbin/smbd"));

            var statRes = await ExecuteHostCommandAsync("systemctl is-active smbd || systemctl is-active smb", 3, cancellationToken);
            isRunning = statRes.StandardOutput.Trim() == "active";

            if (File.Exists(configPath))
            {
                try
                {
                    var lines = await File.ReadAllLinesAsync(configPath, cancellationToken);
                    string? currentSection = null;
                    var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    void FlushSection()
                    {
                        if (currentSection != null && !currentSection.Equals("global", StringComparison.OrdinalIgnoreCase) && !currentSection.Equals("homes", StringComparison.OrdinalIgnoreCase) && !currentSection.Equals("printers", StringComparison.OrdinalIgnoreCase))
                        {
                            var vfs = props.GetValueOrDefault("vfs objects", "");
                            var isRecycle = vfs.Contains("recycle", StringComparison.OrdinalIgnoreCase);
                            var isFruit = vfs.Contains("fruit", StringComparison.OrdinalIgnoreCase);
                            var browseable = !props.GetValueOrDefault("browseable", "yes").Equals("no", StringComparison.OrdinalIgnoreCase);

                            shares.Add(new SambaShareItem
                            {
                                Name = currentSection,
                                Path = props.GetValueOrDefault("path", ""),
                                ReadOnly = props.GetValueOrDefault("read only", "yes").Equals("yes", StringComparison.OrdinalIgnoreCase),
                                GuestOk = props.GetValueOrDefault("guest ok", "no").Equals("yes", StringComparison.OrdinalIgnoreCase),
                                Browseable = browseable,
                                ValidUsers = props.GetValueOrDefault("valid users"),
                                WriteList = props.GetValueOrDefault("write list"),
                                ReadList = props.GetValueOrDefault("read list"),
                                EnableRecycleBin = isRecycle,
                                EnableAppleFruit = isFruit,
                                ForceUser = props.GetValueOrDefault("force user"),
                                Comment = props.GetValueOrDefault("comment")
                            });
                        }
                        props.Clear();
                    }

                    foreach (var rawLine in lines)
                    {
                        var line = rawLine.Trim();
                        if (line.StartsWith('#') || line.StartsWith(';') || string.IsNullOrWhiteSpace(line)) continue;

                        if (line.StartsWith('[') && line.EndsWith(']'))
                        {
                            FlushSection();
                            currentSection = line[1..^1].Trim();
                            continue;
                        }

                        var parts = line.Split('=', 2);
                        if (parts.Length == 2)
                        {
                            var k = parts[0].Trim();
                            var v = parts[1].Trim();
                            props[k] = v;
                            if (currentSection != null && currentSection.Equals("global", StringComparison.OrdinalIgnoreCase) && k.Equals("workgroup", StringComparison.OrdinalIgnoreCase))
                            {
                                workgroup = v;
                            }
                        }
                    }
                    FlushSection();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to parse smb.conf");
                }
            }

            // 探测共享物理目录存在性与宿主磁盘容量
            await ProbeShareDisksAsync(shares, cancellationToken);

            // 获取 Samba 用户列表
            users = (await ListSambaUsersAsync(cancellationToken)).ToList();
        }

        var sessions = await GetSambaSessionsAsync(cancellationToken);

        return new SambaServiceConfig
        {
            IsInstalled = isInstalled,
            IsRunning = isRunning,
            IsHostPierced = isPierced,
            Workgroup = workgroup,
            ConfigFilePath = "/etc/samba/smb.conf",
            Shares = shares,
            Users = users,
            Sessions = sessions
        };
    }

    public Task<ServiceActionResult> UpdateSambaSharesAsync(IReadOnlyList<SambaShareItem> shares, CancellationToken cancellationToken = default)
    {
        return SaveSambaConfigAsync(new SaveSambaConfigRequest { Shares = shares }, cancellationToken);
    }

    public async Task<ServiceActionResult> SaveSambaConfigAsync(SaveSambaConfigRequest request, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return new ServiceActionResult { Success = false, Message = "Windows 环境下不支持直接修改 Linux smb.conf" };
        }

        var configPath = ResolveHostPath("/etc/samba/smb.conf");
        var parentDir = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
        {
            try { Directory.CreateDirectory(parentDir); } catch { }
        }

        try
        {
            var globalLines = new List<string>();
            var inGlobal = true;

            if (File.Exists(configPath))
            {
                var lines = await File.ReadAllLinesAsync(configPath, cancellationToken);
                foreach (var l in lines)
                {
                    var t = l.Trim();
                    if (t.StartsWith('[') && t.EndsWith(']'))
                    {
                        var sec = t[1..^1].Trim();
                        if (!sec.Equals("global", StringComparison.OrdinalIgnoreCase))
                        {
                            inGlobal = false;
                        }
                    }
                    if (inGlobal)
                    {
                        if (!string.IsNullOrWhiteSpace(request.Workgroup) && t.StartsWith("workgroup", StringComparison.OrdinalIgnoreCase))
                        {
                            globalLines.Add($"   workgroup = {request.Workgroup.Trim()}");
                        }
                        else
                        {
                            globalLines.Add(l);
                        }
                    }
                }
            }

            if (globalLines.Count == 0)
            {
                var initTpl = DefaultSambaGlobalTemplate;
                if (!string.IsNullOrWhiteSpace(request.Workgroup))
                {
                    initTpl = initTpl.Replace("workgroup = WORKGROUP", $"workgroup = {request.Workgroup.Trim()}");
                }
                globalLines.AddRange(initTpl.Split([Environment.NewLine, "\n"], StringSplitOptions.None));
            }

            var sb = new StringBuilder();
            foreach (var gl in globalLines) sb.AppendLine(gl);

            foreach (var share in request.Shares)
            {
                if (string.IsNullOrWhiteSpace(share.Name) || string.IsNullOrWhiteSpace(share.Path)) continue;

                // 路径安全防护守卫：拦截高危系统目录
                var (isSafe, errMsg) = ValidateSharePathSafety(share.Path);
                if (!isSafe)
                {
                    return new ServiceActionResult { Success = false, Message = $"共享 [{share.Name}] 路径不合规: {errMsg}" };
                }

                // 穿透确保宿主物理挂载目录存在并设置安全读写权限 0775
                await ExecuteHostCommandAsync($"mkdir -p '{share.Path.Trim()}' && chmod 0775 '{share.Path.Trim()}' 2>/dev/null || true", 5, cancellationToken);

                sb.AppendLine();
                sb.AppendLine($"[{share.Name.Trim()}]");
                sb.AppendLine($"   path = {share.Path.Trim()}");
                sb.AppendLine($"   read only = {(share.ReadOnly ? "yes" : "no")}");
                sb.AppendLine($"   guest ok = {(share.GuestOk ? "yes" : "no")}");
                sb.AppendLine($"   browseable = {(share.Browseable ? "yes" : "no")}");
                if (!string.IsNullOrWhiteSpace(share.ValidUsers)) sb.AppendLine($"   valid users = {share.ValidUsers.Trim()}");
                if (!string.IsNullOrWhiteSpace(share.WriteList)) sb.AppendLine($"   write list = {share.WriteList.Trim()}");
                if (!string.IsNullOrWhiteSpace(share.ReadList)) sb.AppendLine($"   read list = {share.ReadList.Trim()}");
                if (!string.IsNullOrWhiteSpace(share.ForceUser)) sb.AppendLine($"   force user = {share.ForceUser.Trim()}");
                if (!string.IsNullOrWhiteSpace(share.Comment)) sb.AppendLine($"   comment = {share.Comment.Trim()}");
                sb.AppendLine("   create mask = 0775");
                sb.AppendLine("   directory mask = 0775");

                if (share.EnableRecycleBin || share.EnableAppleFruit)
                {
                    var vfsList = new List<string>();
                    if (share.EnableRecycleBin) vfsList.Add("recycle");
                    if (share.EnableAppleFruit) vfsList.AddRange(["catia", "fruit", "streams_xattr"]);
                    sb.AppendLine($"   vfs objects = {string.Join(' ', vfsList)}");

                    if (share.EnableRecycleBin)
                    {
                        sb.AppendLine("   recycle:repository = .recycle");
                        sb.AppendLine("   recycle:keeptree = yes");
                        sb.AppendLine("   recycle:versions = yes");
                        sb.AppendLine("   recycle:touch = yes");
                        sb.AppendLine("   recycle:maxsize = 0");
                        sb.AppendLine("   recycle:exclude = *.tmp,~$*,Thumbs.db");
                    }
                    if (share.EnableAppleFruit)
                    {
                        sb.AppendLine("   fruit:aapl = yes");
                        sb.AppendLine("   fruit:model = Mac");
                        sb.AppendLine("   fruit:resource = file");
                    }
                }
            }

            var newConfigContent = sb.ToString();

            // 语法检测：先将 candidate 写至宿主 tmp 目录以便宿主 testparm 校验
            var candidateHostVirtual = $"/tmp/smb.conf.candidate.{Guid.NewGuid():N}";
            var candidatePhysical = ResolveHostPath(candidateHostVirtual);
            await File.WriteAllTextAsync(candidatePhysical, newConfigContent, cancellationToken);

            var testRes = await ExecuteHostCommandAsync($"testparm -s '{candidateHostVirtual}' 2>&1", 5, cancellationToken);
            try { File.Delete(candidatePhysical); } catch { }

            if (testRes.ExitCode == 0 || testRes.StandardOutput.Contains("Loaded services file OK") || testRes.ExitCode == 127)
            {
                // 校验通过或宿主尚未安装 testparm (127: command not found)，继续写入
            }
            else
            {
                return new ServiceActionResult { Success = false, Message = $"Samba 语法校验失败: {testRes.StandardOutput}" };
            }

            // 备份原文件并落盘新配置
            if (File.Exists(configPath))
            {
                var backupPath = $"{configPath}.bak.{DateTime.UtcNow:yyyyMMdd_HHmmss}";
                File.Copy(configPath, backupPath, overwrite: true);
            }
            await File.WriteAllTextAsync(configPath, newConfigContent, cancellationToken);

            // 重启宿主 smbd / smb 服务
            await ExecuteHostCommandAsync("systemctl restart smbd || systemctl restart smb", 8, cancellationToken);

            return new ServiceActionResult { Success = true, Message = "Samba 共享配置更新成功并已重载服务" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Samba config");
            return new ServiceActionResult { Success = false, Message = $"更新失败: {ex.Message}" };
        }
    }

    public async Task<IReadOnlyList<SambaUserItem>> ListSambaUsersAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux()) return [];

        var res = await ExecuteHostCommandAsync("pdbedit -L 2>/dev/null", 5, cancellationToken);
        if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput))
        {
            return [];
        }

        var list = new List<SambaUserItem>();
        var lines = res.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var parts = line.Split(':');
            if (parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            {
                list.Add(new SambaUserItem
                {
                    Username = parts[0].Trim(),
                    HasPassword = true,
                    Comment = parts.Length > 2 ? parts[2].Trim() : null
                });
            }
        }
        return list;
    }

    public async Task<ServiceActionResult> CreateSambaUserAsync(CreateSambaUserRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || request.Username.Length > 32 || !Regex.IsMatch(request.Username, @"^[a-zA-Z0-9_\-]+$"))
        {
            return new ServiceActionResult { Success = false, Message = "用户名格式不合法（仅允许 1~32 位字母、数字、下划线及横杠）" };
        }
        if (string.IsNullOrEmpty(request.Password))
        {
            return new ServiceActionResult { Success = false, Message = "密码不能为空" };
        }

        var username = request.Username.Trim();
        var escapedPass = request.Password.Replace("'", "'\\''");

        // 1. 检查宿主系统是否存在该 Linux 用户，若不存在则创建无交互登录权限的系统影子用户
        var chkSys = await ExecuteHostCommandAsync($"id -u '{username}' 2>/dev/null", 3, cancellationToken);
        if (chkSys.ExitCode != 0)
        {
            var addSysRes = await ExecuteHostCommandAsync($"useradd -M -s /usr/sbin/nologin '{username}' 2>/dev/null || useradd -M -s /bin/false '{username}' 2>/dev/null || adduser -D -s /sbin/nologin '{username}' 2>/dev/null", 5, cancellationToken);
            if (addSysRes.ExitCode != 0)
            {
                _logger.LogWarning("Creating system user {User} returned code {Code}: {Err}", username, addSysRes.ExitCode, addSysRes.ErrorOutput);
            }
        }

        // 2. 将用户注册进 Samba passdb 并设置密码
        var smbRes = await ExecuteHostCommandAsync($"printf '%s\n%s\n' '{escapedPass}' '{escapedPass}' | smbpasswd -a -s '{username}' && smbpasswd -e '{username}'", 6, cancellationToken);
        if (smbRes.ExitCode != 0)
        {
            return new ServiceActionResult { Success = false, Message = $"Samba 用户创建失败: {smbRes.ErrorOutput.Trim()}" };
        }

        return new ServiceActionResult { Success = true, Message = $"Samba 用户 [{username}] 创建成功" };
    }

    public async Task<ServiceActionResult> UpdateSambaUserPasswordAsync(UpdateSambaUserPasswordRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            return new ServiceActionResult { Success = false, Message = "用户名不能为空" };
        }
        if (string.IsNullOrEmpty(request.Password))
        {
            return new ServiceActionResult { Success = false, Message = "密码不能为空" };
        }

        var username = request.Username.Trim();
        var escapedPass = request.Password.Replace("'", "'\\''");

        var res = await ExecuteHostCommandAsync($"printf '%s\n%s\n' '{escapedPass}' '{escapedPass}' | smbpasswd -s '{username}'", 6, cancellationToken);
        if (res.ExitCode != 0)
        {
            return new ServiceActionResult { Success = false, Message = $"密码更新失败: {res.ErrorOutput.Trim()}" };
        }

        return new ServiceActionResult { Success = true, Message = $"用户 [{username}] 密码修改成功" };
    }

    public async Task<ServiceActionResult> DeleteSambaUserAsync(string username, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(username) || !Regex.IsMatch(username, @"^[a-zA-Z0-9_\-]+$"))
        {
            return new ServiceActionResult { Success = false, Message = "用户名格式不合法" };
        }

        var u = username.Trim();
        var res = await ExecuteHostCommandAsync($"smbpasswd -x '{u}'", 5, cancellationToken);
        if (res.ExitCode != 0)
        {
            return new ServiceActionResult { Success = false, Message = $"删除失败: {res.ErrorOutput.Trim()}" };
        }

        return new ServiceActionResult { Success = true, Message = $"Samba 用户 [{u}] 已删除" };
    }

    public async Task<ServiceActionResult> InstallSambaOnHostAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsLinux())
        {
            return new ServiceActionResult { Success = false, Message = "Windows 环境下不支持在宿主自动安装 Samba" };
        }

        var env = await _envDetector.DetectAsync(cancellationToken);
        var distro = env.HostDistroKey?.ToLowerInvariant() ?? "";
        string installCmd;

        if (distro.Contains("debian") || distro.Contains("ubuntu"))
        {
            installCmd = "export DEBIAN_FRONTEND=noninteractive; apt-get update -y && apt-get install -y samba smbclient";
        }
        else if (distro.Contains("alpine"))
        {
            installCmd = "apk add --no-cache samba samba-common-tools";
        }
        else if (distro.Contains("rhel") || distro.Contains("centos") || distro.Contains("fedora"))
        {
            installCmd = "dnf install -y samba samba-client || yum install -y samba";
        }
        else
        {
            installCmd = "apt-get update -y && apt-get install -y samba || apk add samba || dnf install -y samba";
        }

        _logger.LogInformation("Executing host Samba installation: {Cmd}", installCmd);
        var res = await ExecuteHostCommandAsync(installCmd, 180, cancellationToken);
        if (res.ExitCode != 0)
        {
            return new ServiceActionResult { Success = false, Message = $"安装失败: {res.ErrorOutput}", Output = res.StandardOutput };
        }

        // 初始化默认 smb.conf（若不存在）
        var configPath = ResolveHostPath("/etc/samba/smb.conf");
        if (!File.Exists(configPath))
        {
            var dir = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(configPath, DefaultSambaGlobalTemplate, cancellationToken);
        }

        // 启动并开机自启
        await ExecuteHostCommandAsync("systemctl enable smbd --now || systemctl enable smb --now || systemctl start smbd", 10, cancellationToken);

        return new ServiceActionResult { Success = true, Message = "Samba 服务在宿主机安装成功并已启动" };
    }
    #endregion
}
