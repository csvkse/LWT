using System.IO.Compression;
using System.Security;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Workers;

public sealed partial class WorkersManager
{
    public async ValueTask<WorkerInstanceDto> ImportZipAsync(Stream zipStream, CancellationToken ct = default)
    {
        var tempExtractDir = Path.Combine(Path.GetTempPath(), "lwt_worker_zip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                    {
                        continue;
                    }

                    var destinationPath = Path.GetFullPath(Path.Combine(tempExtractDir, entry.FullName));
                    if (!destinationPath.StartsWith(tempExtractDir, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new SecurityException("非法压缩包：检测到潜在的 Zip Slip 路径穿越安全风险！");
                    }

                    var parentDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    entry.ExtractToFile(destinationPath, overwrite: true);
                }
            }

            // 寻找 worker.json 所在目录
            var manifestFiles = Directory.GetFiles(tempExtractDir, "worker.json", SearchOption.AllDirectories);
            if (manifestFiles.Length == 0)
            {
                throw new InvalidOperationException("导入失败：Zip 压缩包内未找到有效的 worker.json 清单配置文件。");
            }

            var manifestPath = manifestFiles[0];
            var sourceDir = Path.GetDirectoryName(manifestPath)!;
            var json = await File.ReadAllTextAsync(manifestPath, ct);
            var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest)
                ?? throw new InvalidOperationException("导入失败：worker.json 内容格式错误。");

            var targetDir = Path.Combine(_workersRoot, manifest.Id);
            Directory.CreateDirectory(targetDir);

            // 拷贝全部文件至目标目录
            CopyDirectory(sourceDir, targetDir, overwrite: true);

            return BuildDto(manifest, targetDir);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempExtractDir))
                {
                    Directory.Delete(tempExtractDir, true);
                }
            }
            catch { }
        }
    }

    public async ValueTask<WorkerRepoScanResult> ScanRepoAsync(string repoUrl, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_worker_repo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var normalizedUrl = NormalizeGitUrl(repoUrl);
            var cloneSuccess = await RunGitCloneAsync(normalizedUrl, tempDir, ct);
            if (!cloneSuccess)
            {
                throw new InvalidOperationException($"无法拉取或克隆 Git 仓库: {repoUrl}，请检查网络或仓库地址有效性。");
            }

            var manifestFiles = Directory.GetFiles(tempDir, "worker.json", SearchOption.AllDirectories);
            var items = new List<WorkerRepoScanItem>();

            foreach (var mf in manifestFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(mf, ct);
                    var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
                    if (manifest == null) continue;

                    var moduleDir = Path.GetDirectoryName(mf)!;
                    var relPath = Path.GetRelativePath(tempDir, moduleDir);
                    var installedDir = Path.Combine(_workersRoot, manifest.Id);

                    items.Add(new WorkerRepoScanItem
                    {
                        Id = manifest.Id,
                        Name = manifest.Name,
                        Description = manifest.Description,
                        Mode = manifest.Mode,
                        Runtime = manifest.Runtime,
                        RelativePath = relPath,
                        AlreadyInstalled = Directory.Exists(installedDir)
                    });
                }
                catch { }
            }

            return new WorkerRepoScanResult
            {
                RepoUrl = repoUrl,
                TempDirectory = tempDir,
                Modules = items
            };
        }
        catch
        {
            try { Directory.Delete(tempDir, true); } catch { }
            throw;
        }
    }

    public ValueTask<int> ApplyRepoModulesAsync(WorkerRepoApplyRequest request, CancellationToken ct = default)
    {
        if (!Directory.Exists(request.TempDirectory))
        {
            throw new DirectoryNotFoundException("临时扫描仓库已过期或不存在，请重新扫描。");
        }

        var count = 0;
        try
        {
            var manifestFiles = Directory.GetFiles(request.TempDirectory, "worker.json", SearchOption.AllDirectories);
            foreach (var mf in manifestFiles)
            {
                try
                {
                    var json = File.ReadAllText(mf);
                    var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
                    if (manifest == null || !request.ModuleIds.Contains(manifest.Id, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sourceDir = Path.GetDirectoryName(mf)!;
                    var targetDir = Path.Combine(_workersRoot, manifest.Id);

                    if (Directory.Exists(targetDir) && !request.OverwriteExisting)
                    {
                        continue;
                    }

                    Directory.CreateDirectory(targetDir);
                    CopyDirectory(sourceDir, targetDir, overwrite: true);
                    count++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "批量应用仓库模块异常: {ManifestFile}", mf);
                }
            }

            return ValueTask.FromResult(count);
        }
        finally
        {
            try { Directory.Delete(request.TempDirectory, true); } catch { }
        }
    }

    private static string NormalizeGitUrl(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        // 短路径 github 简写: owner/repo
        if (trimmed.Contains('/') && !trimmed.Contains(' '))
        {
            return $"https://github.com/{trimmed}.git";
        }

        return trimmed;
    }

    private static async Task<bool> RunGitCloneAsync(string url, string targetDir, CancellationToken ct)
    {
        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"clone --depth 1 \"{url}\" \"{targetDir}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (proc == null) return false;
            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir, bool overwrite)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            if (dirName.Equals(".git", StringComparison.OrdinalIgnoreCase)) continue;
            var dest = Path.Combine(targetDir, dirName);
            CopyDirectory(subDir, dest, overwrite);
        }
    }
}
