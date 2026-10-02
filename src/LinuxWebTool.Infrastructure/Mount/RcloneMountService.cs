using System.Diagnostics;
using System.Text;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Support;
using LinuxWebTool.Infrastructure.SystemInfo;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>SFTP / S3 共用的 rclone FUSE 执行器；每个配置独占凭据文件和写缓存。</summary>
public sealed class RcloneMountService(DataPaths dataPaths, ILogger<RcloneMountService> logger)
{
    private const string ConfigFolder = "rclone-config";
    private const string CacheFolder = "rclone-cache";

    public static bool IsSupported => WebDavMountService.IsSupported;

    public SmbMountStatus GetStatus(RcloneMount mount)
    {
        if (!OperatingSystem.IsLinux()) return SmbMountStatus.Unsupported;
        try
        {
            var path = MountOperationCoordinator.NormalizePath(mount.LocalPath);
            var entry = LinuxMountInfoParser.Parse(File.ReadAllText("/proc/self/mountinfo"), includeVirtual: true)
                .LastOrDefault(m => MountOperationCoordinator.NormalizePath(m.MountPoint) == path);
            if (entry is null) return SmbMountStatus.NotMounted;
            return entry.FileSystem == "fuse.rclone" && entry.Source.Contains(mount.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)
                ? SmbMountStatus.Mounted : SmbMountStatus.Abnormal;
        }
        catch { return SmbMountStatus.Abnormal; }
    }

    public async Task<(bool Success, string Message)> MountAsync(RcloneMount mount, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return (false, "需要 Linux、rclone 和 /dev/fuse；请检查容器设备与权限");
        var status = GetStatus(mount);
        if (status == SmbMountStatus.Mounted) return (true, "已挂载");
        if (status == SmbMountStatus.Abnormal) return (false, "挂载点已被其他文件系统占用");
        try
        {
            var created = await SmbMountRuntimeProbe.RunFsProbeAsync(["mkdir", "-p", "--", mount.LocalPath],
                TimeSpan.FromSeconds(5), "创建挂载目录超时", cancellationToken);
            if (!created.Success) return (false, created.Error ?? "无法创建挂载目录");
            var config = await EnsureConfigAsync(mount, cancellationToken);
            var cache = Path.Combine(dataPaths.Resolve(CacheFolder), mount.Id.ToString("N"));
            Directory.CreateDirectory(cache);
            var result = await RunAsync(["--config", config, "mount", Remote(mount), mount.LocalPath,
                "--daemon", "--daemon-wait", "30s", "--vfs-cache-mode", "writes",
                "--cache-dir", cache, "--vfs-cache-max-size", "1G"], null, TimeSpan.FromSeconds(40), cancellationToken, guardedPath: mount.LocalPath);
            if (result.ExitCode != 0) return (false, $"rclone 挂载失败（exit {result.ExitCode}）：{Redact(result.Stderr, mount)}");
            if (GetStatus(mount) != SmbMountStatus.Mounted)
                return (false, "rclone 返回成功，但未找到匹配的 FUSE 挂载记录");
            SystemStatusProvider.ManagedMountPoints[MountOperationCoordinator.NormalizePath(mount.LocalPath)] = 0;
            logger.LogInformation("{Kind} 挂载成功：{Name} → {Path}", mount.Kind, mount.Name, mount.LocalPath);
            return (true, "挂载成功");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (false, $"挂载失败：{Redact(ex.Message, mount)}"); }
    }

    public async Task<(bool Success, string Message)> UnmountAsync(RcloneMount mount, CancellationToken cancellationToken = default)
    {
        var status = GetStatus(mount);
        if (status == SmbMountStatus.NotMounted) return (true, "挂载点当前未挂载");
        if (status != SmbMountStatus.Mounted) return (false, "挂载点并非当前配置，拒绝卸载");
        var result = await RunProcessAsync("umount", ["--", mount.LocalPath], null, TimeSpan.FromSeconds(40), cancellationToken, guardedPath: mount.LocalPath);
        if (result.ExitCode != 0) return (false, $"卸载失败（exit {result.ExitCode}）：{Redact(result.Stderr, mount)}");
        return (true, "已卸载；待上传缓存保留");
    }

    public async Task<(bool Success, bool CredentialsRejected, string? Error)> ProbeRemoteAsync(
        RcloneMount mount, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return (false, false, "Linux FUSE 挂载能力不可用");
        try
        {
            var config = await EnsureConfigAsync(mount, cancellationToken);
            var result = await RunAsync(["--config", config, "lsf", Remote(mount), "--max-depth", "1"],
                null, TimeSpan.FromSeconds(10), cancellationToken, captureStdout: false,
                guardedPath: mount.LocalPath, stopAfterFirstEntry: true);
            if (result.ExitCode == 0) return (true, false, null);
            var error = Redact(result.Stderr, mount);
            var rejected = error.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                || error.Contains("access denied", StringComparison.OrdinalIgnoreCase)
                || error.Contains("invalidaccesskeyid", StringComparison.OrdinalIgnoreCase)
                || error.Contains("signaturedoesnotmatch", StringComparison.OrdinalIgnoreCase)
                || error.Contains("key mismatch", StringComparison.OrdinalIgnoreCase);
            return (false, rejected, $"远端目录读取失败（exit {result.ExitCode}）：{error}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { return (false, false, $"远端探测失败：{Redact(ex.Message, mount)}"); }
    }

    public Task<(bool Success, string? Error)> ProbeLocalAsync(RcloneMount mount, CancellationToken cancellationToken = default) =>
        SmbMountRuntimeProbe.RunFsProbeAsync(["ls", "-A", "--", mount.LocalPath], TimeSpan.FromSeconds(5),
            "本地目录探测超时", cancellationToken);

    public void DeleteConfig(Guid id)
    {
        var path = ConfigPath(id);
        if (File.Exists(path)) File.Delete(path);
        var knownHosts = KnownHostsPath(id);
        if (File.Exists(knownHosts)) File.Delete(knownHosts);
    }

    private async Task<string> EnsureConfigAsync(RcloneMount mount, CancellationToken cancellationToken)
    {
        var directory = dataPaths.Resolve(ConfigFolder);
        Directory.CreateDirectory(directory);
        var config = new StringBuilder().Append('[').Append(RemoteName(mount)).Append("]\n");
        if (mount.Kind == "sftp")
        {
            var password = await ObscureAsync(mount.Password, cancellationToken);
            var knownHostsPath = KnownHostsPath(mount.Id);
            var hostField = mount.Port == 22 ? mount.Host : $"[{mount.Host}]:{mount.Port}";
            await WritePrivateFileAsync(knownHostsPath, $"{hostField} {mount.HostKey}\n", cancellationToken);
            config.Append("type = sftp\n")
                .Append("host = ").Append(mount.Host).Append('\n')
                .Append("port = ").Append(mount.Port).Append('\n')
                .Append("user = ").Append(mount.Username).Append('\n')
                .Append("pass = ").Append(password).Append('\n')
                .Append("known_hosts_file = ").Append(knownHostsPath).Append('\n');
            if (!string.IsNullOrEmpty(mount.KeyFile)) config.Append("key_file = ").Append(mount.KeyFile).Append('\n');
        }
        else if (mount.Kind == "s3")
        {
            config.Append("type = s3\n")
                .Append("provider = ").Append(string.IsNullOrEmpty(mount.Endpoint) ? "AWS" : "Other").Append('\n')
                .Append("env_auth = false\n")
                .Append("access_key_id = ").Append(mount.AccessKeyId).Append('\n')
                .Append("secret_access_key = ").Append(mount.SecretAccessKey).Append('\n')
                .Append("region = ").Append(mount.Region).Append('\n');
            if (!string.IsNullOrEmpty(mount.Endpoint)) config.Append("endpoint = ").Append(mount.Endpoint).Append('\n');
        }
        else throw new ArgumentException("不支持的 rclone 后端");
        var path = ConfigPath(mount.Id);
        await WritePrivateFileAsync(path, config.ToString(), cancellationToken);
        return path;
    }

    private static async Task WritePrivateFileAsync(string path, string contents, CancellationToken cancellationToken)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (OperatingSystem.IsLinux()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
                await stream.WriteAsync(Encoding.UTF8.GetBytes(contents), cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static async Task<string> ObscureAsync(string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var result = await RunAsync(["obscure", "-"], value + "\n", TimeSpan.FromSeconds(10), cancellationToken);
        if (result.ExitCode != 0) throw new IOException("rclone 密码转换失败");
        return result.Stdout.Trim();
    }

    private string ConfigPath(Guid id) => Path.Combine(dataPaths.Resolve(ConfigFolder), id.ToString("N") + ".conf");
    private string KnownHostsPath(Guid id) => Path.Combine(dataPaths.Resolve(ConfigFolder), id.ToString("N") + ".known_hosts");
    private static string RemoteName(RcloneMount mount) => "rclone_" + mount.Id.ToString("N");
    private static string Remote(RcloneMount mount) => RemoteName(mount) + ":" +
        (mount.Kind == "s3" ? mount.Bucket + (string.IsNullOrEmpty(mount.RemotePath) ? "" : "/" + mount.RemotePath.Trim('/'))
            : mount.RemotePath);

    private static string Redact(string value, RcloneMount mount)
    {
        foreach (var secret in new[] { mount.Password, mount.SecretAccessKey, mount.AccessKeyId })
            if (!string.IsNullOrEmpty(secret)) value = value.Replace(secret, "***", StringComparison.Ordinal);
        return value.Length > 400 ? value[..400] : value;
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        IReadOnlyList<string> args, string? stdin, TimeSpan timeout, CancellationToken cancellationToken,
        bool captureStdout = true, string? guardedPath = null, bool stopAfterFirstEntry = false) =>
        RunProcessAsync("rclone", args, stdin, timeout, cancellationToken, captureStdout, guardedPath, stopAfterFirstEntry);

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName, IReadOnlyList<string> args, string? stdin, TimeSpan timeout, CancellationToken cancellationToken,
        bool captureStdout = true, string? guardedPath = null, bool stopAfterFirstEntry = false)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo
        {
            FileName = fileName, UseShellExecute = false, RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
        } };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        if (stdin is not null) { await process.StandardInput.WriteAsync(stdin); process.StandardInput.Close(); }
        var observedEntry = false;
        var stdout = stopAfterFirstEntry ? ReadFirstEntryAsync(process, () => observedEntry = true, cancellationToken)
            : captureStdout ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : DrainAsync(process.StandardOutput, cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken); }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (guardedPath is not null) MountProbeProcessGuard.RecordIfAlive(guardedPath, process);
            return (-1, string.Empty, "命令执行超时");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            if (guardedPath is not null) MountProbeProcessGuard.RecordIfAlive(guardedPath, process);
            throw;
        }
        var output = await stdout;
        var error = await stderr;
        return (observedEntry ? 0 : process.ExitCode, output, error);
    }

    private static async Task<string> ReadFirstEntryAsync(Process process, Action onEntry, CancellationToken cancellationToken)
    {
        // One visible entry proves directory listing access; an empty directory must exit successfully.
        var entry = await process.StandardOutput.ReadLineAsync(cancellationToken);
        if (entry is not null)
        {
            onEntry();
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        return string.Empty;
    }

    private static async Task<string> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        await reader.BaseStream.CopyToAsync(Stream.Null, cancellationToken);
        return string.Empty;
    }
}
