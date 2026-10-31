using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

public sealed partial class ScriptExtensionManager
{
    private static readonly ConcurrentDictionary<string, (string Dir, DateTime ExpireAt)> CachedRepoDirs = new();

    #region Mono-repo & GitHub 仓库扫描与批量导入
    public async Task<InspectRepoModulesResponse> InspectRepositoryModulesAsync(InspectRepoModulesRequest request, CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.TargetType, "worker", StringComparison.OrdinalIgnoreCase))
        {
            return new InspectRepoModulesResponse
            {
                RepositoryUrl = request.RepositoryUrl,
                Branch = request.Branch ?? "main",
                CommitHash = null,
                Modules = []
            };
        }

        var (fetchSuccess, fetchMsg, repoDir) = await _repoFetcher.FetchRepositoryAsync(
            request.RepositoryUrl,
            request.Branch,
            request.ProxyPrefix,
            cancellationToken);

        if (!fetchSuccess || string.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir))
        {
            return new InspectRepoModulesResponse
            {
                RepositoryUrl = request.RepositoryUrl,
                Branch = request.Branch ?? "main",
                CommitHash = null,
                Modules = []
            };
        }

        CachedRepoDirs[request.RepositoryUrl] = (repoDir, DateTime.UtcNow.AddMinutes(15));

        var currentOs = OperatingSystem.IsWindows() ? "windows" : (OperatingSystem.IsLinux() ? "linux" : "macos");
        var discovered = new List<DiscoveredModuleItem>();

        var manifestFiles = Directory.EnumerateFiles(repoDir, "manifest.json", SearchOption.AllDirectories);

        foreach (var mFile in manifestFiles)
        {
            try
            {
                var mDir = Path.GetDirectoryName(mFile)!;
                var relPath = Path.GetRelativePath(repoDir, mDir).Replace('\\', '/');

                var json = await File.ReadAllTextAsync(mFile, cancellationToken);
                var manifest = ParseManifest(json, mDir);
                if (manifest == null) continue;

                var isCompatible = manifest.SupportedPlatforms.Contains(currentOs, StringComparer.OrdinalIgnoreCase);
                var missingRunners = ProbeMissingRunners(manifest.Dependencies.Runners);
                var alreadyInstalled = Directory.Exists(Path.Combine(_baseExtensionsDir, manifest.Id));

                discovered.Add(new DiscoveredModuleItem
                {
                    RelativePath = relPath,
                    Manifest = manifest,
                    IsCompatibleWithCurrentOs = isCompatible,
                    HasMissingRunners = missingRunners.Count > 0,
                    MissingRunners = missingRunners,
                    AlreadyInstalled = alreadyInstalled
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inspect manifest at {File}", mFile);
            }
        }

        return new InspectRepoModulesResponse
        {
            RepositoryUrl = request.RepositoryUrl,
            Branch = request.Branch ?? "main",
            CommitHash = "HEAD",
            Modules = discovered
        };
    }

    public async Task<BatchImportModulesResult> BatchImportModulesAsync(BatchImportModulesRequest request, CancellationToken cancellationToken = default)
    {
        string? repoDir = null;
        if (CachedRepoDirs.TryGetValue(request.RepositoryUrl, out var entry) && Directory.Exists(entry.Dir) && entry.ExpireAt > DateTime.UtcNow)
        {
            repoDir = entry.Dir;
        }
        else
        {
            var (fetchSuccess, fetchMsg, fetchedDir) = await _repoFetcher.FetchRepositoryAsync(
                request.RepositoryUrl,
                request.Branch,
                request.ProxyPrefix,
                cancellationToken);
            if (!fetchSuccess || string.IsNullOrEmpty(fetchedDir))
            {
                return new BatchImportModulesResult { Success = false, Message = $"拉取仓库失败: {fetchMsg}" };
            }
            repoDir = fetchedDir;
        }

        var importedIds = new List<string>();
        var errors = new List<string>();

        foreach (var relPath in request.SelectedModulePaths)
        {
            try
            {
                if (relPath.Contains(".."))
                {
                    errors.Add($"拒绝非法相对路径: {relPath}");
                    continue;
                }

                var sourceModuleDir = Path.GetFullPath(Path.Combine(repoDir, relPath));
                if (!sourceModuleDir.StartsWith(Path.GetFullPath(repoDir), StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"路径逃逸阻断: {relPath}");
                    continue;
                }

                var manifestFile = Path.Combine(sourceModuleDir, "manifest.json");
                if (!File.Exists(manifestFile))
                {
                    errors.Add($"模块目录未包含 manifest.json: {relPath}");
                    continue;
                }

                var json = await File.ReadAllTextAsync(manifestFile, cancellationToken);
                var manifest = ParseManifest(json, sourceModuleDir);
                if (manifest == null)
                {
                    errors.Add($"无法解析 manifest.json: {relPath}");
                    continue;
                }

                var targetDir = Path.Combine(_baseExtensionsDir, manifest.Id);
                if (!Path.GetFullPath(targetDir).StartsWith(Path.GetFullPath(_baseExtensionsDir), StringComparison.OrdinalIgnoreCase))
                {
                    errors.Add($"扩展落盘路径不合法: {manifest.Id}");
                    continue;
                }

                string? existingConfigJson = null;
                var existingConfigFile = Path.Combine(targetDir, "config.json");
                if (File.Exists(existingConfigFile))
                {
                    try { existingConfigJson = await File.ReadAllTextAsync(existingConfigFile, cancellationToken); } catch { }
                }

                if (Directory.Exists(targetDir))
                {
                    if (!request.OverwriteExisting)
                    {
                        errors.Add($"扩展 {manifest.Id} 已存在且跳过覆写");
                        continue;
                    }
                    Directory.Delete(targetDir, recursive: true);
                }

                Directory.CreateDirectory(targetDir);
                CopyDirectoryRecursive(sourceModuleDir, targetDir);

                if (!string.IsNullOrEmpty(existingConfigJson))
                {
                    await File.WriteAllTextAsync(existingConfigFile, existingConfigJson, cancellationToken);
                }

                var sourceMeta = new ExtensionSourceMetadata
                {
                    SourceType = "git",
                    RepoUrl = request.RepositoryUrl,
                    SubDirectory = relPath,
                    InstalledVersion = manifest.Version,
                    InstalledCommitHash = "HEAD",
                    InstalledAt = DateTime.UtcNow
                };
                var sourceMetaFile = Path.Combine(targetDir, "source.json");
                await File.WriteAllTextAsync(sourceMetaFile, JsonSerializer.Serialize(sourceMeta, ServiceJsonContext.Default.ExtensionSourceMetadata), cancellationToken);

                // 兼容旧版 .lwt-source.json
                var legacySourceMetaFile = Path.Combine(targetDir, ".lwt-source.json");
                var legacyMeta = $"{{\"repo\":\"{EscapeJson(request.RepositoryUrl)}\",\"branch\":\"{EscapeJson(request.Branch ?? "main")}\",\"importedAt\":\"{DateTime.UtcNow:O}\"}}";
                await File.WriteAllTextAsync(legacySourceMetaFile, legacyMeta, cancellationToken);

                importedIds.Add(manifest.Id);
            }
            catch (Exception ex)
            {
                errors.Add($"导入模块 {relPath} 失败: {ex.Message}");
            }
        }

        return new BatchImportModulesResult
        {
            Success = importedIds.Count > 0,
            Message = importedIds.Count > 0 ? $"成功导入 {importedIds.Count} 个扩展" : "未能成功导入任何扩展",
            ImportedExtensionIds = importedIds,
            Errors = errors
        };
    }

    public async Task<BatchImportModulesResult> ImportZipAsync(Stream zipStream, CancellationToken cancellationToken = default)
    {
        var tempExtractDir = Path.Combine(Path.GetTempPath(), "lwt_ext_zip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempExtractDir);

        var importedIds = new List<string>();
        var errors = new List<string>();

        try
        {
            using (var archive = new System.IO.Compression.ZipArchive(zipStream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: false))
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
                        throw new System.Security.SecurityException("非法压缩包：检测到潜在的 Zip Slip 路径穿越安全风险！");
                    }

                    var parentDir = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(parentDir))
                    {
                        Directory.CreateDirectory(parentDir);
                    }

                    entry.ExtractToFile(destinationPath, overwrite: true);
                }
            }

            var manifestFiles = Directory.GetFiles(tempExtractDir, "manifest.json", SearchOption.AllDirectories);
            if (manifestFiles.Length == 0)
            {
                return new BatchImportModulesResult
                {
                    Success = false,
                    Message = "导入失败：Zip 压缩包内未找到有效的 manifest.json 清单配置文件。",
                    Errors = ["未找到 manifest.json"]
                };
            }

            foreach (var mFile in manifestFiles)
            {
                var sourceDir = Path.GetDirectoryName(mFile)!;
                try
                {
                    var json = await File.ReadAllTextAsync(mFile, cancellationToken);
                    var manifest = ParseManifest(json, sourceDir);
                    if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                    {
                        errors.Add($"manifest.json 格式无效: {mFile}");
                        continue;
                    }

                    var targetDir = Path.Combine(_baseExtensionsDir, manifest.Id);

                    // 保护已有用户的 config.json
                    string? existingConfigJson = null;
                    var existingConfigFile = Path.Combine(targetDir, "config.json");
                    if (File.Exists(existingConfigFile))
                    {
                        try { existingConfigJson = await File.ReadAllTextAsync(existingConfigFile, cancellationToken); } catch { }
                    }

                    if (Directory.Exists(targetDir))
                    {
                        Directory.Delete(targetDir, recursive: true);
                    }

                    Directory.CreateDirectory(targetDir);
                    CopyDirectoryRecursive(sourceDir, targetDir);

                    if (!string.IsNullOrEmpty(existingConfigJson))
                    {
                        await File.WriteAllTextAsync(existingConfigFile, existingConfigJson, cancellationToken);
                    }

                    var sourceMeta = new ExtensionSourceMetadata
                    {
                        SourceType = "zip",
                        InstalledVersion = manifest.Version,
                        InstalledAt = DateTime.UtcNow
                    };
                    var sourceMetaFile = Path.Combine(targetDir, "source.json");
                    await File.WriteAllTextAsync(sourceMetaFile, JsonSerializer.Serialize(sourceMeta, ServiceJsonContext.Default.ExtensionSourceMetadata), cancellationToken);

                    importedIds.Add(manifest.Id);
                }
                catch (Exception ex)
                {
                    errors.Add($"导入扩展模块失败 ({Path.GetFileName(sourceDir)}): {ex.Message}");
                }
            }

            return new BatchImportModulesResult
            {
                Success = importedIds.Count > 0,
                Message = importedIds.Count > 0 ? $"成功导入 {importedIds.Count} 个扩展" : "未能成功导入任何扩展",
                ImportedExtensionIds = importedIds,
                Errors = errors
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempExtractDir))
                {
                    Directory.Delete(tempExtractDir, recursive: true);
                }
            }
            catch { }
        }
    }
    #endregion
}
