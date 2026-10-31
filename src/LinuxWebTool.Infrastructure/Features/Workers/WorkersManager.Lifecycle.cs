using System.Text.Json;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Workers;

public sealed partial class WorkersManager
{
    public async ValueTask<bool> SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        var manifestFile = Path.Combine(dir, "worker.json");
        if (!File.Exists(manifestFile)) return false;

        try
        {
            var disabledMarker = Path.Combine(dir, ".disabled");

            if (!enabled)
            {
                await StopAsync(id, ct);
                if (!File.Exists(disabledMarker))
                {
                    await File.WriteAllTextAsync(disabledMarker, "disabled", ct);
                }
            }
            else
            {
                if (File.Exists(disabledMarker))
                {
                    File.Delete(disabledMarker);
                }
            }

            var json = await File.ReadAllTextAsync(manifestFile, ct);
            var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
            if (manifest != null)
            {
                var updated = manifest with { IsEnabled = enabled };
                var newJson = JsonSerializer.Serialize(updated, WorkerJsonContext.Default.WorkerManifest);
                await File.WriteAllTextAsync(manifestFile, newJson, ct);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新 Worker 启用状态失败: {Id}, Enabled: {Enabled}", id, enabled);
            return false;
        }
    }

    public async ValueTask<WorkerSourceMetadata?> GetSourceMetadataAsync(string id, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        var manifestFile = Path.Combine(dir, "worker.json");
        if (!File.Exists(manifestFile)) return null;

        var sourceFile = Path.Combine(dir, "source.json");
        if (File.Exists(sourceFile))
        {
            try
            {
                var json = await File.ReadAllTextAsync(sourceFile, ct);
                var meta = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerSourceMetadata);
                if (meta != null) return meta;
            }
            catch { }
        }

        string version = "1.0.0";
        try
        {
            var manifestJson = await File.ReadAllTextAsync(manifestFile, ct);
            var manifest = JsonSerializer.Deserialize(manifestJson, WorkerJsonContext.Default.WorkerManifest);
            if (manifest != null) version = manifest.Version;
        }
        catch { }

        return new WorkerSourceMetadata
        {
            SourceType = id.StartsWith("demo-", StringComparison.OrdinalIgnoreCase) ? "builtin" : "custom",
            InstalledVersion = version,
            InstalledAt = File.GetCreationTimeUtc(manifestFile)
        };
    }

    public async ValueTask<WorkerUpdateCheckResponse> CheckUpdateAsync(string id, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        var manifestFile = Path.Combine(dir, "worker.json");
        if (!File.Exists(manifestFile))
        {
            return new WorkerUpdateCheckResponse(false, "0.0.0", null, "Worker 不存在");
        }

        var json = await File.ReadAllTextAsync(manifestFile, ct);
        var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
        var currentVer = manifest?.Version ?? "1.0.0";

        var sourceMeta = await GetSourceMetadataAsync(id, ct);
        if (sourceMeta == null || string.IsNullOrWhiteSpace(sourceMeta.RepoUrl) || sourceMeta.SourceType != "git")
        {
            return new WorkerUpdateCheckResponse(false, currentVer, null, "该服务不是通过 Git 仓库导入，无法远程检测更新。");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_worker_chk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var normalizedUrl = NormalizeGitUrl(sourceMeta.RepoUrl);
            var cloneSuccess = await RunGitCloneAsync(normalizedUrl, tempDir, ct);
            if (!cloneSuccess)
            {
                return new WorkerUpdateCheckResponse(false, currentVer, null, "无法连接远程 Git 仓库进行版本比对");
            }

            var manifestFiles = Directory.GetFiles(tempDir, "worker.json", SearchOption.AllDirectories);
            WorkerManifest? remoteManifest = null;

            foreach (var mf in manifestFiles)
            {
                try
                {
                    var mJson = await File.ReadAllTextAsync(mf, ct);
                    var parsed = JsonSerializer.Deserialize(mJson, WorkerJsonContext.Default.WorkerManifest);
                    if (parsed != null && string.Equals(parsed.Id, id, StringComparison.OrdinalIgnoreCase))
                    {
                        remoteManifest = parsed;
                        break;
                    }
                }
                catch { }
            }

            if (remoteManifest == null)
            {
                return new WorkerUpdateCheckResponse(false, currentVer, null, "远程仓库中未检索到同名 Worker 配置");
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
                JsonSerializer.Serialize(updatedSource, WorkerJsonContext.Default.WorkerSourceMetadata),
                ct);

            return new WorkerUpdateCheckResponse(
                hasUpdate,
                currentVer,
                remoteManifest.Version,
                hasUpdate ? $"检测到新版本 v{remoteManifest.Version}" : "当前已是最新版本");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "检查 Worker {Id} 远程更新失败", id);
            return new WorkerUpdateCheckResponse(false, currentVer, null, $"检查失败: {ex.Message}");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    public async ValueTask<WorkerHotUpdateResponse> HotUpdateAsync(string id, WorkerHotUpdateRequest request, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        var manifestFile = Path.Combine(dir, "worker.json");
        if (!File.Exists(manifestFile))
        {
            return new WorkerHotUpdateResponse(false, "0.0.0", "0.0.0", "Worker 实例不存在");
        }

        var json = await File.ReadAllTextAsync(manifestFile, ct);
        var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
        var currentVer = manifest?.Version ?? "1.0.0";

        var sourceMeta = await GetSourceMetadataAsync(id, ct);
        if (sourceMeta == null || string.IsNullOrWhiteSpace(sourceMeta.RepoUrl) || sourceMeta.SourceType != "git")
        {
            return new WorkerHotUpdateResponse(false, currentVer, currentVer, "该服务不是通过 Git 仓库导入，无法执行在线热更新。");
        }

        var backupDir = Path.Combine(_workersRoot, $"{id}.backup_{DateTime.UtcNow.Ticks}");
        var tempDir = Path.Combine(Path.GetTempPath(), "lwt_worker_upd_" + Guid.NewGuid().ToString("N"));

        bool wasRunning = _activeProcesses.ContainsKey(id);

        try
        {
            // 1. 创建快照临时备份（排除持久化数据文件）
            Directory.CreateDirectory(backupDir);
            BackupWorkerFiles(dir, backupDir);

            // 2. 优雅停止正在运行的子进程
            if (wasRunning)
            {
                await StopAsync(id, ct);
            }

            // 3. 克隆并提取最新源码
            Directory.CreateDirectory(tempDir);
            var normalizedUrl = NormalizeGitUrl(sourceMeta.RepoUrl);
            var cloneSuccess = await RunGitCloneAsync(normalizedUrl, tempDir, ct);
            if (!cloneSuccess)
            {
                throw new InvalidOperationException("无法拉取远程 Git 仓库最新源码");
            }

            var manifestFiles = Directory.GetFiles(tempDir, "worker.json", SearchOption.AllDirectories);
            string? remoteModuleDir = null;
            WorkerManifest? remoteManifest = null;

            foreach (var mf in manifestFiles)
            {
                try
                {
                    var mJson = await File.ReadAllTextAsync(mf, ct);
                    var parsed = JsonSerializer.Deserialize(mJson, WorkerJsonContext.Default.WorkerManifest);
                    if (parsed != null && string.Equals(parsed.Id, id, StringComparison.OrdinalIgnoreCase))
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
                throw new InvalidOperationException("远程仓库中未检索到同名 Worker 配置");
            }

            // 4. 增量覆写源码文件（严禁覆写 data.db 与 kv.db）
            CopyDirectoryExcludingData(remoteModuleDir, dir);

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
                JsonSerializer.Serialize(updatedSource, WorkerJsonContext.Default.WorkerSourceMetadata),
                ct);

            // 6. 重启服务进入 2 秒看门狗存活校验
            if (wasRunning)
            {
                var startOk = await StartAsync(id, ct);
                if (startOk)
                {
                    await Task.Delay(2000, ct);
                    var isAlive = _activeProcesses.TryGetValue(id, out var procCtx) && !procCtx.Process.HasExited;
                    if (!isAlive)
                    {
                        startOk = false;
                    }
                }

                if (!startOk)
                {
                    // 7. 瞬崩安全自动回滚
                    _logger.LogWarning("Worker {Id} 热更新启动校验失败，触发原子自动回滚", id);
                    await StopAsync(id, ct);
                    CopyDirectoryExcludingData(backupDir, dir);
                    await StartAsync(id, ct);

                    return new WorkerHotUpdateResponse(
                        false,
                        currentVer,
                        remoteManifest.Version,
                        "新版本启动校验瞬崩，已自动安全回滚至上一稳定版本",
                        RollbackPerformed: true);
                }
            }

            // 8. 成功后安全清理备份
            try { Directory.Delete(backupDir, true); } catch { }
            return new WorkerHotUpdateResponse(
                true,
                currentVer,
                remoteManifest.Version,
                $"热更新成功已生效！(v{currentVer} -> v{remoteManifest.Version})");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Worker {Id} 热更新异常，执行回滚保护", id);
            try
            {
                if (Directory.Exists(backupDir))
                {
                    await StopAsync(id, ct);
                    CopyDirectoryExcludingData(backupDir, dir);
                    if (wasRunning) await StartAsync(id, ct);
                }
            }
            catch { }

            return new WorkerHotUpdateResponse(
                false,
                currentVer,
                currentVer,
                $"热更新异常并已回滚: {ex.Message}",
                RollbackPerformed: true);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            try { if (Directory.Exists(backupDir)) Directory.Delete(backupDir, true); } catch { }
        }
    }

    private static void BackupWorkerFiles(string sourceDir, string targetDir)
    {
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("data.db", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("kv.db", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            File.Copy(file, Path.Combine(targetDir, fileName), overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(sub);
            if (dirName.StartsWith(".backup", StringComparison.OrdinalIgnoreCase) ||
                dirName.Equals(".git", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var destSub = Path.Combine(targetDir, dirName);
            Directory.CreateDirectory(destSub);
            CopyDirectory(sub, destSub, overwrite: true);
        }
    }

    private static void CopyDirectoryExcludingData(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("data.db", StringComparison.OrdinalIgnoreCase) ||
                fileName.Equals("kv.db", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            File.Copy(file, Path.Combine(targetDir, fileName), overwrite: true);
        }

        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(sub);
            if (dirName.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                dirName.StartsWith(".backup", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var destSub = Path.Combine(targetDir, dirName);
            Directory.CreateDirectory(destSub);
            CopyDirectoryExcludingData(sub, destSub);
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