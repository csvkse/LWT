using System.Diagnostics;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager
{
    private const long MaxCodeSizeBytes = 2 * 1024 * 1024; // 2MB 代码编辑限制

    private static readonly HashSet<string> KnownBinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".so", ".bin", ".dylib", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".wasm", ".zip", ".gz", ".tar", ".7z", ".mp3", ".mp4", ".node", ".pyc"
    };

    private static bool IsBinaryFile(string path, long fileLength)
    {
        if (fileLength > MaxCodeSizeBytes) return true;
        var ext = Path.GetExtension(path);
        if (KnownBinaryExtensions.Contains(ext)) return true;
        try
        {
            using var fs = File.OpenRead(path);
            var buffer = new byte[Math.Min(1024, fs.Length)];
            var read = fs.Read(buffer, 0, buffer.Length);
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == 0) return true;
            }
        }
        catch { }
        return false;
    }

    public async ValueTask<PluginCodeDto> GetCodeAsync(string id, string? filePath = null, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) throw new ArgumentException($"非法插件 ID: {id}");

        var dir = Path.Combine(_pluginsRoot, id);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"未找到插件目录: {id}");

        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".git") && !f.Contains("node_modules") && !f.Contains("__pycache__") && !f.Contains(".venv"))
            .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
            .OrderBy(f => f)
            .ToList();

        var targetRel = filePath;
        if (string.IsNullOrWhiteSpace(targetRel))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (File.Exists(manifestPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(manifestPath, ct);
                    var manifest = JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest);
                    targetRel = manifest?.Entrypoint;
                }
                catch { }
            }
            if (string.IsNullOrWhiteSpace(targetRel) || !File.Exists(Path.Combine(dir, targetRel)))
            {
                targetRel = files.FirstOrDefault(f => f.EndsWith(".js") || f.EndsWith(".py") || f.EndsWith(".sh") || f.EndsWith(".ps1") || f.EndsWith(".cs")) ?? "plugin.json";
            }
        }

        var fullTarget = Path.GetFullPath(Path.Combine(dir, targetRel));
        if (!fullTarget.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("禁止访问插件目录外部路径");
        }

        var isBinary = false;
        long fileSizeBytes = 0;
        var content = "";
        if (File.Exists(fullTarget))
        {
            var fileInfo = new FileInfo(fullTarget);
            fileSizeBytes = fileInfo.Length;
            isBinary = IsBinaryFile(fullTarget, fileSizeBytes);
            if (!isBinary)
            {
                content = await File.ReadAllTextAsync(fullTarget, ct);
            }
        }

        var bakPath = fullTarget + ".bak";
        var hasBackup = File.Exists(bakPath);
        DateTimeOffset? backupTs = hasBackup ? new FileInfo(bakPath).LastWriteTimeUtc : null;

        return new PluginCodeDto
        {
            FilePath = targetRel,
            Content = content,
            FileTree = files,
            HasBackup = hasBackup,
            BackupTimestamp = backupTs,
            IsBinary = isBinary,
            FileSizeBytes = fileSizeBytes
        };
    }

    public async ValueTask<bool> SaveCodeAsync(string id, PluginCodeSaveRequest request, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return false;

        var dir = Path.Combine(_pluginsRoot, id);
        if (!Directory.Exists(dir)) return false;

        var fullTarget = Path.GetFullPath(Path.Combine(dir, request.FilePath));
        if (!fullTarget.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("禁止访问插件目录外部路径");
        }

        var bakPath = fullTarget + ".bak";
        var timestampBak = fullTarget + "." + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ".bak";

        if (request.RestoreBackup)
        {
            if (File.Exists(bakPath))
            {
                File.Copy(bakPath, fullTarget, overwrite: true);
                return true;
            }
            return false;
        }

        if (request.CreateBackup && File.Exists(fullTarget))
        {
            File.Copy(fullTarget, bakPath, overwrite: true);
            try { File.Copy(fullTarget, timestampBak, overwrite: true); } catch { }
        }

        var parent = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        await File.WriteAllTextAsync(fullTarget, request.Content, ct);
        return true;
    }

    public async ValueTask<bool> UploadFileAsync(string id, string relativePath, Stream fileStream, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return false;
        var dir = Path.Combine(_pluginsRoot, id);
        if (!Directory.Exists(dir)) return false;

        var cleanRel = (relativePath ?? "").TrimStart('/', '\\').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(cleanRel)) return false;

        var fullTarget = Path.GetFullPath(Path.Combine(dir, cleanRel));
        if (!fullTarget.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("禁止访问插件目录外部路径");
        }

        var parent = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using (var dest = new FileStream(fullTarget, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await fileStream.CopyToAsync(dest, ct);
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                var ext = Path.GetExtension(fullTarget).ToLowerInvariant();
                var isInBin = cleanRel.StartsWith("bin/", StringComparison.OrdinalIgnoreCase);
                if (isInBin || ext == ".sh" || ext == ".so" || string.IsNullOrEmpty(ext))
                {
                    File.SetUnixFileMode(fullTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                                     UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                                     UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
            catch { }
        }

        return true;
    }

    public ValueTask<(Stream Stream, string ContentType, string FileName)> DownloadFileAsync(string id, string relativePath, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) throw new ArgumentException($"非法插件 ID: {id}");
        var dir = Path.Combine(_pluginsRoot, id);
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"未找到插件目录: {id}");

        var cleanRel = (relativePath ?? "").TrimStart('/', '\\').Replace('\\', '/');
        var fullTarget = Path.GetFullPath(Path.Combine(dir, cleanRel));
        if (!fullTarget.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("禁止访问插件目录外部路径");
        }
        if (!File.Exists(fullTarget)) throw new FileNotFoundException($"文件不存在: {cleanRel}");

        var fs = new FileStream(fullTarget, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ValueTask.FromResult<(Stream, string, string)>((fs, "application/octet-stream", Path.GetFileName(fullTarget)));
    }

    public ValueTask<bool> DeleteFileAsync(string id, string relativePath, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return ValueTask.FromResult(false);
        var dir = Path.Combine(_pluginsRoot, id);
        if (!Directory.Exists(dir)) return ValueTask.FromResult(false);

        var cleanRel = (relativePath ?? "").TrimStart('/', '\\').Replace('\\', '/');
        if (cleanRel.Equals("plugin.json", StringComparison.OrdinalIgnoreCase))
        {
            // 受保护的核心清单文件，禁止删除
            throw new InvalidOperationException("核心插件描述清单 (plugin.json) 受保护，不可删除");
        }

        var fullTarget = Path.GetFullPath(Path.Combine(dir, cleanRel));
        if (!fullTarget.StartsWith(Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("禁止访问插件目录外部路径");
        }
        if (!File.Exists(fullTarget)) return ValueTask.FromResult(false);

        File.Delete(fullTarget);
        var bak = fullTarget + ".bak";
        if (File.Exists(bak))
        {
            try { File.Delete(bak); } catch { }
        }
        return ValueTask.FromResult(true);
    }

    public async ValueTask<PluginDebugRunResultDto> DebugRunAsync(string id, PluginDebugRunRequest request, CancellationToken ct = default)
    {
        var actionReq = new PluginActionRequest
        {
            PluginId = id,
            Action = string.IsNullOrWhiteSpace(request.Action) ? "status" : request.Action,
            Param = request.Param
        };

        var sw = Stopwatch.StartNew();
        var actionResult = await ExecuteActionAsync(actionReq, ct);
        sw.Stop();

        return new PluginDebugRunResultDto
        {
            Success = actionResult.Status == "succeeded",
            ExitCode = actionResult.ExitCode ?? 0,
            Output = actionResult.Message ?? "",
            Error = actionResult.Error ?? "",
            ElapsedMs = sw.Elapsed.TotalMilliseconds
        };
    }
}
