using System.Diagnostics;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Workers;

public sealed partial class WorkersManager
{
    private static readonly HashSet<string> AllowedEditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".js", ".mjs", ".cjs", ".ts", ".py", ".sh", ".bash", ".ps1", ".bat", ".cmd", ".cs", ".json", ".yaml", ".yml", ".txt", ".env", ".md"
    };

    public ValueTask<WorkerCodeDto> GetCodeAsync(string id, string? filePath = null, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"未找到 Worker 目录: {id}");
        }

        var availableFiles = GetSafeFileList(dir);
        var backups = GetBackupFileList(dir);

        var manifestFile = Path.Combine(dir, "worker.json");
        string defaultFile = "worker.json";
        if (File.Exists(manifestFile))
        {
            try
            {
                var json = File.ReadAllText(manifestFile);
                var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
                if (manifest != null && !string.IsNullOrWhiteSpace(manifest.Entrypoint))
                {
                    defaultFile = manifest.Entrypoint;
                }
            }
            catch { }
        }

        var targetRelPath = string.IsNullOrWhiteSpace(filePath) ? defaultFile : filePath.Trim().Replace('\\', '/');
        var safeFullPath = ValidateAndResolvePath(dir, targetRelPath);

        string content = "";
        if (File.Exists(safeFullPath))
        {
            var fileInfo = new FileInfo(safeFullPath);
            if (fileInfo.Length > 2 * 1024 * 1024)
            {
                content = $"// 文件过大 ({fileInfo.Length / 1024} KB)，在线编辑器仅支持 2MB 以内的文本文件。";
            }
            else
            {
                content = File.ReadAllText(safeFullPath);
            }
        }

        var result = new WorkerCodeDto
        {
            WorkerId = id,
            SelectedFile = targetRelPath,
            Content = content,
            AvailableFiles = availableFiles,
            Backups = backups
        };

        return ValueTask.FromResult(result);
    }

    public async ValueTask<bool> SaveCodeAsync(string id, WorkerCodeSaveRequest request, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"未找到 Worker 目录: {id}");
        }

        var targetRelPath = string.IsNullOrWhiteSpace(request.FilePath) ? "worker.js" : request.FilePath.Trim().Replace('\\', '/');
        var safeFullPath = ValidateAndResolvePath(dir, targetRelPath);

        // 备忘录模式：在覆盖写盘前创建时间戳快照
        if (File.Exists(safeFullPath))
        {
            try
            {
                var fileName = Path.GetFileName(safeFullPath);
                var backupDir = Path.Combine(dir, ".backups");
                Directory.CreateDirectory(backupDir);

                var backupFileName = $"{fileName}.bak_{DateTime.UtcNow:yyyyMMddHHmmss}";
                var backupPath = Path.Combine(backupDir, backupFileName);
                File.Copy(safeFullPath, backupPath, overwrite: true);

                // 只保留最近 5 份历史快照
                PruneOldBackups(backupDir, fileName, 5);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "为 Worker {Id} 文件 {File} 创建备忘录快照失败", id, targetRelPath);
            }
        }

        var parentDir = Path.GetDirectoryName(safeFullPath);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        await File.WriteAllTextAsync(safeFullPath, request.Content, ct);
        _logger.LogInformation("Worker {Id} 代码文件 {File} 保存成功", id, targetRelPath);

        if (request.RestartAfterSave)
        {
            try
            {
                if (_activeProcesses.ContainsKey(id))
                {
                    _logger.LogInformation("保存代码后自动热重启 Worker {Id}...", id);
                    await RestartAsync(id, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Worker {Id} 保存后自动重启异常", id);
            }
        }

        return true;
    }

    public async ValueTask<WorkerDebugRunResultDto> DebugRunAsync(string id, WorkerDebugRunRequest request, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        if (!Directory.Exists(dir))
        {
            return new WorkerDebugRunResultDto
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = $"未找到 Worker 目录: {id}",
                ElapsedMilliseconds = 0
            };
        }

        var manifestFile = Path.Combine(dir, "worker.json");
        if (!File.Exists(manifestFile))
        {
            return new WorkerDebugRunResultDto
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = "缺少 worker.json 清单文件",
                ElapsedMilliseconds = 0
            };
        }

        var json = await File.ReadAllTextAsync(manifestFile, ct);
        var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
        if (manifest == null)
        {
            return new WorkerDebugRunResultDto
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = "worker.json 格式错误",
                ElapsedMilliseconds = 0
            };
        }

        if (!_adapters.TryGetValue(manifest.Runtime, out var adapter))
        {
            return new WorkerDebugRunResultDto
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = $"不支持的运行时: {manifest.Runtime}",
                ElapsedMilliseconds = 0
            };
        }

        if (!adapter.CheckAvailable(out var missingReason, out _))
        {
            return new WorkerDebugRunResultDto
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = $"缺少执行运行时: {missingReason}",
                ElapsedMilliseconds = 0
            };
        }

        // 策略模式构建瞬态调试执行规格，分配隔离端口，绝对不注入 supervisor 进程池
        var debugPort = 19000 + (Math.Abs(id.GetHashCode()) % 8000);
        var spec = adapter.BuildStartSpec(manifest, dir, debugPort);

        var timeoutSec = Math.Clamp(request.TimeoutSeconds, 1, 30);
        var sw = Stopwatch.StartNew();

        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = spec.FileName,
                Arguments = string.IsNullOrWhiteSpace(request.Args) ? spec.Arguments : $"{spec.Arguments} {request.Args}",
                WorkingDirectory = spec.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var kvp in spec.EnvironmentVariables)
            {
                proc.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
            proc.StartInfo.EnvironmentVariables["DEBUG_MODE"] = "1";
            proc.StartInfo.EnvironmentVariables["LWT_DEBUG_INVOKE"] = "1";

            proc.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            var exited = await proc.WaitForExitAsync(cts.Token).ContinueWith(t => !t.IsCanceled && !t.IsFaulted, CancellationToken.None);

            if (!exited)
            {
                try { KillProcessTree(proc); } catch { }
                sw.Stop();
                return new WorkerDebugRunResultDto
                {
                    Success = false,
                    ExitCode = -1,
                    StandardOutput = "",
                    StandardError = $"调试执行超时 (硬限制 {timeoutSec} 秒)。",
                    ElapsedMilliseconds = sw.ElapsedMilliseconds
                };
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            sw.Stop();

            return new WorkerDebugRunResultDto
            {
                Success = proc.ExitCode == 0,
                ExitCode = proc.ExitCode,
                StandardOutput = stdout,
                StandardError = stderr,
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new WorkerDebugRunResultDto
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = $"调试执行已取消或超时 ({timeoutSec} 秒)。",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new WorkerDebugRunResultDto
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = $"启动调试进程异常: {ex.Message}",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
    }

    #region 私有辅助方法
    private static string ValidateAndResolvePath(string baseDir, string relativePath)
    {
        var cleanRel = relativePath.TrimStart('/', '\\').Replace("..", "");
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, cleanRel));
        var fullBase = Path.GetFullPath(baseDir);

        if (!fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("安全拦截：禁止通过相对路径逃逸出当前 Worker 目录！");
        }

        var ext = Path.GetExtension(fullPath);
        if (!string.IsNullOrEmpty(ext) && !AllowedEditableExtensions.Contains(ext))
        {
            throw new NotSupportedException($"不支持编辑文件扩展名: {ext}");
        }

        return fullPath;
    }

    private static List<string> GetSafeFileList(string dir)
    {
        var list = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(dir, file).Replace('\\', '/');
                if (rel.StartsWith(".backups/", StringComparison.OrdinalIgnoreCase) ||
                    rel.StartsWith(".git/", StringComparison.OrdinalIgnoreCase) ||
                    rel.EndsWith(".db", StringComparison.OrdinalIgnoreCase) ||
                    rel.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ext = Path.GetExtension(file);
                if (AllowedEditableExtensions.Contains(ext))
                {
                    list.Add(rel);
                }
            }
        }
        catch { }
        list.Sort();
        return list;
    }

    private static List<string> GetBackupFileList(string dir)
    {
        var list = new List<string>();
        var backupDir = Path.Combine(dir, ".backups");
        if (Directory.Exists(backupDir))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(backupDir, "*", SearchOption.TopDirectoryOnly))
                {
                    list.Add(Path.GetFileName(file));
                }
            }
            catch { }
        }
        list.Sort((a, b) => string.Compare(b, a, StringComparison.Ordinal));
        return list;
    }

    private static void PruneOldBackups(string backupDir, string originalFileName, int maxBackups)
    {
        try
        {
            var files = Directory.GetFiles(backupDir, $"{originalFileName}.bak_*")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.CreationTimeUtc)
                .Skip(maxBackups)
                .ToList();

            foreach (var f in files)
            {
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }
    #endregion

    public async ValueTask<WorkerInstanceDto> DeploySingleFileAsync(WorkerQuickDeployRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.FileName)) throw new ArgumentException("文件名不能为空", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Content)) throw new ArgumentException("脚本内容不能为空", nameof(request));

        var meta = SingleFileHeaderParser.Parse(request.Content, request.FileName);
        var workerId = !string.IsNullOrWhiteSpace(request.WorkerId)
            ? request.WorkerId.Trim().ToLowerInvariant()
            : Path.GetFileNameWithoutExtension(request.FileName).Trim().ToLowerInvariant().Replace(' ', '-');

        workerId = new string(workerId.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (string.IsNullOrEmpty(workerId)) workerId = "quick-" + Guid.NewGuid().ToString("N")[..8];

        var runtimeType = meta.Runtime switch
        {
            "node" => WorkerRuntimeType.Node,
            "python" => WorkerRuntimeType.Python,
            "bash" => WorkerRuntimeType.Bash,
            "powershell" => WorkerRuntimeType.PowerShell,
            "batch" => WorkerRuntimeType.Batch,
            "dotnet" => WorkerRuntimeType.DotNet,
            _ => WorkerRuntimeType.Node
        };

        var entryFileName = Path.GetFileName(request.FileName);
        var createReq = new WorkerCreateOrUpdateRequest
        {
            Id = workerId,
            Name = meta.Name,
            Description = string.IsNullOrWhiteSpace(meta.Description) ? $"单文件快速部署脚本 ({entryFileName})" : meta.Description,
            Runtime = runtimeType,
            Mode = meta.Mode,
            Entrypoint = entryFileName,
            ScriptContent = request.Content,
            AutoRestart = true
        };

        var created = await CreateOrUpdateAsync(createReq, ct);
        if (request.AutoStart)
        {
            try
            {
                await StartAsync(workerId, ct);
                var latest = await GetByIdAsync(workerId, ct);
                if (latest != null) return latest;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Worker {Id} 单文件快速部署后自动启动异常，保持就绪状态", workerId);
            }
        }
        return created;
    }
}
