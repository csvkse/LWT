using System.Diagnostics;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager
{
    private const long MaxCodeSizeBytes = 2 * 1024 * 1024; // 2MB 代码编辑限制

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

        var content = "";
        if (File.Exists(fullTarget))
        {
            var fileInfo = new FileInfo(fullTarget);
            if (fileInfo.Length > MaxCodeSizeBytes)
            {
                throw new InvalidOperationException($"文件大小超过限制 (2MB): {fileInfo.Length} 字节");
            }
            content = await File.ReadAllTextAsync(fullTarget, ct);
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
            BackupTimestamp = backupTs
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
