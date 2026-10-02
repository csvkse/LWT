using System.Diagnostics;
using System.Net.Sockets;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Support;
using LinuxWebTool.Infrastructure.SystemInfo;

namespace LinuxWebTool.Infrastructure.Mount;

public sealed record SmbMountIdentity(string MountId, string Source, string FileSystem, string Path);

/// <summary>
/// SMB(CIFS) 挂载执行器：mount -t cifs + credentials 凭据文件（密码不进命令行，避免 /proc 泄露）。
/// 仅 Linux 可用；Docker 部署需要 --privileged（或 --cap-add SYS_ADMIN）+ --user root，且镜像内含 cifs-utils。
/// 挂载点同时注册到 SystemStatusProvider.ManagedMountPoints，使状态页磁盘明细展示 SMB 挂载。
/// </summary>
public sealed class SmbMountService(DataPaths dataPaths, ILogger<SmbMountService> logger) : ISmbMountOperations
{
    private const int ProcessTimeoutMs = 30_000;

    /// <summary>当前环境是否支持（Linux；Windows 开发机返回 Unsupported 状态）。</summary>
    public static bool IsSupported => OperatingSystem.IsLinux();
    public static bool HasMountDependencies => IsSupported && new[] { "/sbin/mount.cifs", "/usr/sbin/mount.cifs", "/bin/mount.cifs", "/usr/bin/mount.cifs" }.Any(File.Exists);

    /// <summary>凭据文件目录：&lt;data&gt;/mount-creds。</summary>
    private string CredsDirectory => dataPaths.Resolve("mount-creds");

    // ---------- 挂载 / 卸载 ----------

    /// <summary>挂载。返回 (成功, 提示消息)。</summary>
    public Task<(bool Success, string Message)> MountAsync(SmbMount mount) => MountAsync(mount, CancellationToken.None);

    public async Task<MountOperationResult> MountResultAsync(SmbMount mount, CancellationToken cancellationToken)
    {
        var result = await MountAsync(mount, cancellationToken);
        // mount.cifs exposes errno in its diagnostic. Classification stays at this process boundary.
        var kind = result.Message.Contains("mount error(13)", StringComparison.Ordinal)
            ? Contracts.Models.MountFailureKind.AuthenticationFailed
            : result.Message.Contains("超时", StringComparison.Ordinal) ? Contracts.Models.MountFailureKind.Timeout : Contracts.Models.MountFailureKind.Failed;
        return MountOperationResult.From(result, kind);
    }

    public async Task<(bool Success, string Message)> MountAsync(SmbMount mount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported)
        {
            return (false, "当前系统不支持 SMB 挂载管理（仅 Linux；Windows 开发机不可用）");
        }

        var status = GetStatus(mount);
        if (status == SmbMountStatus.Mounted) return (true, "已挂载");
        if (status == SmbMountStatus.Abnormal) return (false, "挂载身份冲突，拒绝覆盖挂载");
        // 挂载点必须存在（无权限创建时给出明确指引）
        try
        {
            var created = await SmbMountRuntimeProbe.RunFsProbeAsync(["mkdir", "-p", "--", mount.LocalPath],
                TimeSpan.FromSeconds(5), "创建挂载目录超时", cancellationToken);
            if (!created.Success) return (false, created.Error ?? "无法创建挂载目录");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (false, $"挂载点目录不可创建（{mount.LocalPath}）：{ex.Message}。请先以有权限的用户创建该目录，或以 root 运行应用");
        }

        // TCP 445 连通性预检，失败时给出可读的错误而不是 mount 的原始报错
        var host = ExtractHost(mount.Server);
        var portOption = (mount.Options ?? "").Split(',').FirstOrDefault(o => o.Trim().StartsWith("port=", StringComparison.Ordinal));
        var port = portOption is not null && int.TryParse(portOption.Trim()[5..], out var configuredPort) ? configuredPort : 445;
        if (!await IsPortOpenAsync(host, port, cancellationToken))
        {
            return (false, $"服务器 {host}:{port} 不可达：请检查主机地址、防火墙与 NAS 的 SMB 服务是否开启");
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
        var (exitCode, _, stderr) = await RunAsync("mount", args, cancellationToken, mount.LocalPath);
        var actual = GetStatus(mount);
        logger.LogInformation("SMB mount reconciled MountId={MountId} Path={Path} CommandExit={Exit} Actual={Actual} Identity={Identity}",
            mount.Id, mount.LocalPath, exitCode, actual, ReadIdentity(mount.LocalPath));
        if (exitCode != 0)
        {
            if (actual == SmbMountStatus.Mounted && !MountProbeProcessGuard.IsBlocked(mount.LocalPath))
                return (true, "命令未报告成功，但实际挂载已建立，等待访问验证");
            var reason = FirstLine(stderr);
            return (false, $"挂载失败（exit {exitCode}）：{reason}");
        }

        if (GetStatus(mount) != SmbMountStatus.Mounted)
        {
            return (false, "mount 命令成功但未在 /proc/self/mounts 中发现 cifs 挂载，请检查挂载点");
        }

        SystemStatusProvider.ManagedMountPoints[NormalizePath(mount.LocalPath)] = 0;
        logger.LogInformation("SMB 挂载成功：{Server} → {LocalPath}", mount.Server, mount.LocalPath);
        return (true, "挂载成功");
    }

    public async Task<MountOperationResult> UnmountResultAsync(SmbMount mount, bool lazy, CancellationToken ct)
    {
        var result = await UnmountAsync(mount, lazy, ct);
        var kind = result.Message.Contains("超时", StringComparison.Ordinal) ? Contracts.Models.MountFailureKind.Timeout
            : result.Message.Contains("身份", StringComparison.Ordinal) ? Contracts.Models.MountFailureKind.Conflict
            : result.Message.Contains("busy", StringComparison.OrdinalIgnoreCase) ? Contracts.Models.MountFailureKind.Busy
            : Contracts.Models.MountFailureKind.Failed;
        return MountOperationResult.From(result, kind);
    }

    /// <summary>卸载。busy 时可用 lazy（umount -l）。</summary>
    public Task<(bool Success, string Message)> UnmountAsync(SmbMount mount, bool lazy) => UnmountAsync(mount, lazy, CancellationToken.None);

    public async Task<(bool Success, string Message)> UnmountAsync(SmbMount mount, bool lazy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSupported)
        {
            return (false, "当前系统不支持 SMB 挂载管理（仅 Linux）");
        }

        var status = GetStatus(mount);
        if (status == SmbMountStatus.Abnormal) return (false, "挂载身份冲突，拒绝卸载");
        if (status == SmbMountStatus.NotMounted)
        {
            SystemStatusProvider.ManagedMountPoints.TryRemove(NormalizePath(mount.LocalPath), out _);
            return (true, "挂载点当前未挂载");
        }

        var args = lazy ? new List<string> { "-l", mount.LocalPath } : new List<string> { mount.LocalPath };
        var before = ReadIdentity(mount.LocalPath);
        var (exitCode, _, stderr) = await RunAsync("umount", args, cancellationToken, mount.LocalPath);
        var after = ReadIdentity(mount.LocalPath);
        if (exitCode == -1 && stderr == "命令执行超时" && after == before)
        {
            foreach (var delay in new[] { 1000, 2000 })
            {
                await Task.Delay(delay, cancellationToken);
                after = ReadIdentity(mount.LocalPath);
                if (after != before) break;
            }
        }
        var alive = MountProbeProcessGuard.IsBlocked(mount.LocalPath);
        logger.LogInformation("SMB unmount reconciled MountId={MountId} Path={Path} Lazy={Lazy} CommandExit={Exit} Before={Before} After={After} StillAlive={StillAlive}",
            mount.Id, mount.LocalPath, lazy, exitCode, before, after, alive);
        if (after is null && !alive)
        {
            SystemStatusProvider.ManagedMountPoints.TryRemove(NormalizePath(mount.LocalPath), out _);
            return (true, exitCode == 0 ? "已卸载" : "命令未报告成功，但实际挂载已解除");
        }
        if (after != before && after is not null) return (false, "卸载后挂载身份变化，停止操作并等待检查");
        if (alive) return (false, "卸载命令超时，旧进程尚未退出；暂停后续操作");
        if (exitCode != 0)
        {
            var reason = FirstLine(stderr);
            return (false, $"卸载失败（exit {exitCode}）：{reason}" + (!lazy && reason.Contains("busy", StringComparison.OrdinalIgnoreCase) ? "。可尝试懒卸载" : ""));
        }

        if (after is not null) return (false, "卸载命令成功但挂载记录仍存在");

        SystemStatusProvider.ManagedMountPoints.TryRemove(NormalizePath(mount.LocalPath), out _);
        logger.LogInformation("SMB 卸载：{LocalPath}", mount.LocalPath);
        return (true, "已卸载");
    }

    // ---------- 状态探测 ----------

    public static SmbMountIdentity? ReadIdentity(string path)
    {
        if (!OperatingSystem.IsLinux()) return null;
        var entry = LinuxMountInfoParser.Parse(File.ReadAllText("/proc/self/mountinfo"), includeVirtual: true)
            .LastOrDefault(e => NormalizePath(e.MountPoint) == NormalizePath(path));
        return entry is null ? null : new(entry.MountId, entry.Source, entry.FileSystem, entry.MountPoint);
    }

    public static bool Matches(SmbMountIdentity? identity, string server) => identity is { FileSystem: "cifs" }
        && string.Equals(identity.Source.Replace('\\', '/').TrimEnd('/'), server.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>实时状态：解析 /proc/self/mounts，无需启动子进程。</summary>
    public SmbMountStatus GetStatus(SmbMount mount)
    {
        if (!IsSupported)
        {
            return SmbMountStatus.Unsupported;
        }

        try
        {
            var entry = LinuxMountInfoParser.Parse(File.ReadAllText("/proc/self/mountinfo"), includeVirtual: true)
                .LastOrDefault(e => NormalizePath(e.MountPoint) == NormalizePath(mount.LocalPath));
            if (entry is null) return SmbMountStatus.NotMounted;
            return entry.FileSystem == "cifs" && string.Equals(entry.Source.Replace('\\', '/').TrimEnd('/'),
                mount.Server.Replace('\\', '/').TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                ? SmbMountStatus.Mounted : SmbMountStatus.Abnormal;
        }
        catch { return SmbMountStatus.Abnormal; }
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

    private static async Task<bool> IsPortOpenAsync(string host, int port, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new TcpClient();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(3000);
            await client.ConnectAsync(host, port, budget.Token);
            return client.Connected;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string path) => MountOperationCoordinator.NormalizePath(path);

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? "未知错误" : line;
    }

    /// <summary>挂载命令执行（mount/umount），ArgumentList 传参避免注入与转义问题。</summary>
    private async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(string fileName, List<string> args, CancellationToken cancellationToken, string path)
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
            process.StartInfo.Environment["LC_ALL"] = "C";
            process.Start();
            var elapsed = Stopwatch.StartNew();
            logger.LogInformation("SMB command start Command={Command} Path={Path} PID={Pid}", fileName, path, process.Id);
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(ProcessTimeoutMs);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                MountProbeProcessGuard.RecordIfAlive(path, process);
                logger.LogWarning("SMB command timeout Command={Command} Path={Path} PID={Pid} ElapsedMs={Elapsed} StillAlive={Alive} ProcessState={ProcessState}", fileName, path, process.Id, elapsed.ElapsedMilliseconds, MountProbeProcessGuard.IsBlocked(path), MountProbeProcessGuard.ReadState(process.Id));
                cancellationToken.ThrowIfCancellationRequested();
                return (-1, string.Empty, "命令执行超时");
            }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            logger.LogInformation("SMB command end Command={Command} Path={Path} PID={Pid} Exit={Exit} ElapsedMs={Elapsed}", fileName, path, process.Id, process.ExitCode, elapsed.ElapsedMilliseconds);
            return (process.HasExited ? process.ExitCode : -1, stdout, stderr);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return (-1, string.Empty, ex.Message);
        }
    }
}
