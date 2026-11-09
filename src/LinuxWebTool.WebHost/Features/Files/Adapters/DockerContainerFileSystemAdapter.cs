using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using LinuxWebTool.Application.Features.Security.Policies;
using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Infrastructure.Features.Logging;
using LinuxWebTool.Infrastructure.Features.Security.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using LinuxWebTool.WebHost.Shared.Routes;
using Directory = System.IO.Directory;

namespace LinuxWebTool.WebHost.Features.Files.Adapters;

/// <summary>
/// Docker 容器文件系统目标适配器：
/// 实现 IFileSystemTargetAdapter 统一契约，提供双模混合加速引擎：
/// 1. 运行中模式 (ProcRoot VFS)：通过特权容器直通 /proc/{pid}/root，原生 POSIX 毫秒读写、支持 Seek 与原子保存；
/// 2. 离线/受限模式 (Docker Archive Tar)：基于 .NET 10 原生 System.Formats.Tar 封包解包，保障停机容器抢救排障。
/// </summary>
public sealed class DockerContainerFileSystemAdapter(
    IDockerEngineAdapter dockerAdapter,
    IOperationLogger operationLogger) : IFileSystemTargetAdapter
{
    private const long MaxTextBytes = 2 * 1024 * 1024;
    public string TargetId => "docker";

    private static string? ExtractContainerId(HttpContext ctx)
    {
        var target = ctx.Items["FilesTarget"] as string;
        if (string.IsNullOrWhiteSpace(target))
        {
            target = ctx.Request.Query["target"].ToString();
        }
        if (string.IsNullOrWhiteSpace(target) && ctx.Request.HasFormContentType)
        {
            target = ctx.Request.Form["target"].ToString();
        }

        if (!string.IsNullOrWhiteSpace(target) && target.StartsWith("docker:", StringComparison.OrdinalIgnoreCase))
        {
            return target["docker:".Length..].Trim();
        }

        return null;
    }

    private static string ResolvePhysicalPath(int pid, string normalizedVirtual)
    {
        var baseRoot = $"/proc/{pid}/root";
        if (normalizedVirtual == "/" || string.IsNullOrWhiteSpace(normalizedVirtual))
        {
            return baseRoot;
        }

        var direct = $"{baseRoot}{normalizedVirtual}";
        if (OperatingSystem.IsLinux() && (File.Exists(direct) || Directory.Exists(direct)))
        {
            try
            {
                FileSystemInfo fsi = Directory.Exists(direct) ? new DirectoryInfo(direct) : new FileInfo(direct);
                if (fsi.LinkTarget is not null && fsi.LinkTarget.StartsWith('/'))
                {
                    var remapped = $"{baseRoot}{HostPathResolver.NormalizePosixPath(fsi.LinkTarget)}";
                    if (File.Exists(remapped) || Directory.Exists(remapped))
                    {
                        return remapped;
                    }
                }
            }
            catch { }
        }

        return direct;
    }

    public async Task<IResult> ListAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        var containerId = ExtractContainerId(httpContext);
        if (string.IsNullOrWhiteSpace(containerId))
            return Results.BadRequest(new MessageResponse("未指定目标 Docker 容器 ID"));

        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;

        var inspect = await dockerAdapter.InspectContainerAsync(containerId, ct);
        if (inspect == null)
            return Results.NotFound(new MessageResponse($"容器 [{containerId}] 不存在"));

        // 1. 运行中且支持 ProcRoot 穿透 -> 毫秒级原生 POSIX 直通
        if (inspect.Running && inspect.Pid > 0 && OperatingSystem.IsLinux() && Directory.Exists($"/proc/{inspect.Pid}/root"))
        {
            var phys = ResolvePhysicalPath(inspect.Pid, normalized);
            if (!Directory.Exists(phys))
                return Results.NotFound(new MessageResponse($"容器目录不存在：{normalized}"));

            try
            {
                var entries = BuildEntries(normalized, phys);
                var fullTargetId = $"docker:{inspect.Id}";
                return Results.Ok(new FileListResponse(normalized, Parent(normalized), Name(normalized), normalized == "/", entries, false, fullTargetId));
            }
            catch (Exception ex)
            {
                return Results.Json(new MessageResponse($"读取容器目录失败：{ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
            }
        }

        // 2. 停机或非特权环境 -> Docker Archive Tar 流式解析降级
        try
        {
            var archiveStream = await dockerAdapter.GetArchiveAsync(containerId, normalized, ct);
            if (archiveStream == null)
                return Results.NotFound(new MessageResponse($"无法获取容器归档路径：{normalized}"));

            await using (archiveStream)
            {
                var entries = new List<FileEntryResponse>();
                using var tarReader = new TarReader(archiveStream);
                var isFirst = true;

                while (await tarReader.GetNextEntryAsync(false, ct) is { } entry)
                {
                    if (isFirst)
                    {
                        isFirst = false;
                        continue;
                    }

                    var cleanName = entry.Name.TrimEnd('/').Split('/').Last();
                    if (string.IsNullOrWhiteSpace(cleanName)) continue;

                    var isDir = entry.EntryType == TarEntryType.Directory;
                    var entryVirtual = Join(normalized, cleanName);
                    entries.Add(new FileEntryResponse(
                        cleanName,
                        entryVirtual,
                        isDir,
                        entry.Length,
                        entry.ModificationTime.UtcDateTime,
                        Path.GetExtension(cleanName),
                        IsTextualExtension(cleanName),
                        entry.EntryType == TarEntryType.SymbolicLink
                    ));
                }

                var fullTargetId = $"docker:{inspect.Id}";
                return Results.Ok(new FileListResponse(normalized, Parent(normalized), Name(normalized), normalized == "/", entries, false, fullTargetId));
            }
        }
        catch (Exception ex)
        {
            return Results.Json(new MessageResponse($"Docker Archive 解析失败: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public async Task<IResult> ReadContentAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        var containerId = ExtractContainerId(httpContext);
        if (string.IsNullOrWhiteSpace(containerId))
            return Results.BadRequest(new MessageResponse("未指定目标 Docker 容器 ID"));

        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;

        var inspect = await dockerAdapter.InspectContainerAsync(containerId, ct);
        if (inspect == null)
            return Results.NotFound(new MessageResponse($"容器 [{containerId}] 不存在"));

        // 1. ProcRoot VFS 模式
        if (inspect.Running && inspect.Pid > 0 && OperatingSystem.IsLinux() && Directory.Exists($"/proc/{inspect.Pid}/root"))
        {
            var phys = ResolvePhysicalPath(inspect.Pid, normalized);
            if (!File.Exists(phys))
                return Results.NotFound(new MessageResponse($"容器文件不存在：{normalized}"));

            var info = new FileInfo(phys);
            if (info.Length > MaxTextBytes)
                return Results.BadRequest(new ReadFileErrorResponse($"文件大小超过 {MaxTextBytes / 1024 / 1024}MB，禁止文本在线加载", true, false));

            using (var fs = new FileStream(phys, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var buf = new byte[Math.Min((int)info.Length, 8192)];
                var read = await fs.ReadAsync(buf, ct);
                if (buf.Take(read).Contains((byte)0))
                    return Results.BadRequest(new ReadFileErrorResponse("二进制文件，无法以文本查看", false, true));
            }

            var text = await File.ReadAllTextAsync(phys, Encoding.UTF8, ct);
            return Results.Ok(new FileContentResponse(normalized, info.Name, info.Length, text));
        }

        // 2. Docker Archive Tar 降级模式
        try
        {
            var archiveStream = await dockerAdapter.GetArchiveAsync(containerId, normalized, ct);
            if (archiveStream == null)
                return Results.NotFound(new MessageResponse($"容器文件不存在：{normalized}"));

            await using (archiveStream)
            {
                using var tarReader = new TarReader(archiveStream);
                if (await tarReader.GetNextEntryAsync(true, ct) is { } entry && entry.DataStream != null)
                {
                    if (entry.Length > MaxTextBytes)
                        return Results.BadRequest(new ReadFileErrorResponse($"文件大小超过 {MaxTextBytes / 1024 / 1024}MB，禁止文本在线加载", true, false));

                    using var ms = new MemoryStream();
                    await entry.DataStream.CopyToAsync(ms, ct);
                    var bytes = ms.ToArray();
                    if (bytes.Take(8192).Contains((byte)0))
                        return Results.BadRequest(new ReadFileErrorResponse("二进制文件，无法以文本查看", false, true));

                    var text = Encoding.UTF8.GetString(bytes);
                    var fileName = Path.GetFileName(normalized);
                    return Results.Ok(new FileContentResponse(normalized, fileName, bytes.Length, text));
                }
            }
            return Results.NotFound(new MessageResponse("未在归档中找到匹配的文件项"));
        }
        catch (Exception ex)
        {
            return Results.Json(new MessageResponse($"读取容器文件异常: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public async Task<IResult> WriteContentAsync(string path, string? content, HttpContext httpContext, CancellationToken ct = default)
    {
        var containerId = ExtractContainerId(httpContext);
        if (string.IsNullOrWhiteSpace(containerId))
            return Results.BadRequest(new MessageResponse("未指定目标 Docker 容器 ID"));

        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;

        var inspect = await dockerAdapter.InspectContainerAsync(containerId, ct);
        if (inspect == null)
            return Results.NotFound(new MessageResponse($"容器 [{containerId}] 不存在"));

        // 1. ProcRoot VFS 模式（原子 Inode 写入）
        if (inspect.Running && inspect.Pid > 0 && OperatingSystem.IsLinux() && Directory.Exists($"/proc/{inspect.Pid}/root"))
        {
            var phys = ResolvePhysicalPath(inspect.Pid, normalized);
            var parentPhys = Path.GetDirectoryName(phys);
            if (parentPhys != null && !Directory.Exists(parentPhys))
                return Results.BadRequest(new MessageResponse($"容器父目录不存在：{Parent(normalized)}"));

            var exists = File.Exists(phys);
            var tempPhys = $"{phys}.tmp.{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(tempPhys, content ?? string.Empty, Encoding.UTF8, ct);

            try
            {
                File.Move(tempPhys, phys, overwrite: true);
            }
            catch
            {
                // 若由于跨挂载点导致 Move 失败，降级直接写入
                await File.WriteAllTextAsync(phys, content ?? string.Empty, Encoding.UTF8, ct);
                try { File.Delete(tempPhys); } catch { }
            }

            await operationLogger.LogAsync(exists ? "编辑容器文件" : "新建容器文件", "文件管理", Name(normalized), Parent(normalized), clientIp: httpContext.GetClientIp());
            return Results.Ok(new CreateFileResponse(exists ? "已保存" : "已创建", normalized));
        }

        // 2. Docker Archive Tar 降级写入
        try
        {
            var fileName = Name(normalized);
            var parentDir = Parent(normalized) ?? "/";

            using var memStream = new MemoryStream();
            using (var tarWriter = new TarWriter(memStream, TarEntryFormat.Pax, leaveOpen: true))
            {
                var contentBytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
                var entry = new PaxTarEntry(TarEntryType.RegularFile, fileName)
                {
                    DataStream = new MemoryStream(contentBytes)
                };
                await tarWriter.WriteEntryAsync(entry, ct);
            }
            memStream.Position = 0;

            var putSuccess = await dockerAdapter.PutArchiveAsync(containerId, parentDir, memStream, ct);
            if (putSuccess)
            {
                await operationLogger.LogAsync("更新容器文件 (Tar)", "文件管理", fileName, parentDir, clientIp: httpContext.GetClientIp());
                return Results.Ok(new CreateFileResponse("已保存", normalized));
            }
            return Results.Json(new MessageResponse("Docker Archive 写入失败"), statusCode: StatusCodes.Status500InternalServerError);
        }
        catch (Exception ex)
        {
            return Results.Json(new MessageResponse($"写入容器文件异常: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public async Task<IResult> MkdirAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        var containerId = ExtractContainerId(httpContext);
        if (string.IsNullOrWhiteSpace(containerId))
            return Results.BadRequest(new MessageResponse("未指定目标 Docker 容器 ID"));

        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;

        var inspect = await dockerAdapter.InspectContainerAsync(containerId, ct);
        if (inspect == null)
            return Results.NotFound(new MessageResponse($"容器 [{containerId}] 不存在"));

        if (inspect.Running && inspect.Pid > 0 && OperatingSystem.IsLinux() && Directory.Exists($"/proc/{inspect.Pid}/root"))
        {
            var phys = ResolvePhysicalPath(inspect.Pid, normalized);
            if (Directory.Exists(phys))
                return Results.BadRequest(new MessageResponse("容器目录已存在"));

            Directory.CreateDirectory(phys);
            await operationLogger.LogAsync("创建容器目录", "文件管理", Name(normalized), Parent(normalized), clientIp: httpContext.GetClientIp());
            return Results.Ok(new CreateFileResponse("已创建", normalized));
        }

        // 停机模式下通过 Tar 注入目录
        try
        {
            var dirName = Name(normalized);
            var parentDir = Parent(normalized) ?? "/";
            using var mem = new MemoryStream();
            using (var tar = new TarWriter(mem, TarEntryFormat.Pax, leaveOpen: true))
            {
                var entry = new PaxTarEntry(TarEntryType.Directory, dirName);
                await tar.WriteEntryAsync(entry, ct);
            }
            mem.Position = 0;
            var ok = await dockerAdapter.PutArchiveAsync(containerId, parentDir, mem, ct);
            return ok ? Results.Ok(new CreateFileResponse("已创建", normalized)) : Results.BadRequest(new MessageResponse("创建容器目录失败"));
        }
        catch (Exception ex)
        {
            return Results.Json(new MessageResponse($"创建容器目录异常: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public async Task<IResult> RenameAsync(string from, string to, HttpContext httpContext, CancellationToken ct = default)
    {
        var containerId = ExtractContainerId(httpContext);
        if (string.IsNullOrWhiteSpace(containerId))
            return Results.BadRequest(new MessageResponse("未指定目标 Docker 容器 ID"));

        if (!TryNormalize(from, out var normFrom, out var failFrom)) return failFrom!;
        if (!TryNormalize(to, out var normTo, out var failTo)) return failTo!;

        var inspect = await dockerAdapter.InspectContainerAsync(containerId, ct);
        if (inspect == null)
            return Results.NotFound(new MessageResponse($"容器 [{containerId}] 不存在"));

        if (inspect.Running && inspect.Pid > 0 && OperatingSystem.IsLinux() && Directory.Exists($"/proc/{inspect.Pid}/root"))
        {
            var physFrom = ResolvePhysicalPath(inspect.Pid, normFrom);
            var physTo = ResolvePhysicalPath(inspect.Pid, normTo);

            if (File.Exists(physFrom))
            {
                if (File.Exists(physTo)) return Results.BadRequest(new MessageResponse("目标文件已存在"));
                File.Move(physFrom, physTo);
            }
            else if (Directory.Exists(physFrom))
            {
                if (Directory.Exists(physTo)) return Results.BadRequest(new MessageResponse("目标目录已存在"));
                Directory.Move(physFrom, physTo);
            }
            else
            {
                return Results.NotFound(new MessageResponse("源路径不存在"));
            }

            await operationLogger.LogAsync("重命名容器文件", "文件管理", $"{Name(normFrom)} -> {Name(normTo)}", Parent(normFrom), clientIp: httpContext.GetClientIp());
            return Results.Ok(new CreateFileResponse("已重命名", normTo));
        }

        return Results.BadRequest(new MessageResponse("容器未运行，离线状态暂不支持重命名操作"));
    }

    public async Task<IResult> DeleteAsync(string path, bool recursive, HttpContext httpContext, CancellationToken ct = default)
    {
        var containerId = ExtractContainerId(httpContext);
        if (string.IsNullOrWhiteSpace(containerId))
            return Results.BadRequest(new MessageResponse("未指定目标 Docker 容器 ID"));

        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        if (normalized == "/")
            return Results.BadRequest(new MessageResponse("严禁删除容器根目录！"));

        var inspect = await dockerAdapter.InspectContainerAsync(containerId, ct);
        if (inspect == null)
            return Results.NotFound(new MessageResponse($"容器 [{containerId}] 不存在"));

        if (inspect.Running && inspect.Pid > 0 && OperatingSystem.IsLinux() && Directory.Exists($"/proc/{inspect.Pid}/root"))
        {
            var phys = ResolvePhysicalPath(inspect.Pid, normalized);
            if (File.Exists(phys))
            {
                File.Delete(phys);
            }
            else if (Directory.Exists(phys))
            {
                if (!recursive && Directory.EnumerateFileSystemEntries(phys).Any())
                {
                    return Results.BadRequest(new DeleteFileErrorResponse("容器目录非空，确认后使用递归删除", true));
                }
                Directory.Delete(phys, recursive);
            }
            else
            {
                return Results.NotFound(new MessageResponse("容器路径不存在"));
            }

            await operationLogger.LogAsync("删除容器文件", "文件管理", Name(normalized), Parent(normalized), clientIp: httpContext.GetClientIp());
            return Results.Ok(new MessageResponse("已删除"));
        }

        return Results.BadRequest(new MessageResponse("容器未处于运行状态，离线状态禁止删除文件以保护联合文件系统一致性"));
    }

    public async Task<IResult> UploadAsync(string path, IFormFile? file, HttpContext httpContext, CancellationToken ct = default)
    {
        var containerId = ExtractContainerId(httpContext);
        if (string.IsNullOrWhiteSpace(containerId))
            return Results.BadRequest(new MessageResponse("未指定目标 Docker 容器 ID"));

        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;
        if (file is null || file.Length == 0) return Results.BadRequest(new MessageResponse("未选择文件"));

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) return Results.BadRequest(new MessageResponse("文件名无效"));

        var inspect = await dockerAdapter.InspectContainerAsync(containerId, ct);
        if (inspect == null)
            return Results.NotFound(new MessageResponse($"容器 [{containerId}] 不存在"));

        if (inspect.Running && inspect.Pid > 0 && OperatingSystem.IsLinux() && Directory.Exists($"/proc/{inspect.Pid}/root"))
        {
            var physDir = ResolvePhysicalPath(inspect.Pid, normalized);
            if (!Directory.Exists(physDir))
                return Results.BadRequest(new MessageResponse($"容器目标目录不存在：{normalized}"));

            var targetVirtual = Join(normalized, fileName);
            var targetPhys = ResolvePhysicalPath(inspect.Pid, targetVirtual);

            await using (var fs = File.Create(targetPhys))
            {
                await file.CopyToAsync(fs, ct);
            }

            await operationLogger.LogAsync("上传容器文件", "文件管理", fileName, normalized, clientIp: httpContext.GetClientIp());
            return Results.Ok(new UploadFileResponse("已上传", targetVirtual));
        }

        // 停机模式使用 Tar 上传
        try
        {
            using var mem = new MemoryStream();
            using (var tar = new TarWriter(mem, TarEntryFormat.Pax, leaveOpen: true))
            {
                using var uploadStream = file.OpenReadStream();
                var entry = new PaxTarEntry(TarEntryType.RegularFile, fileName)
                {
                    DataStream = uploadStream
                };
                await tar.WriteEntryAsync(entry, ct);
            }
            mem.Position = 0;
            var ok = await dockerAdapter.PutArchiveAsync(containerId, normalized, mem, ct);
            return ok ? Results.Ok(new UploadFileResponse("已上传", Join(normalized, fileName))) : Results.BadRequest(new MessageResponse("上传容器文件失败"));
        }
        catch (Exception ex)
        {
            return Results.Json(new MessageResponse($"上传容器文件异常: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    public async Task<IResult> DownloadAsync(string path, HttpContext httpContext, CancellationToken ct = default)
    {
        var containerId = ExtractContainerId(httpContext);
        if (string.IsNullOrWhiteSpace(containerId))
            return Results.BadRequest(new MessageResponse("未指定目标 Docker 容器 ID"));

        if (!TryNormalize(path, out var normalized, out var failure)) return failure!;

        var inspect = await dockerAdapter.InspectContainerAsync(containerId, ct);
        if (inspect == null)
            return Results.NotFound(new MessageResponse($"容器 [{containerId}] 不存在"));

        if (inspect.Running && inspect.Pid > 0 && OperatingSystem.IsLinux() && Directory.Exists($"/proc/{inspect.Pid}/root"))
        {
            var phys = ResolvePhysicalPath(inspect.Pid, normalized);
            if (File.Exists(phys))
            {
                return Results.File(phys, "application/octet-stream", fileDownloadName: Name(normalized));
            }

            if (Directory.Exists(phys))
            {
                var dirName = normalized == "/" ? $"container-{inspect.Name}-root" : Name(normalized);
                var zipFileName = $"{dirName}.zip";

                httpContext.Response.ContentType = "application/zip";
                httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{Uri.EscapeDataString(zipFileName)}\"";

                await using var zipStream = new MemoryStream();
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    AddDirectoryToZip(phys, "", archive);
                }
                zipStream.Position = 0;
                await zipStream.CopyToAsync(httpContext.Response.Body, ct);
                return Results.Empty;
            }

            return Results.NotFound(new MessageResponse("容器路径不存在"));
        }

        // 停机模式使用 Tar 流下载
        try
        {
            var stream = await dockerAdapter.GetArchiveAsync(containerId, normalized, ct);
            if (stream == null) return Results.NotFound(new MessageResponse("容器文件或归档不存在"));

            var downloadName = (normalized == "/" ? $"{inspect.Name}-root" : Name(normalized)) + ".tar";
            return Results.File(stream, "application/x-tar", fileDownloadName: downloadName);
        }
        catch (Exception ex)
        {
            return Results.Json(new MessageResponse($"下载容器归档异常: {ex.Message}"), statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static void AddDirectoryToZip(string physicalDir, string entryPrefix, ZipArchive archive)
    {
        var dirInfo = new DirectoryInfo(physicalDir);
        foreach (var file in dirInfo.EnumerateFiles())
        {
            if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var entryName = string.IsNullOrEmpty(entryPrefix) ? file.Name : $"{entryPrefix}/{file.Name}";
            var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
            entry.LastWriteTime = file.LastWriteTime;
            using var fs = file.OpenRead();
            using var es = entry.Open();
            fs.CopyTo(es);
        }
        foreach (var sub in dirInfo.EnumerateDirectories())
        {
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var subPrefix = string.IsNullOrEmpty(entryPrefix) ? sub.Name : $"{entryPrefix}/{sub.Name}";
            AddDirectoryToZip(sub.FullName, subPrefix, archive);
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
            entries.Add(new FileEntryResponse(name, Join(virtualDir, name), true, 0, File.GetLastWriteTime(dir), "", false, isSymlink));
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
            entries.Add(new FileEntryResponse(name, Join(virtualDir, name), false, size, File.GetLastWriteTime(file), Path.GetExtension(name), IsTextualExtension(name), isSymlink));
        }
        return entries;
    }

    private static bool TryNormalize(string input, out string path, out IResult? failure)
    {
        try
        {
            path = HostPathResolver.NormalizePosixPath(input);
            failure = null;
            return true;
        }
        catch (Exception ex)
        {
            path = "";
            failure = Results.BadRequest(new MessageResponse(ex.Message));
            return false;
        }
    }

    private static string? Parent(string normalized)
    {
        if (normalized == "/") return null;
        var index = normalized.LastIndexOf('/');
        return index <= 0 ? "/" : normalized[..index];
    }

    private static string Name(string normalized) => FileBrowserPath.Name(normalized);
    private static string Join(string dir, string name) => FileBrowserPath.Join(dir, name);

    private static bool IsTextualExtension(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext is ".txt" or ".json" or ".yaml" or ".yml" or ".xml" or ".conf" or ".config" or ".ini" or ".sh" or ".env" or ".log" or ".md" or ".js" or ".ts" or ".py" or ".cs" or ".go" or ".html" or ".css" or ".dockerfile";
    }
}
