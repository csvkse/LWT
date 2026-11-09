using System.IO.Compression;
using System.Text;
using LinuxWebTool.Application.Features.Security.Policies;
using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.Infrastructure.Features.Logging;
using LinuxWebTool.Infrastructure.Features.Security.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using LinuxWebTool.WebHost.Shared.Routes;
using Directory = System.IO.Directory;

namespace LinuxWebTool.WebHost.Features.Files.Adapters;

/// <summary>
/// 宿主主机穿透文件系统适配器：
/// 在特权容器（--privileged --pid=host）环境下，通过宿主 PID 1 的 Mount Namespace (/proc/1/root)
/// 穿透至物理宿主机根文件系统，支持高性能 VFS 直通、绝对符号链接解构重映射、核心系统目录防护黑名单与审计记录。
/// </summary>
public sealed class HostPiercingFileSystemAdapter(
    IOperationLogger operationLogger,
    ISecurityPolicyEngine policyEngine) : IFileSystemTargetAdapter
{
    private const long MaxTextBytes = 2 * 1024 * 1024;
    public string TargetId => "host";

    private static ApiKeyEntity? GetApiKey(HttpContext ctx) => ctx.Items["CurrentApiKey"] as ApiKeyEntity;

    private async ValueTask<(bool Allowed, string? Error)> CheckFileAccessAsync(string normalizedPath, bool isWrite, HttpContext ctx)
    {
        var apiKey = GetApiKey(ctx);
        if (apiKey is null) return (true, null);

        // API Key 必须显式授权全局访问，或包含该宿主路径
        if (!PathScopeGuard.IsAllowed(normalizedPath, apiKey.AllowedPaths, out var scopeErr))
        {
            return (false, scopeErr);
        }

        var evalCtx = new SecurityEvaluationContext(
            ApiKeyId: apiKey.Id,
            ApiKeyName: apiKey.Name,
            Domain: SecurityTargetDomain.LocalFile,
            ActionVerb: isWrite ? "Write" : "Read",
            TargetResource: "host",
            RawCommandOrPath: normalizedPath,
            ClientIp: ctx.GetClientIp());

        var decision = await policyEngine.EvaluateAsync(evalCtx);
        if (!decision.IsAllowed)
        {
            return (false, decision.DeniedReason ?? "安全策略拦截此宿主文件操作");
        }

        return (true, null);
    }

    private static bool EnsurePrivileged(out IResult? failure)
    {
        if (!HostPathResolver.CanPierceHost())
        {
            failure = Results.Json(new MessageResponse("当前环境非特权容器，无法穿透至宿主机文件系统"), statusCode: StatusCodes.Status403Forbidden);
            return false;
        }
        failure = null;
        return true;
    }

    public async Task<IResult> ListAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!EnsurePrivileged(out var privFail)) return privFail!;
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;

        var (allowed, failureReason) = await CheckFileAccessAsync(normalized, isWrite: false, httpContext);
        if (!allowed)
        {
            return Results.Json(new MessageResponse(failureReason ?? "无权限访问该宿主路径"), statusCode: StatusCodes.Status403Forbidden);
        }

        var physicalPath = HostPathResolver.ResolvePhysicalPath(normalized);
        if (!Directory.Exists(physicalPath))
        {
            return Results.NotFound(new MessageResponse($"宿主目录不存在：{normalized}"));
        }

        List<FileEntryResponse> entries;
        try
        {
            entries = BuildEntries(normalized, physicalPath);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Json(new MessageResponse($"无权限读取宿主目录：{normalized}"), statusCode: StatusCodes.Status403Forbidden);
        }
        catch (IOException ex)
        {
            return Results.Json(new MessageResponse($"读取宿主目录失败：{ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }

        return Results.Ok(new FileListResponse(normalized, Parent(normalized), Name(normalized), normalized == "/", entries, false, TargetId));
    }

    public async Task<IResult> ReadContentAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!EnsurePrivileged(out var privFail)) return privFail!;
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: false, httpContext);
        if (!allowed) return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);

        // 屏蔽宿主特殊设备与阻塞型虚拟节点
        if (HostPathResolver.IsPseudoOrSpecialFileSystem(normalized) && normalized.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return Results.BadRequest(new ReadFileErrorResponse("宿主特殊设备节点禁止以文本方式查看", false, true));
        }

        var physicalPath = HostPathResolver.ResolvePhysicalPath(normalized);
        if (!System.IO.File.Exists(physicalPath))
        {
            return Results.NotFound(new MessageResponse($"宿主文件不存在：{normalized}"));
        }

        var info = new System.IO.FileInfo(physicalPath);
        if (info.Length > MaxTextBytes)
        {
            return Results.BadRequest(new ReadFileErrorResponse($"文件超过 {MaxTextBytes / 1024 / 1024}MB，无法以文本查看，请直接用系统工具处理", true, false));
        }

        using (var fs = new System.IO.FileStream(physicalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var buffer = new byte[Math.Min((int)info.Length, 8192)];
            var read = await fs.ReadAsync(buffer, ct);
            if (buffer.Take(read).Contains((byte)0))
            {
                return Results.BadRequest(new ReadFileErrorResponse("二进制文件，无法以文本查看", false, true));
            }
        }

        var content = await System.IO.File.ReadAllTextAsync(physicalPath, Encoding.UTF8, ct);
        return Results.Ok(new FileContentResponse(normalized, info.Name, info.Length, content));
    }

    public async Task<IResult> WriteContentAsync(string path, string? content, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!EnsurePrivileged(out var privFail)) return privFail!;
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: true, httpContext);
        if (!allowed) return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);

        if (HostCriticalPathGuard.IsProtected(normalized, isDestructiveOperation: true, out var guardReason))
        {
            return Results.BadRequest(new MessageResponse(guardReason!));
        }

        var physicalPath = HostPathResolver.ResolvePhysicalPath(normalized);
        var physicalParent = Path.GetDirectoryName(physicalPath);
        if (physicalParent is not null && !Directory.Exists(physicalParent))
        {
            return Results.BadRequest(new MessageResponse($"宿主父目录不存在：{Parent(normalized)}"));
        }

        var exists = System.IO.File.Exists(physicalPath);
        await System.IO.File.WriteAllTextAsync(physicalPath, content ?? string.Empty, Encoding.UTF8, ct);
        await operationLogger.LogAsync(exists ? "编辑宿主文件" : "新建宿主文件", "文件管理", Name(normalized), Parent(normalized), clientIp: httpContext.GetClientIp());
        return Results.Ok(new CreateFileResponse(exists ? "已保存" : "已创建", normalized));
    }

    public async Task<IResult> MkdirAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!EnsurePrivileged(out var privFail)) return privFail!;
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: true, httpContext);
        if (!allowed) return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);

        if (HostCriticalPathGuard.IsProtected(normalized, isDestructiveOperation: true, out var guardReason))
        {
            return Results.BadRequest(new MessageResponse(guardReason!));
        }

        var physicalPath = HostPathResolver.ResolvePhysicalPath(normalized);
        if (Directory.Exists(physicalPath))
        {
            return Results.BadRequest(new MessageResponse("宿主目录已存在"));
        }

        Directory.CreateDirectory(physicalPath);
        await operationLogger.LogAsync("新建宿主文件夹", "文件管理", Name(normalized), Parent(normalized), clientIp: httpContext.GetClientIp());
        return Results.Ok(new CreateFileResponse("已创建", normalized));
    }

    public async Task<IResult> RenameAsync(string from, string to, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!EnsurePrivileged(out var privFail)) return privFail!;
        if (!TryNormalize(from, out var fromNorm, out var failureFrom)) return failureFrom!;
        if (!TryNormalize(to, out var toNorm, out var failureTo)) return failureTo!;

        var (allowedFrom, scopeErrFrom) = await CheckFileAccessAsync(fromNorm, isWrite: true, httpContext);
        if (!allowedFrom) return Results.Json(new MessageResponse(scopeErrFrom!), statusCode: StatusCodes.Status403Forbidden);

        var (allowedTo, scopeErrTo) = await CheckFileAccessAsync(toNorm, isWrite: true, httpContext);
        if (!allowedTo) return Results.Json(new MessageResponse(scopeErrTo!), statusCode: StatusCodes.Status403Forbidden);

        if (HostCriticalPathGuard.IsProtected(fromNorm, isDestructiveOperation: true, out var guardFrom))
        {
            return Results.BadRequest(new MessageResponse(guardFrom!));
        }
        if (HostCriticalPathGuard.IsProtected(toNorm, isDestructiveOperation: true, out var guardTo))
        {
            return Results.BadRequest(new MessageResponse(guardTo!));
        }

        var physFrom = HostPathResolver.ResolvePhysicalPath(fromNorm);
        var physTo = HostPathResolver.ResolvePhysicalPath(toNorm);

        if (System.IO.File.Exists(physTo) || Directory.Exists(physTo))
        {
            return Results.BadRequest(new MessageResponse($"宿主目标路径已存在：{toNorm}"));
        }

        if (System.IO.File.Exists(physFrom))
        {
            System.IO.File.Move(physFrom, physTo);
        }
        else if (Directory.Exists(physFrom))
        {
            Directory.Move(physFrom, physTo);
        }
        else
        {
            return Results.NotFound(new MessageResponse("宿主源路径不存在"));
        }
        await operationLogger.LogAsync("重命名/移动宿主文件", "文件管理", $"{Name(fromNorm)} -> {Name(toNorm)}", Parent(fromNorm), clientIp: httpContext.GetClientIp());
        return Results.Ok(new RenameFileResponse("已完成", toNorm));
    }

    public async Task<IResult> DeleteAsync(string path, bool recursive, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!EnsurePrivileged(out var privFail)) return privFail!;
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: true, httpContext);
        if (!allowed) return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);

        // 强力防守：宿主核心目录严禁删除！
        if (HostCriticalPathGuard.IsProtected(normalized, isDestructiveOperation: true, out var guardReason))
        {
            return Results.BadRequest(new MessageResponse(guardReason!));
        }

        var physicalPath = HostPathResolver.ResolvePhysicalPath(normalized);

        if (System.IO.File.Exists(physicalPath))
        {
            System.IO.File.Delete(physicalPath);
        }
        else if (Directory.Exists(physicalPath))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(physicalPath).Any())
            {
                return Results.BadRequest(new DeleteFileErrorResponse("宿主目录非空，确认后使用递归删除", true));
            }
            Directory.Delete(physicalPath, recursive);
        }
        else
        {
            return Results.NotFound(new MessageResponse("宿主路径不存在"));
        }
        await operationLogger.LogAsync("删除宿主文件", "文件管理", Name(normalized), Parent(normalized), success: true, clientIp: httpContext.GetClientIp());
        return Results.Ok(new MessageResponse("已删除"));
    }

    public async Task<IResult> UploadAsync(string path, IFormFile? file, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!EnsurePrivileged(out var privFail)) return privFail!;
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: true, httpContext);
        if (!allowed) return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);

        if (HostCriticalPathGuard.IsProtected(normalized, isDestructiveOperation: true, out var guardReason))
        {
            return Results.BadRequest(new MessageResponse(guardReason!));
        }

        var physicalPath = HostPathResolver.ResolvePhysicalPath(normalized);
        if (!Directory.Exists(physicalPath))
        {
            return Results.BadRequest(new MessageResponse($"宿主目录不存在：{normalized}"));
        }
        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new MessageResponse("未选择文件"));
        }

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Results.BadRequest(new MessageResponse("文件名无效"));
        }

        var targetVirtual = UniqueVirtualPath(normalized, fileName);
        var targetPhysical = HostPathResolver.ResolvePhysicalPath(targetVirtual);
        await using (var stream = System.IO.File.Create(targetPhysical))
        {
            await file.CopyToAsync(stream, ct);
        }
        await operationLogger.LogAsync("上传宿主文件", "文件管理", fileName, normalized, clientIp: httpContext.GetClientIp());
        return Results.Ok(new UploadFileResponse("已上传", targetVirtual));
    }

    public async Task<IResult> DownloadAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        if (!EnsurePrivileged(out var privFail)) return privFail!;
        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        var (allowed, scopeErr) = await CheckFileAccessAsync(normalized, isWrite: false, httpContext);
        if (!allowed) return Results.Json(new MessageResponse(scopeErr!), statusCode: StatusCodes.Status403Forbidden);

        if (HostPathResolver.IsPseudoOrSpecialFileSystem(normalized))
        {
            return Results.BadRequest(new MessageResponse("宿主虚拟伪文件系统禁止打包下载"));
        }

        var physicalPath = HostPathResolver.ResolvePhysicalPath(normalized);

        if (System.IO.File.Exists(physicalPath))
        {
            return Results.File(physicalPath, "application/octet-stream", fileDownloadName: Name(normalized));
        }

        if (!Directory.Exists(physicalPath))
        {
            return Results.NotFound(new MessageResponse("宿主路径不存在"));
        }

        var dirName = FileBrowserPath.IsRoot(normalized) ? "host-root" : Name(normalized);
        var zipFileName = $"{dirName}.zip";

        httpContext.Response.ContentType = "application/zip";
        httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(zipFileName)}\"";

        try
        {
            await using var zipStream = new MemoryStream();
            using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
            {
                ZipDirectory(normalized, physicalPath, archive);
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

    private static List<FileEntryResponse> BuildEntries(string virtualDir, string physicalDir)
    {
        var entries = new List<FileEntryResponse>();
        foreach (var dir in Directory.EnumerateDirectories(physicalDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(name)) continue;
            var isSymlink = false;
            try
            {
                var fsi = new DirectoryInfo(dir);
                isSymlink = fsi.LinkTarget is not null || (fsi.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch { }
            entries.Add(new FileEntryResponse(name, Join(virtualDir, name), true, 0, System.IO.File.GetLastWriteTime(dir), "", false, isSymlink));
        }
        foreach (var file in Directory.EnumerateFiles(physicalDir).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            if (string.IsNullOrEmpty(name)) continue;
            var isSymlink = false;
            long size = 0;
            try
            {
                var fsi = new FileInfo(file);
                size = fsi.Length;
                isSymlink = fsi.LinkTarget is not null || (fsi.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch { }
            entries.Add(new FileEntryResponse(name, Join(virtualDir, name), false, size, System.IO.File.GetLastWriteTime(file), Path.GetExtension(name), ContainerFileSystemAdapter.IsTextExtension(name), isSymlink));
        }
        return entries;
    }

    private static void ZipDirectory(string virtualRoot, string physicalRoot, ZipArchive archive)
    {
        const int maxFiles = 2000;
        const long maxTotalBytes = 500 * 1024 * 1024;
        var fileCount = 0;
        var totalBytes = 0L;

        void AddDirectory(string currentVirtual, string currentPhysical, string relativePath)
        {
            // 防御伪文件系统递归进入
            if (HostPathResolver.IsPseudoOrSpecialFileSystem(currentVirtual)) return;

            var currentDir = new DirectoryInfo(currentPhysical);
            foreach (var subDir in currentDir.EnumerateDirectories())
            {
                if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                var subVirtual = Join(currentVirtual, subDir.Name);
                if (HostPathResolver.IsPseudoOrSpecialFileSystem(subVirtual)) continue;

                var subRelative = string.IsNullOrEmpty(relativePath) ? subDir.Name : $"{relativePath}/{subDir.Name}";
                AddDirectory(subVirtual, subDir.FullName, subRelative);
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

        AddDirectory(virtualRoot, physicalRoot, "");
    }

    private static string UniqueVirtualPath(string virtualDir, string fileName)
    {
        var targetVirtual = Join(virtualDir, fileName);
        var targetPhys = HostPathResolver.ResolvePhysicalPath(targetVirtual);
        if (!System.IO.File.Exists(targetPhys) && !Directory.Exists(targetPhys)) return targetVirtual;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i <= 999; i++)
        {
            targetVirtual = Join(virtualDir, $"{stem}({i}){ext}");
            targetPhys = HostPathResolver.ResolvePhysicalPath(targetVirtual);
            if (!System.IO.File.Exists(targetPhys) && !Directory.Exists(targetPhys)) return targetVirtual;
        }
        throw new IOException("重名文件过多，无法生成唯一文件名");
    }

    private static bool TryNormalize(string input, out string path, out IResult? failure)
    {
        try { path = HostPathResolver.NormalizePosixPath(input); failure = null; return true; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { path = ""; failure = Results.BadRequest(new MessageResponse(ex.Message)); return false; }
    }

    private static string? Parent(string normalized)
    {
        if (normalized == "/") return null;
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }
    private static string Name(string normalized) => FileBrowserPath.Name(normalized);
    private static string Join(string dir, string name) => FileBrowserPath.Join(dir, name);
}
