using System.IO.Compression;
using System.Text;
using LinuxWebTool.Application.Features.Security.Policies;
using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.Infrastructure.Features.Logging;
using LinuxWebTool.Infrastructure.Features.Mount.Adapters;
using LinuxWebTool.Infrastructure.Features.Security.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using LinuxWebTool.WebHost.Shared.Routes;
using Directory = System.IO.Directory;

namespace LinuxWebTool.WebHost.Features.Files.Adapters;

/// <summary>
/// 当前容器环境文件系统适配器：
/// 直接操作当前容器（或本机）文件系统，包含 SMB 共享挂载融合、Windows 路径适配与程序数据目录自保护。
/// </summary>
public sealed class ContainerFileSystemAdapter(
    DataPaths dataPaths,
    IOperationLogger operationLogger,
    MountStateMachineService mountHealth,
    SmbDirectoryReader smbDirectoryReader,
    SmbMountStore smbMountStore,
    ISecurityPolicyEngine policyEngine) : IFileSystemTargetAdapter
{
    private const long MaxTextBytes = 2 * 1024 * 1024;
    public string TargetId => "container";

    private static ApiKeyEntity? GetApiKey(HttpContext ctx) => ctx.Items["CurrentApiKey"] as ApiKeyEntity;

    private async ValueTask<(bool Allowed, string? Error)> CheckFileAccessAsync(string normalizedPath, bool isWrite, HttpContext ctx)
    {
        var apiKey = GetApiKey(ctx);
        if (apiKey is null) return (true, null);

        if (!PathScopeGuard.IsAllowed(normalizedPath, apiKey.AllowedPaths, out var scopeErr))
        {
            return (false, scopeErr);
        }

        var evalCtx = new SecurityEvaluationContext(
            ApiKeyId: apiKey.Id,
            ApiKeyName: apiKey.Name,
            Domain: SecurityTargetDomain.LocalFile,
            ActionVerb: isWrite ? "Write" : "Read",
            TargetResource: "localhost",
            RawCommandOrPath: normalizedPath,
            ClientIp: ctx.GetClientIp());

        var decision = await policyEngine.EvaluateAsync(evalCtx);
        if (!decision.IsAllowed)
        {
            return (false, decision.DeniedReason ?? "安全策略拦截此文件操作");
        }

        return (true, null);
    }

    public async Task<IResult> ListAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;

        var apiKey = GetApiKey(httpContext);
        if (!string.IsNullOrWhiteSpace(apiKey?.AllowedPaths))
        {
            if (normalized == "/")
            {
                var roots = PathScopeGuard.GetAllowedRoots(apiKey.AllowedPaths);
                var rootEntries = roots.Select(r => new FileEntryResponse(
                    Name: Name(r),
                    Path: r,
                    IsDirectory: true,
                    Size: 0,
                    Modified: Directory.Exists(r) ? Directory.GetLastWriteTime(r) : DateTime.UtcNow,
                    Extension: "",
                    IsTextual: false,
                    IsSymlink: false
                )).ToList();
                return Results.Ok(new FileListResponse("/", null, "工作空间", true, rootEntries, true, TargetId));
            }

            var (allowed, failureReason) = await CheckFileAccessAsync(normalized, isWrite: false, httpContext);
            if (!allowed)
            {
                return Results.Json(new MessageResponse(failureReason ?? "无权限访问该路径"), statusCode: StatusCodes.Status403Forbidden);
            }
        }

        var smbRoot = OperatingSystem.IsLinux() ? mountHealth.GetSmbMountRoot(normalized) : null;
        if (OperatingSystem.IsLinux() && smbRoot is null)
        {
            smbRoot = (await smbMountStore.GetAllAsync()).Select(m => MountOperationCoordinator.NormalizePath(m.LocalPath))
                .Where(root => normalized == root || normalized.StartsWith(root == "/" ? "/" : root + "/", StringComparison.Ordinal))
                .OrderByDescending(root => root.Length).FirstOrDefault();
        }

        if (smbRoot is not null)
        {
            var result = await smbDirectoryReader.ReadAsync(smbRoot, normalized, httpContext.TraceIdentifier, httpContext.RequestAborted);
            if (!result.Success)
            {
                mountHealth.RequestImmediateCheck(normalized);
                return Results.Json(new MessageResponse(result.Error ?? "读取挂载目录超时"),
                    statusCode: result.Busy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status504GatewayTimeout);
            }
            var smbEntries = result.Entries.Select(e => new FileEntryResponse(
                e.Name, Join(normalized, e.Name), e.IsDirectory, e.IsDirectory ? 0 : e.Size, e.Modified,
                e.IsDirectory ? string.Empty : Path.GetExtension(e.Name), !e.IsDirectory && IsTextExtension(e.Name),
                IsSymlink: false
            )).ToList();
            return Results.Ok(new FileListResponse(normalized, Parent(normalized), Name(normalized), normalized == "/", smbEntries, false, TargetId));
        }

        if (OperatingSystem.IsWindows() && normalized == "/")
        {
            var roots = Directory.GetLogicalDrives().Select(d => FileBrowserPath.Normalize(d))
                .Select(r => new FileEntryResponse(r, r, true, 0, Directory.GetLastWriteTime(r), "", false, false)).ToList();
            return Results.Ok(new FileListResponse("/", null, "磁盘", true, roots, true, TargetId));
        }

        if (!Directory.Exists(normalized))
        {
            return Results.NotFound(new MessageResponse($"目录不存在：{normalized}"));
        }

        List<FileEntryResponse> entries;
        try
        {
            entries = BuildEntries(normalized);
        }
        catch (UnauthorizedAccessException)
        {
            mountHealth.RequestImmediateCheck(normalized);
            return Results.Json(new MessageResponse($"无权限读取该目录：{normalized}"), statusCode: StatusCodes.Status403Forbidden);
        }
        catch (IOException ex)
        {
            mountHealth.RequestImmediateCheck(normalized);
            return Results.Json(new MessageResponse($"读取目录失败：{ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(new FileListResponse(normalized, Parent(normalized), Name(normalized), normalized == "/", entries, false, TargetId));
    }

    public async Task<IResult> ReadContentAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: false, httpContext);
        if (!allowed)
        {
            return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);
        }

        if (!System.IO.File.Exists(normalized))
        {
            return Results.NotFound(new MessageResponse($"文件不存在：{normalized}"));
        }

        var info = new System.IO.FileInfo(normalized);
        if (info.Length > MaxTextBytes)
        {
            return Results.BadRequest(new ReadFileErrorResponse($"文件超过 {MaxTextBytes / 1024 / 1024}MB，无法以文本查看，请直接用系统工具处理", true, false));
        }

        using (var fs = new System.IO.FileStream(normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var buffer = new byte[Math.Min((int)info.Length, 8192)];
            var read = await fs.ReadAsync(buffer, ct);
            if (buffer.Take(read).Contains((byte)0))
            {
                return Results.BadRequest(new ReadFileErrorResponse("二进制文件，无法以文本查看", false, true));
            }
        }

        var content = await System.IO.File.ReadAllTextAsync(normalized, Encoding.UTF8, ct);
        return Results.Ok(new FileContentResponse(normalized, info.Name, info.Length, content));
    }

    public async Task<IResult> WriteContentAsync(string path, string? content, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: true, httpContext);
        if (!allowed)
        {
            return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);
        }

        var parent = Parent(normalized);
        if (parent is not null && !Directory.Exists(parent))
        {
            return Results.BadRequest(new MessageResponse($"父目录不存在：{parent}"));
        }
        if (IsProtected(normalized))
        {
            return Results.BadRequest(new MessageResponse("程序数据目录禁止写入"));
        }

        var exists = System.IO.File.Exists(normalized);
        await System.IO.File.WriteAllTextAsync(normalized, content ?? string.Empty, Encoding.UTF8, ct);
        await operationLogger.LogAsync(exists ? "编辑文件" : "新建文件", "文件管理", Name(normalized), parent, clientIp: httpContext.GetClientIp());
        return Results.Ok(new CreateFileResponse(exists ? "已保存" : "已创建", normalized));
    }

    public async Task<IResult> MkdirAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: true, httpContext);
        if (!allowed)
        {
            return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);
        }

        if (Directory.Exists(normalized))
        {
            return Results.BadRequest(new MessageResponse("目录已存在"));
        }
        if (IsProtected(normalized))
        {
            return Results.BadRequest(new MessageResponse("程序数据目录禁止创建子目录"));
        }

        Directory.CreateDirectory(normalized);
        await operationLogger.LogAsync("新建文件夹", "文件管理", Name(normalized), Parent(normalized), clientIp: httpContext.GetClientIp());
        return Results.Ok(new CreateFileResponse("已创建", normalized));
    }

    public async Task<IResult> RenameAsync(string from, string to, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!TryNormalize(from, out var fromNorm, out var failureFrom)) return failureFrom!;
        if (!TryNormalize(to, out var toNorm, out var failureTo)) return failureTo!;

        var (allowedFrom, scopeErrFrom) = await CheckFileAccessAsync(fromNorm, isWrite: true, httpContext);
        if (!allowedFrom) return Results.Json(new MessageResponse(scopeErrFrom!), statusCode: StatusCodes.Status403Forbidden);

        var (allowedTo, scopeErrTo) = await CheckFileAccessAsync(toNorm, isWrite: true, httpContext);
        if (!allowedTo) return Results.Json(new MessageResponse(scopeErrTo!), statusCode: StatusCodes.Status403Forbidden);

        if (IsProtected(fromNorm) || IsProtected(toNorm))
        {
            return Results.BadRequest(new MessageResponse("程序数据目录禁止移动或重命名"));
        }
        if (System.IO.File.Exists(toNorm) || Directory.Exists(toNorm))
        {
            return Results.BadRequest(new MessageResponse($"目标路径已存在：{toNorm}"));
        }

        if (System.IO.File.Exists(fromNorm))
        {
            System.IO.File.Move(fromNorm, toNorm);
        }
        else if (Directory.Exists(fromNorm))
        {
            Directory.Move(fromNorm, toNorm);
        }
        else
        {
            return Results.NotFound(new MessageResponse("源路径不存在"));
        }
        await operationLogger.LogAsync("重命名/移动", "文件管理", $"{Name(fromNorm)} -> {Name(toNorm)}", Parent(fromNorm), clientIp: httpContext.GetClientIp());
        return Results.Ok(new RenameFileResponse("已完成", toNorm));
    }

    public async Task<IResult> DeleteAsync(string path, bool recursive, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: true, httpContext);
        if (!allowed)
        {
            return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);
        }

        if (FileBrowserPath.IsRoot(normalized))
        {
            return Results.BadRequest(new MessageResponse("不能删除根目录"));
        }
        if (IsProtected(normalized))
        {
            return Results.BadRequest(new MessageResponse("程序数据目录禁止删除"));
        }

        if (System.IO.File.Exists(normalized))
        {
            System.IO.File.Delete(normalized);
        }
        else if (Directory.Exists(normalized))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(normalized).Any())
            {
                return Results.BadRequest(new DeleteFileErrorResponse("目录非空，确认后使用递归删除", true));
            }
            Directory.Delete(normalized, recursive);
        }
        else
        {
            return Results.NotFound(new MessageResponse("路径不存在"));
        }
        await operationLogger.LogAsync("删除", "文件管理", Name(normalized), Parent(normalized), success: true, clientIp: httpContext.GetClientIp());
        return Results.Ok(new MessageResponse("已删除"));
    }

    public async Task<IResult> UploadAsync(string path, IFormFile? file, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: true, httpContext);
        if (!allowed)
        {
            return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);
        }

        if (!Directory.Exists(normalized))
        {
            return Results.BadRequest(new MessageResponse($"目录不存在：{normalized}"));
        }
        if (IsProtected(normalized)) return Results.BadRequest(new MessageResponse("程序数据目录禁止上传"));
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new MessageResponse("未选择文件"));
        }

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Results.BadRequest(new MessageResponse("文件名无效"));
        }

        var target = UniquePath(normalized, fileName);
        await using (var stream = System.IO.File.Create(target))
        {
            await file.CopyToAsync(stream, ct);
        }
        await operationLogger.LogAsync("上传文件", "文件管理", fileName, normalized, clientIp: httpContext.GetClientIp());
        return Results.Ok(new UploadFileResponse("已上传", target));
    }

    public async Task<IResult> DownloadAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: false, httpContext);
        if (!allowed)
        {
            return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);
        }

        if (FileBrowserPath.IsRoot(normalized))
        {
            return Results.BadRequest(new MessageResponse("不能下载根目录"));
        }
        if (IsProtected(normalized))
        {
            return Results.BadRequest(new MessageResponse("程序数据目录禁止下载"));
        }


        if (System.IO.File.Exists(normalized))
        {
            return Results.File(normalized, "application/octet-stream", fileDownloadName: Name(normalized));
        }

        if (!Directory.Exists(normalized))
        {
            return Results.NotFound(new MessageResponse("路径不存在"));
        }

        var dirName = FileBrowserPath.IsRoot(normalized) ? "root" : Name(normalized);
        var zipFileName = $"{dirName}.zip";

        httpContext.Response.ContentType = "application/zip";
        httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(zipFileName)}\"";

        try
        {
            await using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipDirectory(normalized, archive);
            }
            zipStream.Position = 0;
            await zipStream.CopyToAsync(httpContext.Response.Body, ct);
            return Results.Empty;
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new MessageResponse(ex.Message));
        }
    }

    private static List<FileEntryResponse> BuildEntries(string normalized)
    {
        var entries = new List<FileEntryResponse>();
        foreach (var dir in Directory.EnumerateDirectories(normalized).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Name(dir);
            var isSymlink = false;
            try
            {
                var fsi = new DirectoryInfo(dir);
                isSymlink = fsi.LinkTarget is not null || (fsi.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch { }
            entries.Add(new FileEntryResponse(name, Join(normalized, name), true, 0, System.IO.File.GetLastWriteTime(dir), "", false, isSymlink));
        }
        foreach (var file in Directory.EnumerateFiles(normalized).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Name(file);
            var isSymlink = false;
            long size = 0;
            try
            {
                var fsi = new FileInfo(file);
                size = fsi.Length;
                isSymlink = fsi.LinkTarget is not null || (fsi.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch { }
            entries.Add(new FileEntryResponse(name, Join(normalized, name), false, size, System.IO.File.GetLastWriteTime(file), Path.GetExtension(name), IsTextExtension(name), isSymlink));
        }
        return entries;
    }

    private static void ZipDirectory(string rootDir, ZipArchive archive)
    {
        const int maxFiles = 2000;
        const long maxTotalBytes = 500 * 1024 * 1024;
        var fileCount = 0;
        var totalBytes = 0L;

        void AddDirectory(string currentDirPath, string relativePath)
        {
            var currentDir = new DirectoryInfo(currentDirPath);
            foreach (var subDir in currentDir.EnumerateDirectories())
            {
                if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var subRelative = string.IsNullOrEmpty(relativePath) ? subDir.Name : $"{relativePath}/{subDir.Name}";
                AddDirectory(subDir.FullName, subRelative);
            }

            foreach (var file in currentDir.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (++fileCount > maxFiles)
                    throw new InvalidOperationException($"文件夹内文件数量过多（超过 {maxFiles} 个），建议使用命令行打包");

                totalBytes += file.Length;
                if (totalBytes > maxTotalBytes)
                    throw new InvalidOperationException($"文件夹总大小超过安全上限（{maxTotalBytes / 1024 / 1024 / 1024}GB），建议使用系统工具传输");

                var entryName = string.IsNullOrEmpty(relativePath) ? file.Name : $"{relativePath}/{file.Name}";
                var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                entry.LastWriteTime = file.LastWriteTime;

                using var fs = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var entryStream = entry.Open();
                fs.CopyTo(entryStream);
            }
        }

        AddDirectory(rootDir, "");
    }

    private bool IsProtected(string normalized)
    {
        var dataRoot = FileBrowserPath.Normalize(dataPaths.Root);
        return FileBrowserPath.IsRoot(normalized) || FileBrowserPath.IsWithin(normalized, dataRoot)
            || FileBrowserPath.IsWithin(dataRoot, normalized) || FileBrowserPath.HasReparsePoint(normalized);
    }

    private static string UniquePath(string dir, string fileName)
    {
        var target = Join(dir, fileName);
        if (!System.IO.File.Exists(target) && !Directory.Exists(target)) return target;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i <= 999; i++)
        {
            target = Join(dir, $"{stem}({i}){ext}");
            if (!System.IO.File.Exists(target) && !Directory.Exists(target)) return target;
        }
        throw new IOException("重名文件过多，无法生成唯一文件名");
    }

    private static bool TryNormalize(string input, out string path, out IResult? failure)
    {
        try { path = FileBrowserPath.Normalize(input); failure = null; return true; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { path = ""; failure = Results.BadRequest(new MessageResponse(ex.Message)); return false; }
    }

    private static string? Parent(string normalized) => FileBrowserPath.Parent(normalized);
    private static string Name(string normalized) => FileBrowserPath.Name(normalized);
    private static string Join(string dir, string name) => FileBrowserPath.Join(dir, name);

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".js", ".mjs", ".cjs", ".ts", ".py", ".sh", ".bash", ".zsh",
        ".yml", ".yaml", ".xml", ".html", ".htm", ".css", ".csv", ".conf", ".ini", ".toml",
        ".log", ".env", ".properties", ".cs", ".rs", ".c", ".h", ".cpp", ".hpp", ".java", ".go",
        ".sql", ".bat", ".ps1", ".vim", ".gitignore", ".dockerignore", ".editorconfig", ".csproj",
        ".sln", ".slnx", ".scss",
    };

    public static bool IsTextExtension(string name)
    {
        var ext = Path.GetExtension(name);
        if (TextExtensions.Contains(ext)) return true;
        return ext.Length == 0 && !name.StartsWith(".", StringComparison.Ordinal);
    }
}
