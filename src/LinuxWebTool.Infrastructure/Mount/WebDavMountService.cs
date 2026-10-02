using System.Diagnostics;
using System.Text;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using LinuxWebTool.Infrastructure.Support;
using LinuxWebTool.Infrastructure.SystemInfo;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>通过 rclone/FUSE 管理 WebDAV 挂载。每个挂载独占配置和写回缓存。</summary>
public sealed class WebDavMountService(DataPaths dataPaths, ILogger<WebDavMountService> logger)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(40);

    public static bool IsSupported => OperatingSystem.IsLinux()
        && File.Exists("/dev/fuse")
        && (File.Exists("/usr/bin/rclone") || File.Exists("/bin/rclone"));

    private string ConfigDirectory => dataPaths.Resolve("webdav-config");
    private string CacheDirectory => dataPaths.Resolve("webdav-cache");

    public SmbMountStatus GetStatus(WebDavMount mount)
    {
        if (!OperatingSystem.IsLinux()) return SmbMountStatus.Unsupported;
        var entry = ReadMount(mount.LocalPath);
        if (entry is null) return SmbMountStatus.NotMounted;
        return entry.FileSystem == "fuse.rclone" && entry.Source.Contains(mount.Id.ToString("N"), StringComparison.OrdinalIgnoreCase)
            ? SmbMountStatus.Mounted
            : SmbMountStatus.Abnormal;
    }

    public async Task<(bool Success, string Message)> MountAsync(WebDavMount mount, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) return (false, "需要 Linux、rclone 和 /dev/fuse；请检查镜像依赖与容器挂载权限");
        if (GetStatus(mount) == SmbMountStatus.Mounted) return (true, "已挂载");
        if (GetStatus(mount) == SmbMountStatus.Abnormal) return (false, "挂载点已被其他文件系统占用");

        try
        {
            Directory.CreateDirectory(mount.LocalPath);
            var configPath = await EnsureConfigAsync(mount, cancellationToken);
            var cachePath = Path.Combine(CacheDirectory, mount.Id.ToString("N"));
            Directory.CreateDirectory(cachePath);
            var args = new List<string>
            {
                "--config", configPath, "mount", RemoteName(mount) + ":", mount.LocalPath,
                "--daemon", "--daemon-wait", "30s", "--vfs-cache-mode", "writes",
                "--cache-dir", cachePath, "--vfs-cache-max-size", "1G",
            };
            var result = await RunAsync("rclone", args, null, cancellationToken);
            if (result.ExitCode != 0)
                return (false, $"rclone 挂载失败（exit {result.ExitCode}）：{Redact(result.Stderr, mount)}");
            if (GetStatus(mount) != SmbMountStatus.Mounted)
                return (false, "rclone 返回成功，但未找到匹配的 FUSE 挂载记录");
            SystemStatusProvider.ManagedMountPoints[MountOperationCoordinator.NormalizePath(mount.LocalPath)] = 0;
            logger.LogInformation("WebDAV 挂载成功：{Name} → {LocalPath}", mount.Name, mount.LocalPath);
            return (true, "挂载成功");
        }
        catch (Exception ex)
        {
            return (false, $"WebDAV 挂载失败：{Redact(ex.Message, mount)}");
        }
    }

    public async Task<(bool Success, string Message)> UnmountAsync(WebDavMount mount, bool lazy, CancellationToken cancellationToken = default)
    {
        var status = GetStatus(mount);
        if (status == SmbMountStatus.NotMounted) return (true, "挂载点当前未挂载");
        if (status != SmbMountStatus.Mounted) return (false, "挂载点并非当前 WebDAV 配置，拒绝卸载");
        var args = lazy ? new List<string> { "-l", "--", mount.LocalPath } : new List<string> { "--", mount.LocalPath };
        var result = await RunAsync("umount", args, null, cancellationToken);
        if (result.ExitCode != 0) return (false, $"卸载失败（exit {result.ExitCode}）：{Redact(result.Stderr, mount)}");
        logger.LogInformation("WebDAV 卸载：{LocalPath}", mount.LocalPath);
        return (true, "已卸载；待上传缓存保留");
    }

    public void DeleteConfig(Guid id)
    {
        var path = ConfigPath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    private async Task<string> EnsureConfigAsync(WebDavMount mount, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(ConfigDirectory);
        var obscured = string.Empty;
        if (!string.IsNullOrEmpty(mount.Password))
        {
            if (mount.Password.Contains('\n') || mount.Password.Contains('\r'))
                throw new ArgumentException("密码不能包含换行符");
            var result = await RunAsync("rclone", ["obscure", "-"], mount.Password + "\n", cancellationToken);
            if (result.ExitCode != 0) throw new IOException("rclone 密码转换失败");
            obscured = result.Stdout.Trim();
        }

        var config = $"[{RemoteName(mount)}]\n" +
            "type = webdav\n" +
            $"url = {mount.Url}\n" +
            "vendor = other\n" +
            $"user = {mount.Username}\n" +
            $"pass = {obscured}\n";
        var path = ConfigPath(mount.Id);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
            };
            if (OperatingSystem.IsLinux())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temp, options))
            {
                await stream.WriteAsync(Encoding.UTF8.GetBytes(config), cancellationToken);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return path;
    }

    private string ConfigPath(Guid id) => Path.Combine(ConfigDirectory, id.ToString("N") + ".conf");
    private static string RemoteName(WebDavMount mount) => "webdav_" + mount.Id.ToString("N");

    private static MountMetadata? ReadMount(string path)
    {
        try
        {
            var target = MountOperationCoordinator.NormalizePath(path);
            return LinuxMountInfoParser.Parse(File.ReadAllText("/proc/self/mountinfo"))
                .LastOrDefault(m => MountOperationCoordinator.NormalizePath(m.MountPoint) == target);
        }
        catch { return null; }
    }

    private static string Redact(string value, WebDavMount mount)
    {
        if (!string.IsNullOrEmpty(mount.Password)) value = value.Replace(mount.Password, "***", StringComparison.Ordinal);
        return value.Replace(mount.Url, "[WebDAV URL]", StringComparison.Ordinal);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunAsync(
        string fileName, IReadOnlyList<string> args, string? stdin, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                UseShellExecute = false,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(CommandTimeout, cancellationToken); }
        catch (TimeoutException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (-1, string.Empty, "命令执行超时");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
