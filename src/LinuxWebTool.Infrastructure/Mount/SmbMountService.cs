using System.Diagnostics;
using System.Net.Sockets;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Support;
using LinuxWebTool.Infrastructure.SystemInfo;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>
/// SMB(CIFS) 挂载执行器：mount -t cifs + credentials 凭据文件（密码不进命令行，避免 /proc 泄露）。
/// 仅 Linux 可用；Docker 部署需要 --privileged（或 --cap-add SYS_ADMIN）+ --user root，且镜像内含 cifs-utils。
/// 挂载点同时注册到 SystemStatusProvider.ManagedMountPoints，使状态页磁盘明细展示 SMB 挂载。
/// </summary>
public sealed class SmbMountService(DataPaths dataPaths, ILogger<SmbMountService> logger)
{
    private const int ProcessTimeoutMs = 30_000;

    /// <summary>当前环境是否支持（Linux；Windows 开发机返回 Unsupported 状态）。</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>凭据文件目录：&lt;data&gt;/mount-creds。</summary>
    private string CredsDirectory => dataPaths.Resolve("mount-creds");

    // ---------- 挂载 / 卸载 ----------

    /// <summary>挂载。返回 (成功, 提示消息)。</summary>
    public async Task<(bool Success, string Message)> MountAsync(SmbMount mount)
    {
        if (!IsSupported)
        {
            return (false, "当前系统不支持 SMB 挂载管理（仅 Linux；Windows 开发机不可用）");
        }

        // 挂载点必须存在（无权限创建时给出明确指引）
        try
        {
            Directory.CreateDirectory(mount.LocalPath);
        }
        catch (Exception ex)
        {
            return (false, $"挂载点目录不可创建（{mount.LocalPath}）：{ex.Message}。请先以有权限的用户创建该目录，或以 root 运行应用");
        }

        // TCP 445 连通性预检，失败时给出可读的错误而不是 mount 的原始报错
        var host = ExtractHost(mount.Server);
        if (!await IsPortOpenAsync(host, 445))
        {
            return (false, $"服务器 {host}:445 不可达：请检查主机地址、防火墙与 NAS 的 SMB 服务是否开启");
        }

        EnsureCredentialFile(mount);

        var options = BuildOptions(mount);
        var args = new List<string> { "-t", "cifs", mount.Server, mount.LocalPath };
        if (options.Length > 0)
        {
            args.Add("-o");
            args.Add(options);
        }

        logger.LogInformation("SMB 挂载：{Server} → {LocalPath}", mount.Server, mount.LocalPath);
        var (exitCode, _, stderr) = await RunAsync("mount", args);
        if (exitCode != 0)
        {
            var reason = FirstLine(stderr);
            return (false, $"挂载失败（exit {exitCode}）：{reason}");
        }

        if (GetMountedFsType(mount.LocalPath) != "cifs")
        {
            return (false, "mount 命令成功但未在 /proc/self/mounts 中发现 cifs 挂载，请检查挂载点");
        }

        SystemStatusProvider.ManagedMountPoints[NormalizePath(mount.LocalPath)] = 0;
        logger.LogInformation("SMB 挂载成功：{Server} → {LocalPath}", mount.Server, mount.LocalPath);
        return (true, "挂载成功");
    }

    /// <summary>卸载。busy 时可用 lazy（umount -l）。</summary>
    public async Task<(bool Success, string Message)> UnmountAsync(SmbMount mount, bool lazy)
    {
        if (!IsSupported)
        {
            return (false, "当前系统不支持 SMB 挂载管理（仅 Linux）");
        }

        if (GetMountedFsType(mount.LocalPath) is null)
        {
            SystemStatusProvider.ManagedMountPoints.TryRemove(NormalizePath(mount.LocalPath), out _);
            return (true, "挂载点当前未挂载");
        }

        var args = lazy ? new List<string> { "-l", mount.LocalPath } : new List<string> { mount.LocalPath };
        var (exitCode, _, stderr) = await RunAsync("umount", args);
        if (exitCode != 0)
        {
            var reason = FirstLine(stderr);
            return (false, $"卸载失败（exit {exitCode}）：{reason}。目录可能正被占用，可尝试懒卸载");
        }

        SystemStatusProvider.ManagedMountPoints.TryRemove(NormalizePath(mount.LocalPath), out _);
        logger.LogInformation("SMB 卸载：{LocalPath}", mount.LocalPath);
        return (true, "已卸载");
    }

    // ---------- 状态探测 ----------

    /// <summary>实时状态：解析 /proc/self/mounts，无需启动子进程。</summary>
    public SmbMountStatus GetStatus(SmbMount mount)
    {
        if (!IsSupported)
        {
            return SmbMountStatus.Unsupported;
        }

        var fsType = GetMountedFsType(mount.LocalPath);
        return fsType switch
        {
            "cifs" => SmbMountStatus.Mounted,
            null => SmbMountStatus.NotMounted,
            _ => SmbMountStatus.Abnormal,
        };
    }

    /// <summary>挂载点被占用的文件系统名；未挂载返回 null。</summary>
    private static string? GetMountedFsType(string localPath)
    {
        var target = NormalizePath(localPath);
        if (target.Length == 0)
        {
            return null;
        }

        string content;
        try
        {
            content = File.ReadAllText("/proc/self/mounts");
        }
        catch
        {
            return null;
        }

        foreach (var line in content.Split('\n'))
        {
            var parts = line.Split(' ');
            if (parts.Length < 3)
            {
                continue;
            }

            // /proc/self/mounts 对空格等字符使用八进制转义（\040 空格 \011 制表 \134 反斜杠）
            if (UnescapeMounts(parts[1]) == target)
            {
                return parts[2];
            }
        }
        return null;
    }

    private static string UnescapeMounts(string value)
    {
        if (!value.Contains('\\'))
        {
            return value;
        }
        var sb = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length
                && value[i + 1] is '0' or '1' or '2' or '3')
            {
                if (TryParseOctal(value[(i + 1)..(i + 4)], out var code))
                {
                    sb.Append((char)code);
                    i += 3;
                    continue;
                }
            }
            sb.Append(value[i]);
        }
        return sb.ToString();
    }

    private static bool TryParseOctal(string text, out int code)
    {
        code = 0;
        foreach (var ch in text)
        {
            if (ch is < '0' or > '7')
            {
                return false;
            }
            code = code * 8 + (ch - '0');
        }
        return true;
    }

    // ---------- 凭据文件 ----------

    /// <summary>写入 credentials 文件（600 权限），确保挂载 / 启动重挂前凭据就绪。</summary>
    public void EnsureCredentialFile(SmbMount mount)
    {
        var path = CredentialFilePath(mount.Id);
        Directory.CreateDirectory(CredsDirectory);
        if (string.IsNullOrEmpty(mount.Username) && string.IsNullOrEmpty(mount.Password))
        {
            TryDelete(path); // 访客模式：无需凭据文件
            return;
        }

        var content = string.Empty;
        if (!string.IsNullOrEmpty(mount.Username))
        {
            content += $"username={mount.Username}\n";
        }
        if (!string.IsNullOrEmpty(mount.Password))
        {
            content += $"password={mount.Password}\n";
        }
        if (!string.IsNullOrEmpty(mount.Domain))
        {
            content += $"domain={mount.Domain}\n";
        }
        File.WriteAllText(path, content);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                File.SetUnixFileMode(path, System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "凭据文件权限设置失败：{Path}", path);
            }
        }
    }

    public void DeleteCredentialFile(Guid mountId) => TryDelete(CredentialFilePath(mountId));

    private string CredentialFilePath(Guid mountId) => Path.Combine(CredsDirectory, mountId.ToString("N"));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    // ---------- 工具 ----------

    /// <summary>组合挂载选项：用户选项优先，缺 iocharset 时补 utf8；凭据走 credentials= 文件。</summary>
    private string BuildOptions(SmbMount mount)
    {
        var parts = new List<string>();
        var userOptions = (mount.Options ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var hasCharset = userOptions.Any(o => o.StartsWith("iocharset=", StringComparison.OrdinalIgnoreCase));

        if (!hasCharset)
        {
            parts.Add("iocharset=utf8");
        }
        parts.AddRange(userOptions);

        if (!string.IsNullOrEmpty(mount.Username) || !string.IsNullOrEmpty(mount.Password))
        {
            parts.Add($"credentials={CredentialFilePath(mount.Id)}");
        }
        return string.Join(",", parts);
    }

    /// <summary>//host/share 或 \\host\share → host（支持 host:port 写法）。</summary>
    private static string ExtractHost(string server)
    {
        var normalized = server.Trim().Replace('\\', '/').TrimStart('/');
        var firstSegment = normalized.Split('/', 2)[0];
        var host = firstSegment;
        var colon = host.LastIndexOf(':');
        if (colon > 0 && int.TryParse(host[(colon + 1)..], out _))
        {
            host = host[..colon];
        }
        return host;
    }

    private static async Task<bool> IsPortOpenAsync(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(3000));
            return completed == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path) => path.Trim().TrimEnd('/');

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? "未知错误" : line;
    }

    /// <summary>挂载命令执行（mount/umount），ArgumentList 传参避免注入与转义问题。</summary>
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string fileName, List<string> args)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }
            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = new CancellationTokenSource(ProcessTimeoutMs);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (-1, string.Empty, "命令执行超时");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return (process.HasExited ? process.ExitCode : -1, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
