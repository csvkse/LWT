using System.Diagnostics;
using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

public sealed partial class ScriptExtensionManager
{
    private static readonly HashSet<string> AllowedEditableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".sh", ".bash", ".ps1", ".bat", ".cmd", ".js", ".py", ".json", ".yaml", ".yml", ".txt", ".md", ".env", ".conf", ".ini"
    };

    public Task<ExtensionCodeDto> GetCodeAsync(string extensionId, string? filePath = null, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, extensionId);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"未找到扩展服务目录: {extensionId}");
        }

        var availableFiles = GetSafeFileList(dir);
        var backups = GetBackupFileList(dir);

        string defaultFile = "manifest.json";
        var isWindows = OperatingSystem.IsWindows();
        var preferredHooks = isWindows
            ? new[] { "hooks/start.ps1", "hooks/status.ps1", "hooks/start.bat", "hooks/start.js", "start.ps1", "manifest.json" }
            : new[] { "hooks/start.sh", "hooks/status.sh", "hooks/start.py", "hooks/start.js", "start.sh", "manifest.json" };

        foreach (var hook in preferredHooks)
        {
            if (File.Exists(Path.Combine(dir, hook.Replace('/', Path.DirectorySeparatorChar))))
            {
                defaultFile = hook;
                break;
            }
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

        var dto = new ExtensionCodeDto
        {
            ExtensionId = extensionId,
            SelectedFile = targetRelPath,
            Content = content,
            AvailableFiles = availableFiles,
            Backups = backups
        };

        return Task.FromResult(dto);
    }

    public async Task<bool> SaveCodeAsync(string extensionId, ExtensionCodeSaveRequest request, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, extensionId);
        if (!Directory.Exists(dir))
        {
            throw new DirectoryNotFoundException($"未找到扩展服务目录: {extensionId}");
        }

        var targetRelPath = string.IsNullOrWhiteSpace(request.FilePath) ? "manifest.json" : request.FilePath.Trim().Replace('\\', '/');
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

                PruneOldBackups(backupDir, fileName, 5);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "为扩展 {Id} 文件 {File} 创建备忘录快照失败", extensionId, targetRelPath);
            }
        }

        var parentDir = Path.GetDirectoryName(safeFullPath);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        await File.WriteAllTextAsync(safeFullPath, request.Content, cancellationToken);
        _logger.LogInformation("扩展服务 {Id} 脚本文件 {File} 保存成功", extensionId, targetRelPath);

        return true;
    }

    public async Task<ExtensionDebugRunResultDto> DebugRunAsync(string extensionId, ExtensionDebugRunRequest request, CancellationToken cancellationToken = default)
    {
        var dir = Path.Combine(_baseExtensionsDir, extensionId);
        if (!Directory.Exists(dir))
        {
            return new ExtensionDebugRunResultDto
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = $"未找到扩展服务目录: {extensionId}",
                ElapsedMilliseconds = 0
            };
        }

        var hookName = string.IsNullOrWhiteSpace(request.HookName) ? "status" : request.HookName.Trim();
        var sw = Stopwatch.StartNew();

        try
        {
            var (exitCode, stdout, stderr) = await RunHookScriptAsync(dir, hookName, request.Params, cancellationToken, request.Target);
            sw.Stop();

            return new ExtensionDebugRunResultDto
            {
                Success = exitCode == 0,
                ExitCode = exitCode,
                StandardOutput = stdout,
                StandardError = stderr,
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ExtensionDebugRunResultDto
            {
                Success = false,
                ExitCode = -1,
                StandardOutput = "",
                StandardError = $"调试执行钩子脚本异常: {ex.Message}",
                ElapsedMilliseconds = sw.ElapsedMilliseconds
            };
        }
    }

    public async Task<ServiceActionResult> CreateExtensionAsync(ExtensionCreateRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Id) || !Regex.IsMatch(request.Id, "^[a-zA-Z0-9_-]+$"))
        {
            return new ServiceActionResult { Success = false, Message = "扩展 ID 必须由字母、数字、下划线或连字符组成" };
        }

        var dir = Path.Combine(_baseExtensionsDir, request.Id);
        if (Directory.Exists(dir))
        {
            return new ServiceActionResult { Success = false, Message = $"扩展服务 '{request.Id}' 已存在" };
        }

        Directory.CreateDirectory(dir);
        var hooksDir = Path.Combine(dir, "hooks");
        Directory.CreateDirectory(hooksDir);

        var runner = string.IsNullOrWhiteSpace(request.Runner) ? "bash" : request.Runner.ToLowerInvariant();
        var isWindows = OperatingSystem.IsWindows();

        var manifest = new ExtensionManifest
        {
            Id = request.Id,
            Name = request.Name,
            Description = request.Description ?? "自定义运维扩展服务",
            Version = "1.0.0",
            SupportedPlatforms = ["linux", "windows"],
            Target = string.IsNullOrWhiteSpace(request.Target) ? "container" : request.Target.Trim().ToLowerInvariant(),
            Dependencies = new ExtensionDependencies
            {
                Runners = runner switch
                {
                    "pwsh" or "powershell" => ["powershell"],
                    "python" or "python3" => ["python3"],
                    "node" or "js" => ["node"],
                    _ => ["bash"]
                }
            }
        };

        var manifestJson = System.Text.Json.JsonSerializer.Serialize(manifest, ServiceJsonContext.Default.ExtensionManifest);
        await File.WriteAllTextAsync(Path.Combine(dir, "manifest.json"), manifestJson, cancellationToken);

        // 创建默认 start 与 status 钩子脚本（跨平台双规兼容）
        var startPs1 = request.StartScript ?? "Write-Host 'Service started successfully.'";
        var statusPs1 = "Write-Output '{\"status\":\"Running\",\"message\":\"Service is active\"}'";
        var startSh = request.StartScript ?? "#!/bin/bash\necho \"Service started successfully.\"";
        var statusSh = "#!/bin/bash\necho '{\"status\":\"Running\",\"message\":\"Service is active\"}'";

        if (runner is "pwsh" or "powershell")
        {
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "start.ps1"), startPs1, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "status.ps1"), statusPs1, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "start.sh"), startSh, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "status.sh"), statusSh, cancellationToken);
        }
        else if (runner is "node" or "js")
        {
            var startJs = request.StartScript ?? "console.log('Service started successfully.');";
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "start.js"), startJs, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "status.js"),
                "console.log(JSON.stringify({status:'Running',message:'Service is active'}));", cancellationToken);
        }
        else if (runner is "python" or "python3")
        {
            var startPy = request.StartScript ?? "print('Service started successfully.')";
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "start.py"), startPy, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "status.py"),
                "import json; print(json.dumps({'status':'Running','message':'Service is active'}))", cancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "start.sh"), startSh, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "status.sh"), statusSh, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "start.ps1"), startPs1, cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(hooksDir, "status.ps1"), statusPs1, cancellationToken);
        }

        // 写入初始来源信息
        var sourceMeta = new ExtensionSourceMetadata
        {
            SourceType = "custom",
            InstalledVersion = "1.0.0",
            InstalledAt = DateTime.UtcNow
        };
        await File.WriteAllTextAsync(Path.Combine(dir, "source.json"),
            System.Text.Json.JsonSerializer.Serialize(sourceMeta, ServiceJsonContext.Default.ExtensionSourceMetadata),
            cancellationToken);

        return new ServiceActionResult { Success = true, Message = $"扩展服务 '{request.Id}' 创建成功" };
    }

    #region 私有辅助方法
    private static string ValidateAndResolvePath(string baseDir, string relativePath)
    {
        var cleanRel = relativePath.TrimStart('/', '\\').Replace("..", "");
        var fullPath = Path.GetFullPath(Path.Combine(baseDir, cleanRel));
        var fullBase = Path.GetFullPath(baseDir);

        if (!fullPath.StartsWith(fullBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("安全拦截：禁止通过相对路径逃逸出当前扩展目录！");
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
                    rel.StartsWith(".disabled", StringComparison.OrdinalIgnoreCase))
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
}
