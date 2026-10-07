using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;

public enum EasyTierEngineMode
{
    None,
    NativeFfi,
    CoreBinary
}

/// <summary>
/// EasyTier 核心宿主监管与内核热升级调度器
/// 负责动态探测原生 FFI 库与官方 Core 二进制、版本查询、GitHub 云端拉取与双模平滑升级
/// </summary>
public class EasyTierHostSupervisor : IHostedService
{
    private readonly DataPaths dataPaths;
    private readonly ILogger<EasyTierHostSupervisor> logger;
    private readonly string _storageDir;
    private readonly string _binDir;
    private readonly string _stagingDir;
    private readonly HttpClient _httpClient;

    private DateTime? _startTime;
    private string _currentVersion = "Unknown";

    public string StorageDirectory => _storageDir;
    public string BinDirectory => _binDir;

    public EasyTierHostSupervisor(
        DataPaths dataPaths,
        ILogger<EasyTierHostSupervisor> logger)
    {
        this.dataPaths = dataPaths;
        this.logger = logger;
        _storageDir = dataPaths.PathFor("easytier");
        _binDir = Path.Combine(_storageDir, "bin");
        _stagingDir = Path.Combine(_storageDir, "staging");
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15)
        })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var libFileName = isWindows ? "easytier_ffi.dll" : "libeasytier_ffi.so";
        EasyTierNativeMethods.SetCustomLibraryPath(Path.Combine(_binDir, libFileName));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _startTime = DateTime.UtcNow;
        Directory.CreateDirectory(_storageDir);
        Directory.CreateDirectory(_binDir);
        Directory.CreateDirectory(_stagingDir);
        Directory.CreateDirectory(Path.Combine(_storageDir, "nodes"));

        var mode = GetEngineMode(out var resolvedPath);
        if (mode != EasyTierEngineMode.None)
        {
            _currentVersion = DetectVersion(resolvedPath, mode);
            logger.LogInformation("EasyTier Engine ready! Mode: {Mode}, Path: {Path}, Version: {Version}",
                mode, resolvedPath, _currentVersion);
        }
        else
        {
            logger.LogWarning("EasyTier Engine not found in {BinDir}. Awaiting GitHub download or manual upload.", _binDir);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public EasyTierEngineMode GetEngineMode(out string? resolvedPath)
    {
        // 1. 优先探测 Native FFI 动态链接库
        if (EasyTierNativeMethods.ProbeLibrary(out var ffiPath))
        {
            resolvedPath = ffiPath;
            return EasyTierEngineMode.NativeFfi;
        }

        // 2. 探测官方 Core 独立二进制
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var coreName = isWindows ? "easytier-core.exe" : "easytier-core";
        var probePaths = new[]
        {
            Path.Combine(_binDir, coreName),
            Path.Combine(AppContext.BaseDirectory, "data", "easytier", "bin", coreName),
            Path.Combine(AppContext.BaseDirectory, coreName),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "easytier", "bin", coreName),
            Path.Combine(Directory.GetCurrentDirectory(), coreName)
        };

        foreach (var p in probePaths)
        {
            if (File.Exists(p))
            {
                resolvedPath = Path.GetFullPath(p);
                return EasyTierEngineMode.CoreBinary;
            }
        }

        resolvedPath = null;
        return EasyTierEngineMode.None;
    }

    public bool GetCliPath(out string? cliPath)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var cliName = isWindows ? "easytier-cli.exe" : "easytier-cli";
        var probePaths = new[]
        {
            Path.Combine(_binDir, cliName),
            Path.Combine(AppContext.BaseDirectory, "data", "easytier", "bin", cliName),
            Path.Combine(AppContext.BaseDirectory, cliName),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "easytier", "bin", cliName),
            Path.Combine(Directory.GetCurrentDirectory(), cliName)
        };

        foreach (var p in probePaths)
        {
            if (File.Exists(p))
            {
                cliPath = Path.GetFullPath(p);
                return true;
            }
        }

        cliPath = null;
        return false;
    }

    public Task<EasyTierEngineStatusDto> GetEngineStatusAsync(int activeNodeCount)
    {
        var mode = GetEngineMode(out var resolvedPath);
        var isInstalled = mode != EasyTierEngineMode.None;
        if (isInstalled && (_currentVersion == "Unknown" || string.IsNullOrWhiteSpace(_currentVersion)))
        {
            _currentVersion = DetectVersion(resolvedPath, mode);
        }

        var modeLabel = mode switch
        {
            EasyTierEngineMode.NativeFfi => "C ABI / Native FFI",
            EasyTierEngineMode.CoreBinary => "Core Binary (Daemon)",
            _ => "未就绪"
        };

        var hasAdmin = CheckProcessElevation(out var privilegeWarning);

        var status = new EasyTierEngineStatusDto(
            IsInstalled: isInstalled,
            IsRunning: isInstalled && activeNodeCount > 0,
            Version: isInstalled ? _currentVersion : "未安装",
            Mode: modeLabel,
            ProcessId: Environment.ProcessId,
            NativeLibraryPath: resolvedPath ?? "未就绪",
            StorageDirectory: _storageDir,
            ActiveNodeCount: activeNodeCount,
            StartTime: _startTime,
            LastError: isInstalled ? null : "未检测到 easytier_ffi 或 easytier-core 二进制文件",
            HasAdminPrivilege: hasAdmin,
            PrivilegeWarning: privilegeWarning);

        return Task.FromResult(status);
    }

    public static bool CheckProcessElevation(out string? warning)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                var isAdmin = principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
                if (!isAdmin)
                {
                    warning = "当前 WebHost 服务以普通用户权限运行。在 Windows 上创建 TUN 虚拟网卡需要管理员权限，请通过“以管理员身份运行”启动程序，避免出现网卡创建失败或 DHCP 无法生效的问题。";
                    return false;
                }
            }
            catch
            {
                // ignore
            }
            warning = null;
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            var isRoot = Environment.UserName == "root" || Environment.GetEnvironmentVariable("USER") == "root";
            if (!isRoot)
            {
                warning = "当前未以 root 权限运行。在 Linux 下创建 TUN 虚拟网卡需要 root 权限，或执行 'sudo setcap cap_net_admin=+ep <easytier-core路径>' 赋予网卡管理能力。";
                return false;
            }
            warning = null;
            return true;
        }

        warning = null;
        return true;
    }

    public static string GetPlatformAssetPattern()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
        var isMac = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var arch = RuntimeInformation.ProcessArchitecture;

        if (isWindows)
        {
            return arch switch
            {
                Architecture.X64 => "easytier-windows-x86_64",
                Architecture.Arm64 => "easytier-windows-arm64",
                Architecture.X86 => "easytier-windows-i686",
                _ => "easytier-windows-x86_64"
            };
        }
        if (isLinux)
        {
            return arch switch
            {
                Architecture.X64 => "easytier-linux-x86_64",
                Architecture.Arm64 => "easytier-linux-aarch64",
                Architecture.Arm => "easytier-linux-arm",
                _ => "easytier-linux-x86_64"
            };
        }
        if (isMac)
        {
            return arch switch
            {
                Architecture.Arm64 => "easytier-macos-aarch64",
                _ => "easytier-macos-x86_64"
            };
        }
        return "easytier";
    }

    public async Task<EasyTierGitHubReleaseInfoDto> CheckGitHubReleaseAsync(string? proxyPrefix, CancellationToken ct = default)
    {
        var apiUrl = "https://api.github.com/repos/EasyTier/EasyTier/releases/latest";
        using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        req.Headers.UserAgent.ParseAdd("LinuxWebTool/1.0 (Windows/Linux; NET10)");
        req.Headers.Accept.ParseAdd("application/vnd.github.v3+json");

        using var resp = await _httpClient.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var tagName = root.GetProperty("tag_name").GetString() ?? "latest";
        var name = root.TryGetProperty("name", out var nP) ? nP.GetString() ?? tagName : tagName;
        var publishedAt = root.TryGetProperty("published_at", out var pubP) ? pubP.GetDateTime() : DateTime.UtcNow;
        var body = root.TryGetProperty("body", out var bP) ? bP.GetString() ?? "" : "";

        var pattern = GetPlatformAssetPattern();
        string matchedFileName = string.Empty;
        string matchedDownloadUrl = string.Empty;
        long matchedSize = 0;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                var aName = a.GetProperty("name").GetString() ?? "";
                if (aName.StartsWith(pattern, StringComparison.OrdinalIgnoreCase) &&
                    aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    matchedFileName = aName;
                    matchedDownloadUrl = a.GetProperty("browser_download_url").GetString() ?? "";
                    matchedSize = a.TryGetProperty("size", out var sP) ? sP.GetInt64() : 0;
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(matchedFileName))
        {
            throw new InvalidOperationException($"在 EasyTier {tagName} 发布包中未找到适配当前系统架构 ({pattern}) 的 .zip 资源。");
        }

        var normalizedCurrent = _currentVersion.Trim().TrimStart('v');
        var normalizedRemote = tagName.Trim().TrimStart('v');
        var hasUpdate = !string.Equals(normalizedCurrent, normalizedRemote, StringComparison.OrdinalIgnoreCase);

        return new EasyTierGitHubReleaseInfoDto(
            TagName: tagName,
            Name: name,
            PublishedAt: publishedAt,
            Body: body,
            MatchingAssetFileName: matchedFileName,
            MatchingAssetDownloadUrl: matchedDownloadUrl,
            MatchingAssetSize: matchedSize,
            CurrentVersion: _currentVersion,
            HasUpdate: hasUpdate);
    }

    public async Task<EasyTierUpgradeResultDto> InstallGitHubReleaseAsync(
        InstallGitHubReleaseRequest request,
        Func<Task<int>> onBeforeSwapCallback,
        Func<Task<int>> onAfterSwapCallback,
        CancellationToken ct = default)
    {
        var releaseInfo = await CheckGitHubReleaseAsync(request.ProxyPrefix, ct);
        var downloadUrl = releaseInfo.MatchingAssetDownloadUrl;
        if (!string.IsNullOrWhiteSpace(request.ProxyPrefix))
        {
            var cleanPrefix = request.ProxyPrefix.TrimEnd('/');
            if (!downloadUrl.StartsWith(cleanPrefix, StringComparison.OrdinalIgnoreCase))
            {
                downloadUrl = $"{cleanPrefix}/{downloadUrl}";
            }
        }

        logger.LogInformation("Downloading EasyTier release from: {Url}...", downloadUrl);

        var tempZipPath = Path.Combine(_stagingDir, $"{Guid.NewGuid():N}_{releaseInfo.MatchingAssetFileName}");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.UserAgent.ParseAdd("LinuxWebTool/1.0");
            using var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                await resp.Content.CopyToAsync(fs, ct);
            }

            logger.LogInformation("Downloaded {Size} bytes for EasyTier release. Unpacking into bin...", new FileInfo(tempZipPath).Length);

            // 排空旧网络实例
            var drainedCount = await onBeforeSwapCallback();

            // 解压所有文件到 bin 目录
            UnpackZipToBin(tempZipPath);

            var mode = GetEngineMode(out var resolvedPath);
            _currentVersion = DetectVersion(resolvedPath, mode);

            // 恢复实例
            var restoredCount = await onAfterSwapCallback();

            return new EasyTierUpgradeResultDto(
                Success: true,
                PreviousVersion: releaseInfo.CurrentVersion,
                NewVersion: _currentVersion,
                Message: $"EasyTier 内核已成功从 GitHub 发布包安装并热更新至 {_currentVersion}！",
                RestoredNodeCount: restoredCount,
                ExecutedAt: DateTime.UtcNow);
        }
        finally
        {
            if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
        }
    }

    public async Task<EasyTierUpgradeResultDto> ExecuteHotUpgradeAsync(
        Stream binaryStream,
        string fileName,
        Func<Task<int>> onBeforeSwapCallback,
        Func<Task<int>> onAfterSwapCallback,
        CancellationToken ct = default)
    {
        var previousVersion = _currentVersion;
        var cleanFileName = Path.GetFileName(fileName);
        var isZip = cleanFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        var stagedPath = Path.Combine(_stagingDir, $"{Guid.NewGuid():N}_{cleanFileName}");

        logger.LogInformation("Receiving EasyTier local update ({FileName}) into staging...", cleanFileName);

        // 1. 写入暂存文件
        await using (var fs = new FileStream(stagedPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            await binaryStream.CopyToAsync(fs, ct);
        }

        // 2. 升级前排空已运行的实例并记录状态
        var drainedCount = await onBeforeSwapCallback();

        try
        {
            if (isZip)
            {
                UnpackZipToBin(stagedPath);
            }
            else
            {
                var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
                var targetName = cleanFileName;
                if (cleanFileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) targetName = "easytier_ffi.dll";
                else if (cleanFileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)) targetName = "libeasytier_ffi.so";

                var finalPath = Path.Combine(_binDir, targetName);
                File.Copy(stagedPath, finalPath, overwrite: true);
                if (!isWindows) SetExecutablePermissions(finalPath);
            }

            var mode = GetEngineMode(out var resolvedPath);
            var newVersion = DetectVersion(resolvedPath, mode);
            _currentVersion = newVersion;

            // 3. 升级后恢复网络实例
            var restoredCount = await onAfterSwapCallback();

            logger.LogInformation("EasyTier upgraded to {NewVersion}. Restored {Count} active nodes.", newVersion, restoredCount);

            return new EasyTierUpgradeResultDto(
                Success: true,
                PreviousVersion: previousVersion,
                NewVersion: newVersion,
                Message: $"EasyTier 核心已平滑更新至 {newVersion}，已自动恢复 {restoredCount} 个网络节点",
                RestoredNodeCount: restoredCount,
                ExecutedAt: DateTime.UtcNow);
        }
        finally
        {
            if (File.Exists(stagedPath)) File.Delete(stagedPath);
        }
    }

    private void UnpackZipToBin(string zipFilePath)
    {
        using var zip = ZipFile.OpenRead(zipFilePath);
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        foreach (var entry in zip.Entries)
        {
            var fileName = Path.GetFileName(entry.FullName);
            if (string.IsNullOrWhiteSpace(fileName)) continue; // 跳过目录

            var destPath = Path.Combine(_binDir, fileName);
            try
            {
                if (File.Exists(destPath))
                {
                    var backup = destPath + ".old";
                    if (File.Exists(backup)) File.Delete(backup);
                    try { File.Move(destPath, backup); } catch { }
                }

                entry.ExtractToFile(destPath, overwrite: true);

                if (!isWindows && (fileName == "easytier-core" || fileName == "easytier-cli" || fileName.EndsWith(".so")))
                {
                    SetExecutablePermissions(destPath);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to unpack entry {Name} to {Dest}", entry.FullName, destPath);
            }
        }
    }

    private static void SetExecutablePermissions(string filePath)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(filePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private string DetectVersion(string? resolvedPath, EasyTierEngineMode mode)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath) || !File.Exists(resolvedPath))
        {
            return "2.6.4";
        }

        if (mode == EasyTierEngineMode.CoreBinary)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = resolvedPath,
                    Arguments = "-V",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                if (proc != null)
                {
                    var output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(3000);
                    // output: "easytier-core 2.6.4-8428a89d"
                    var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var ver = parts[1].Split('-')[0];
                        return ver;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to run easytier-core -V on {Path}", resolvedPath);
            }
        }
        else if (mode == EasyTierEngineMode.NativeFfi)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(resolvedPath);
                if (!string.IsNullOrWhiteSpace(info.ProductVersion)) return info.ProductVersion;
                if (!string.IsNullOrWhiteSpace(info.FileVersion)) return info.FileVersion;
            }
            catch { }
        }

        return "2.6.4";
    }
}
