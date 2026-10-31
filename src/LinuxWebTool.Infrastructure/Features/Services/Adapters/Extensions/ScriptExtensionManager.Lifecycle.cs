using System.Text.Json;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

public sealed partial class ScriptExtensionManager
{
    public async Task<bool> SetEnabledAsync(string extensionId, bool enabled, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, extensionId);
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath)) return false;

        try
        {
            var disabledMarker = Path.Combine(dir, ".disabled");
            if (!enabled)
            {
                // 若正在运行，尝试执行 stop 钩子
                try
                {
                    await RunHookScriptAsync(dir, "stop", null, cancellationToken);
                }
                catch { }

                if (!File.Exists(disabledMarker))
                {
                    await File.WriteAllTextAsync(disabledMarker, "disabled", cancellationToken);
                }
            }
            else
            {
                if (File.Exists(disabledMarker))
                {
                    File.Delete(disabledMarker);
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新扩展服务启用状态失败: {Id}, Enabled: {Enabled}", extensionId, enabled);
            return false;
        }
    }

    public async Task<ExtensionSourceMetadata?> GetSourceMetadataAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, extensionId);
        if (!Directory.Exists(dir)) return null;

        var sourceFile = Path.Combine(dir, "source.json");
        if (File.Exists(sourceFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(sourceFile, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new ExtensionSourceMetadata
                {
                    SourceType = root.TryGetProperty("sourceType", out var st) ? st.GetString() ?? "git" : "git",
                    RepoUrl = root.TryGetProperty("repoUrl", out var ru) ? ru.GetString() : null,
                    SubDirectory = root.TryGetProperty("subDirectory", out var sd) ? sd.GetString() : null,
                    InstalledVersion = root.TryGetProperty("installedVersion", out var iv) ? iv.GetString() ?? "1.0.0" : "1.0.0",
                    InstalledCommitHash = root.TryGetProperty("installedCommitHash", out var ich) ? ich.GetString() : null,
                    RemoteVersion = root.TryGetProperty("remoteVersion", out var rv) ? rv.GetString() : null,
                    HasUpdate = root.TryGetProperty("hasUpdate", out var hu) && hu.GetBoolean()
                };
            }
            catch { }
        }

        var legacyFile = Path.Combine(dir, ".lwt-source.json");
        if (File.Exists(legacyFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(legacyFile, cancellationToken);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                return new ExtensionSourceMetadata
                {
                    SourceType = "git",
                    RepoUrl = root.TryGetProperty("repo", out var r) ? r.GetString() : null,
                    InstalledVersion = "1.0.0"
                };
            }
            catch { }
        }

        var manifestPath = Path.Combine(dir, "manifest.json");
        string ver = "1.0.0";
        if (File.Exists(manifestPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var m = ParseManifest(json, dir);
                if (m != null) ver = m.Version;
            }
            catch { }
        }

        return new ExtensionSourceMetadata
        {
            SourceType = extensionId.Equals("ssh-access-guard", StringComparison.OrdinalIgnoreCase) ||
                         extensionId.Equals("caddy-server-manager", StringComparison.OrdinalIgnoreCase) ||
                         extensionId.Equals("process-top-monitor", StringComparison.OrdinalIgnoreCase)
                ? "builtin" : "custom",
            InstalledVersion = ver
        };
    }

    public async Task<ExtensionUpdateCheckResponse> CheckUpdateAsync(string extensionId, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, extensionId);
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new ExtensionUpdateCheckResponse(false, "0.0.0", null, "扩展实例不存在");
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = ParseManifest(json, dir);
        var currentVer = manifest?.Version ?? "1.0.0";

        var sourceMeta = await GetSourceMetadataAsync(extensionId, cancellationToken);
        if (sourceMeta == null || string.IsNullOrWhiteSpace(sourceMeta.RepoUrl) || sourceMeta.SourceType != "git")
        {
            return new ExtensionUpdateCheckResponse(false, currentVer, null, "该服务不是通过 Git 仓库导入，无法远程检测更新。");
        }

        var (fetchOk, fetchMsg, repoDir) = await _repoFetcher.FetchRepositoryAsync(
            sourceMeta.RepoUrl,
            null,
            null,
            cancellationToken);

        if (!fetchOk || string.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir))
        {
            return new ExtensionUpdateCheckResponse(false, currentVer, null, $"拉取远程仓库失败: {fetchMsg}");
        }

        try
        {
            var manifestFiles = Directory.EnumerateFiles(repoDir, "manifest.json", SearchOption.AllDirectories);
            ExtensionManifest? remoteManifest = null;

            foreach (var mf in manifestFiles)
            {
                try
                {
                    var mJson = await File.ReadAllTextAsync(mf, cancellationToken);
                    var parsed = ParseManifest(mJson, Path.GetDirectoryName(mf)!);
                    if (parsed != null && string.Equals(parsed.Id, extensionId, StringComparison.OrdinalIgnoreCase))
                    {
                        remoteManifest = parsed;
                        break;
                    }
                }
                catch { }
            }

            if (remoteManifest == null)
            {
                return new ExtensionUpdateCheckResponse(false, currentVer, null, "远程仓库中未检索到同名扩展模块");
            }

            var hasUpdate = IsNewerVersion(remoteManifest.Version, currentVer);
            var updatedSource = sourceMeta with
            {
                RemoteVersion = remoteManifest.Version,
                LastCheckedAt = DateTime.UtcNow,
                HasUpdate = hasUpdate
            };

            await File.WriteAllTextAsync(
                Path.Combine(dir, "source.json"),
                JsonSerializer.Serialize(updatedSource, ServiceJsonContext.Default.ExtensionSourceMetadata),
                cancellationToken);

            return new ExtensionUpdateCheckResponse(
                hasUpdate,
                currentVer,
                remoteManifest.Version,
                hasUpdate ? $"检测到新版本 v{remoteManifest.Version}" : "当前已是最新版本");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查扩展 {Id} 远程更新异常", extensionId);
            return new ExtensionUpdateCheckResponse(false, currentVer, null, $"检查异常: {ex.Message}");
        }
    }

    public async Task<ExtensionHotUpdateResponse> HotUpdateAsync(string extensionId, ExtensionHotUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, extensionId);
        var manifestPath = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            return new ExtensionHotUpdateResponse(false, "0.0.0", "0.0.0", "扩展实例不存在");
        }

        var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        var manifest = ParseManifest(json, dir);
        var currentVer = manifest?.Version ?? "1.0.0";

        var sourceMeta = await GetSourceMetadataAsync(extensionId, cancellationToken);
        if (sourceMeta == null || string.IsNullOrWhiteSpace(sourceMeta.RepoUrl) || sourceMeta.SourceType != "git")
        {
            return new ExtensionHotUpdateResponse(false, currentVer, currentVer, "该服务无 Git 远程源，无法在线热更新");
        }

        var backupDir = Path.Combine(_baseExtensionsDir, $"{extensionId}.backup_{DateTime.UtcNow.Ticks}");

        try
        {
            // 1. 备份现有扩展
            Directory.CreateDirectory(backupDir);
            CopyDirectoryRecursive(dir, backupDir);

            // 2. 拉取最新仓库
            var (fetchOk, fetchMsg, repoDir) = await _repoFetcher.FetchRepositoryAsync(
                sourceMeta.RepoUrl,
                null,
                null,
                cancellationToken);

            if (!fetchOk || string.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir))
            {
                throw new InvalidOperationException($"拉取远程仓库失败: {fetchMsg}");
            }

            var manifestFiles = Directory.EnumerateFiles(repoDir, "manifest.json", SearchOption.AllDirectories);
            string? remoteModuleDir = null;
            ExtensionManifest? remoteManifest = null;

            foreach (var mf in manifestFiles)
            {
                try
                {
                    var mJson = await File.ReadAllTextAsync(mf, cancellationToken);
                    var parsed = ParseManifest(mJson, Path.GetDirectoryName(mf)!);
                    if (parsed != null && string.Equals(parsed.Id, extensionId, StringComparison.OrdinalIgnoreCase))
                    {
                        remoteManifest = parsed;
                        remoteModuleDir = Path.GetDirectoryName(mf);
                        break;
                    }
                }
                catch { }
            }

            if (remoteModuleDir == null || remoteManifest == null)
            {
                throw new InvalidOperationException("远程仓库中未检索到同名扩展配置");
            }

            // 3. 保护用户原有配置 config.json
            string? existingConfigJson = null;
            var configPath = Path.Combine(dir, "config.json");
            if (File.Exists(configPath))
            {
                try { existingConfigJson = await File.ReadAllTextAsync(configPath, cancellationToken); } catch { }
            }

            // 4. 覆盖安装
            CopyDirectoryRecursive(remoteModuleDir, dir);

            if (!string.IsNullOrEmpty(existingConfigJson))
            {
                await File.WriteAllTextAsync(configPath, existingConfigJson, cancellationToken);
            }

            // 5. 更新 source.json
            var updatedSource = sourceMeta with
            {
                InstalledVersion = remoteManifest.Version,
                RemoteVersion = remoteManifest.Version,
                LastCheckedAt = DateTime.UtcNow,
                HasUpdate = false
            };
            await File.WriteAllTextAsync(
                Path.Combine(dir, "source.json"),
                JsonSerializer.Serialize(updatedSource, ServiceJsonContext.Default.ExtensionSourceMetadata),
                cancellationToken);

            // 6. 验证 manifest 是否健全
            var testJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            if (ParseManifest(testJson, dir) == null)
            {
                throw new InvalidOperationException("新版本 manifest.json 校验失败，触发回滚");
            }

            // 7. 成功后删除备份
            try { Directory.Delete(backupDir, true); } catch { }

            return new ExtensionHotUpdateResponse(
                true,
                currentVer,
                remoteManifest.Version,
                $"热更新成功已生效！(v{currentVer} -> v{remoteManifest.Version})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "扩展 {Id} 热更新失败，执行安全回滚", extensionId);
            try
            {
                if (Directory.Exists(backupDir))
                {
                    CopyDirectoryRecursive(backupDir, dir);
                }
            }
            catch { }

            return new ExtensionHotUpdateResponse(
                false,
                currentVer,
                currentVer,
                $"热更新失败并已回滚: {ex.Message}",
                RollbackPerformed: true);
        }
        finally
        {
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true); } catch { }
        }
    }

    private static bool IsNewerVersion(string remoteVer, string currentVer)
    {
        if (Version.TryParse(remoteVer.TrimStart('v', 'V'), out var rv) &&
            Version.TryParse(currentVer.TrimStart('v', 'V'), out var cv))
        {
            return rv > cv;
        }
        return !string.Equals(remoteVer.Trim(), currentVer.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
