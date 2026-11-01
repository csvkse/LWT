using System.Diagnostics;
using System.IO.Compression;
using System.Security;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager
{
    public async ValueTask<PluginInstanceDto> ImportZipAsync(Stream zipStream, CancellationToken ct = default)
    {
        var tempExtractDir = Path.Combine(Path.GetTempPath(), "lwt_plugin_zip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempExtractDir);

        try
        {
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false))
            {
                PluginSecurityGuard.ValidateZipArchive(archive);

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

            var manifestFiles = Directory.GetFiles(tempExtractDir, "plugin.json", SearchOption.AllDirectories);
            if (manifestFiles.Length == 0)
            {
                throw new InvalidOperationException("导入失败：Zip 压缩包内未找到有效的 plugin.json 清单配置文件。");
            }

            var manifestPath = manifestFiles[0];
            var sourceDir = Path.GetDirectoryName(manifestPath)!;
            var json = await File.ReadAllTextAsync(manifestPath, ct);
            var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest)
                ?? throw new InvalidOperationException("导入失败：plugin.json 内容格式错误。");

            if (!PluginSecurityGuard.IsValidPluginId(manifest.Id))
            {
                throw new SecurityException($"导入失败：插件清单中的 ID '{manifest.Id}' 不合法。");
            }

            var targetDir = PluginSecurityGuard.EnsureSafePluginDirectory(_pluginsRoot, manifest.Id);

            string? backupDataDb = null;
            string? backupKvDb = null;
            bool wasDisabled = false;

            if (Directory.Exists(targetDir))
            {
                var srcDataDb = Path.Combine(targetDir, "data.db");
                if (File.Exists(srcDataDb))
                {
                    backupDataDb = Path.Combine(tempExtractDir, "__preserve_data.db");
                    File.Copy(srcDataDb, backupDataDb, overwrite: true);
                }

                var srcKvDb = Path.Combine(targetDir, "kv.db");
                if (File.Exists(srcKvDb))
                {
                    backupKvDb = Path.Combine(tempExtractDir, "__preserve_kv.db");
                    File.Copy(srcKvDb, backupKvDb, overwrite: true);
                }

                wasDisabled = File.Exists(Path.Combine(targetDir, ".disabled"));
            }

            Directory.CreateDirectory(targetDir);
            CopyDirectory(sourceDir, targetDir, overwrite: true);

            if (backupDataDb != null && File.Exists(backupDataDb))
            {
                File.Copy(backupDataDb, Path.Combine(targetDir, "data.db"), overwrite: true);
            }
            if (backupKvDb != null && File.Exists(backupKvDb))
            {
                File.Copy(backupKvDb, Path.Combine(targetDir, "kv.db"), overwrite: true);
            }
            if (wasDisabled)
            {
                await File.WriteAllTextAsync(Path.Combine(targetDir, ".disabled"), "disabled", ct);
            }

            var sourceMeta = new PluginSourceMetadata
            {
                SourceType = "zip",
                InstalledAt = DateTimeOffset.UtcNow
            };
            await File.WriteAllTextAsync(Path.Combine(targetDir, "source.json"), JsonSerializer.Serialize(sourceMeta, PluginJsonContext.Default.PluginSourceMetadata), ct);

            return BuildInstanceDto(manifest, targetDir);
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

    public async ValueTask<PluginRepoScanResult> ScanRepoAsync(string repoUrl, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_plugin_repo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var normalizedUrl = NormalizeGitUrl(repoUrl);
            var cloneSuccess = await RunGitCloneAsync(normalizedUrl, tempDir, ct);
            if (!cloneSuccess)
            {
                throw new InvalidOperationException($"无法拉取或克隆 Git 仓库: {repoUrl}，请检查网络或仓库地址有效性。");
            }

            var manifestFiles = Directory.GetFiles(tempDir, "plugin.json", SearchOption.AllDirectories);
            var modules = new List<PluginRepoModule>();

            foreach (var mf in manifestFiles)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(mf, ct);
                    var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
                    if (manifest == null) continue;

                    var moduleDir = Path.GetDirectoryName(mf)!;
                    var relPath = Path.GetRelativePath(tempDir, moduleDir).Replace('\\', '/');
                    var installedDir = Path.Combine(_pluginsRoot, manifest.Id);

                    modules.Add(new PluginRepoModule
                    {
                        Id = manifest.Id,
                        Name = manifest.Name,
                        Description = manifest.Description,
                        Icon = manifest.Icon,
                        Category = manifest.Category,
                        Profile = manifest.Profile,
                        Runtime = manifest.Runtime,
                        Path = relPath,
                        ExistsLocally = Directory.Exists(installedDir)
                    });
                }
                catch { }
            }

            return new PluginRepoScanResult
            {
                RepoUrl = repoUrl,
                Modules = modules
            };
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public async ValueTask<PluginBatchImportResult> ApplyRepoModulesAsync(PluginRepoApplyRequest request, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_plugin_repo_apply_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var importedIds = new List<string>();
        var errors = new List<string>();

        try
        {
            var normalizedUrl = NormalizeGitUrl(request.RepoUrl);
            var cloneSuccess = await RunGitCloneAsync(normalizedUrl, tempDir, ct);
            if (!cloneSuccess)
            {
                throw new InvalidOperationException($"无法克隆 Git 仓库: {request.RepoUrl}");
            }

            foreach (var modRelPath in request.ModulePaths)
            {
                try
                {
                    var moduleDir = Path.Combine(tempDir, modRelPath);
                    var manifestPath = Path.Combine(moduleDir, "plugin.json");
                    if (!File.Exists(manifestPath)) continue;

                    var json = await File.ReadAllTextAsync(manifestPath, ct);
                    var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
                    if (manifest == null) continue;

                    if (!PluginSecurityGuard.IsValidPluginId(manifest.Id))
                    {
                        errors.Add($"插件 ID '{manifest.Id}' 不合法");
                        continue;
                    }

                    var targetDir = Path.Combine(_pluginsRoot, manifest.Id);
                    Directory.CreateDirectory(targetDir);
                    CopyDirectory(moduleDir, targetDir, overwrite: true);

                    var sourceMeta = new PluginSourceMetadata
                    {
                        SourceType = "git",
                        RepoUrl = request.RepoUrl,
                        SubPath = modRelPath,
                        InstalledAt = DateTimeOffset.UtcNow
                    };
                    await File.WriteAllTextAsync(Path.Combine(targetDir, "source.json"), JsonSerializer.Serialize(sourceMeta, PluginJsonContext.Default.PluginSourceMetadata), ct);

                    importedIds.Add(manifest.Id);
                }
                catch (Exception ex)
                {
                    errors.Add($"导入模块 '{modRelPath}' 失败: {ex.Message}");
                }
            }

            return new PluginBatchImportResult
            {
                ImportedCount = importedIds.Count,
                ImportedPluginIds = importedIds,
                Errors = errors
            };
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public async ValueTask<PluginSourceMetadata?> GetSourceMetadataAsync(string id, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return null;

        var dir = Path.Combine(_pluginsRoot, id);
        var manifestFile = Path.Combine(dir, "plugin.json");
        if (!File.Exists(manifestFile)) return null;

        var metaFile = Path.Combine(dir, "source.json");
        if (File.Exists(metaFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(metaFile, ct);
                var meta = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginSourceMetadata);
                if (meta != null) return meta;
            }
            catch { }
        }

        string version = "1.0.0";
        try
        {
            var manifestJson = await File.ReadAllTextAsync(manifestFile, ct);
            var manifest = JsonSerializer.Deserialize(manifestJson, PluginJsonContext.Default.PluginManifest);
            if (manifest != null) version = manifest.Version;
        }
        catch { }

        return new PluginSourceMetadata
        {
            SourceType = (id.StartsWith("demo-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("sample-", StringComparison.OrdinalIgnoreCase) || id.Equals("ssh-access-guard") || id.Equals("caddy-manager") || id.Equals("chatgpt-channel") || id.Equals("native-command-demo")) ? "builtin" : "custom",
            InstalledVersion = version,
            InstalledAt = File.GetCreationTimeUtc(manifestFile)
        };
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
            using var proc = Process.Start(new ProcessStartInfo
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
