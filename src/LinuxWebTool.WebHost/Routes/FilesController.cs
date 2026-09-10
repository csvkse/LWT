using System.Text;
using LinuxWebTool.Infrastructure.Support;
using LinuxWebTool.WebHost.Extensions;
using Directory = System.IO.Directory;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>
/// 文件管理器：浏览系统文件夹 / 文件，常见文本文件的查看 / 编辑与增删改查。
/// 仅 Linux 绝对路径；破坏性操作（删除 / 重命名 / 新建）守卫根目录与程序数据目录。
/// 与「任意指令执行」同属管理员威胁模型，页面不暴露公网即可。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FilesController(
    DataPaths dataPaths,
    IOperationLogger operationLogger) : ControllerBase
{
    /// <summary>文本查看 / 编辑上限（2MB），超限提示下载而不是误读大文件。</summary>
    private const long MaxTextBytes = 2 * 1024 * 1024;

    // ---------- 目录列表 ----------

    /// <summary>列出目录内容。path 必须是存在的目录。</summary>
    [HttpGet]
    public IActionResult List([FromQuery] string path)
    {
        var normalized = NormalizePosix(path);
        if (!Directory.Exists(normalized))
        {
            return NotFound(new MessageResponse($"路径不存在或不是目录：{normalized}"));
        }

        List<object> entries;
        try
        {
            entries = BuildEntries(normalized);
        }
        catch (UnauthorizedAccessException)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new MessageResponse($"无权限读取该目录：{normalized}"));
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new MessageResponse($"读取目录失败：{ex.Message}"));
        }

        return Ok(new
        {
            path = normalized,
            parent = Parent(normalized),
            name = Name(normalized),
            isRoot = normalized == "/",
            entries,
        });
    }

    private static List<object> BuildEntries(string normalized)
    {
        var entries = new List<object>();
        foreach (var dir in Directory.EnumerateDirectories(normalized).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Name(dir);
            entries.Add(Entry(normalized, name, isDirectory: true, size: null, modified: System.IO.File.GetLastWriteTime(dir)));
        }
        foreach (var file in Directory.EnumerateFiles(normalized).OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Name(file);
            entries.Add(Entry(normalized, name, isDirectory: false, size: new System.IO.FileInfo(file).Length, modified: System.IO.File.GetLastWriteTime(file)));
        }
        return entries;
    }

    private static object Entry(string dir, string name, bool isDirectory, long? size, DateTime modified) => new
    {
        name,
        path = Join(dir, name),
        isDirectory,
        size = isDirectory ? 0 : size ?? 0,
        modified,
        extension = isDirectory ? string.Empty : Path.GetExtension(name),
        isTextual = !isDirectory && IsTextExtension(name),
    };

    private static string Join(string dir, string name) => dir == "/" ? "/" + name : dir + "/" + name;

    // ---------- 文本读取 / 写入 ----------

    /// <summary>读取文本文件内容（二进制 / 超限拒绝）。</summary>
    [HttpGet("Content")]
    public async Task<IActionResult> ReadContent([FromQuery] string path)
    {
        var normalized = NormalizePosix(path);
        if (!System.IO.File.Exists(normalized))
        {
            return NotFound(new MessageResponse($"文件不存在：{normalized}"));
        }

        var info = new System.IO.FileInfo(normalized);
        if (info.Length > MaxTextBytes)
        {
            return BadRequest(new ReadFileErrorResponse($"文件超过 {MaxTextBytes / 1024 / 1024}MB，无法以文本查看，请直接用系统工具处理", true, false));
        }

        // 二进制探测：前 8KB 内出现 NUL 字节即以二进制处理。
        using (var fs = new System.IO.FileStream(normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            var buffer = new byte[Math.Min((int)info.Length, 8192)];
            var read = await fs.ReadAsync(buffer);
            if (buffer.Take(read).Contains((byte)0))
            {
                return BadRequest(new ReadFileErrorResponse("二进制文件，无法以文本查看", false, true));
            }
        }

        var content = await System.IO.File.ReadAllTextAsync(normalized, Encoding.UTF8);
        return Ok(new FileContentResponse(normalized, info.Name, info.Length, content));
    }

    /// <summary>写文本文件（新建或覆盖）。父目录必须存在。</summary>
    [HttpPost("Content")]
    public async Task<IActionResult> WriteContent([FromBody] SaveTextRequest request)
    {
        var normalized = NormalizePosix(request.Path);
        if (IsProtected(normalized))
        {
            return BadRequest(new MessageResponse("程序数据目录禁止写入"));
        }
        var parent = Path.GetDirectoryName(normalized);
        if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
        {
            return BadRequest(new MessageResponse("目标目录不存在，请先创建目录"));
        }
        if (Encoding.UTF8.GetByteCount(request.Content ?? string.Empty) > MaxTextBytes)
        {
            return BadRequest(new MessageResponse("内容超过大小限制"));
        }

        await System.IO.File.WriteAllTextAsync(normalized, request.Content ?? string.Empty, new UTF8Encoding(false));
        await operationLogger.LogAsync("写文本文件", "文件管理", Name(normalized), parent, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已保存"));
    }

    // ---------- 目录 / 文件操作 ----------

    /// <summary>新建目录（可多级）。</summary>
    [HttpPost("Mkdir")]
    public async Task<IActionResult> Mkdir([FromBody] PathRequest request)
    {
        var normalized = NormalizePosix(request.Path);
        if (IsProtected(normalized))
        {
            return BadRequest(new MessageResponse("程序数据目录禁止写入"));
        }
        if (Directory.Exists(normalized) || System.IO.File.Exists(normalized))
        {
            return BadRequest(new MessageResponse("同名文件或目录已存在"));
        }

        try
        {
            Directory.CreateDirectory(normalized);
        }
        catch (Exception ex)
        {
            return BadRequest(new MessageResponse($"创建目录失败：{ex.Message}"));
        }
        await operationLogger.LogAsync("新建文件夹", "文件管理", Name(normalized), Parent(normalized), clientIp: HttpContext.GetClientIp());
        return Ok(new CreateFileResponse("已创建", normalized));
    }

    /// <summary>重命名 / 移动文件或目录。</summary>
    [HttpPost("Rename")]
    public async Task<IActionResult> Rename([FromBody] RenameRequest request)
    {
        var from = NormalizePosix(request.From);
        var to = NormalizePosix(request.To);
        if (from == "/" || to == "/")
        {
            return BadRequest(new MessageResponse("不能对根目录进行操作"));
        }
        if (IsProtected(from) || IsProtected(to))
        {
            return BadRequest(new MessageResponse("程序数据目录禁止重命名 / 移动"));
        }

        if (System.IO.File.Exists(from))
        {
            if (System.IO.File.Exists(to)) return BadRequest(new MessageResponse("目标已存在同名文件"));
            System.IO.File.Move(from, to);
        }
        else if (Directory.Exists(from))
        {
            if (Directory.Exists(to)) return BadRequest(new MessageResponse("目标已存在同名目录"));
            Directory.Move(from, to);
        }
        else
        {
            return NotFound(new MessageResponse("源路径不存在"));
        }
        await operationLogger.LogAsync("重命名", "文件管理", Name(from), $"{from} → {to}", clientIp: HttpContext.GetClientIp());
        return Ok(new RenameFileResponse("已重命名", to));
    }

    /// <summary>删除文件或目录（目录递归需 recursive=true）。</summary>
    [HttpDelete]
    public async Task<IActionResult> Delete([FromQuery] string path, [FromQuery] bool recursive = false)
    {
        var normalized = NormalizePosix(path);
        if (normalized == "/")
        {
            return BadRequest(new MessageResponse("不能删除根目录"));
        }
        if (IsProtected(normalized))
        {
            return BadRequest(new MessageResponse("程序数据目录禁止删除"));
        }

        if (System.IO.File.Exists(normalized))
        {
            System.IO.File.Delete(normalized);
        }
        else if (Directory.Exists(normalized))
        {
            if (!recursive && Directory.EnumerateFileSystemEntries(normalized).Any())
            {
                return BadRequest(new DeleteFileErrorResponse("目录非空，确认后使用递归删除", true));
            }
            Directory.Delete(normalized, recursive);
        }
        else
        {
            return NotFound(new MessageResponse("路径不存在"));
        }
        await operationLogger.LogAsync("删除", "文件管理", Name(normalized), Parent(normalized), success: true, clientIp: HttpContext.GetClientIp());
        return Ok(new MessageResponse("已删除"));
    }

    /// <summary>上传文件到目录（一次一个）。重名自动加序号。</summary>
    [HttpPost("Upload")]
    public async Task<IActionResult> Upload([FromQuery] string path, IFormFile? file)
    {
        var normalized = NormalizePosix(path);
        if (!Directory.Exists(normalized))
        {
            return BadRequest(new MessageResponse($"目录不存在：{normalized}"));
        }
        if (file is null || file.Length == 0)
        {
            return BadRequest(new MessageResponse("未选择文件"));
        }

        var fileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return BadRequest(new MessageResponse("文件名无效"));
        }

        var target = UniquePath(normalized, fileName);
        await using (var stream = System.IO.File.Create(target))
        {
            await file.CopyToAsync(stream);
        }
        await operationLogger.LogAsync("上传文件", "文件管理", fileName, normalized, clientIp: HttpContext.GetClientIp());
        return Ok(new UploadFileResponse("已上传", target));
    }

    // ---------- 校验与工具 ----------

    private bool IsProtected(string normalized)
    {
        // 本功能面向 Linux；Windows 开发机上 data 根为盘符路径，POSIX 归一化不适用，跳过保护（仅调试用）。
        var dataRoot = dataPaths.Root.Replace('\\', '/');
        if (!dataRoot.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }
        dataRoot = NormalizePosix(dataRoot);
        if (dataRoot == "/" || dataRoot.Length == 0)
        {
            return false;
        }
        return normalized == dataRoot || normalized.StartsWith(dataRoot + "/", StringComparison.Ordinal);
    }

    private static string UniquePath(string dir, string fileName)
    {
        var target = Join(dir, fileName);
        if (!System.IO.File.Exists(target) && !Directory.Exists(target))
        {
            return target;
        }
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; i <= 999; i++)
        {
            target = Join(dir, $"{stem}({i}){ext}");
            if (!System.IO.File.Exists(target) && !Directory.Exists(target))
            {
                return target;
            }
        }
        throw new IOException("重名文件过多，无法生成唯一文件名");
    }

    /// <summary>POSIX 风格归一化：\\→/，折叠 . 与 ..，保证单斜杠前缀并去尾斜杠（根目录保留 /）。</summary>
    private static string NormalizePosix(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("路径不能为空");
        }
        var p = input.Trim().Replace('\\', '/');
        var segments = new List<string>();
        foreach (var seg in p.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".")
            {
                continue;
            }
            if (seg == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(seg);
        }
        return "/" + string.Join('/', segments);
    }

    /// <summary>取上一级路径；根目录返回 null。/a/b → /a；/a → /。</summary>
    private static string? Parent(string normalized)
    {
        if (normalized == "/")
        {
            return null;
        }
        var trimmed = normalized.TrimEnd('/');
        var idx = trimmed.LastIndexOf('/');
        return idx <= 0 ? "/" : trimmed[..idx];
    }

    private static string Name(string normalized)
    {
        var trimmed = normalized.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return "/";
        }
        var idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".js", ".mjs", ".cjs", ".ts", ".py", ".sh", ".bash", ".zsh",
        ".yml", ".yaml", ".xml", ".html", ".htm", ".css", ".csv", ".conf", ".ini", ".toml",
        ".log", ".env", ".properties", ".cs", ".rs", ".c", ".h", ".cpp", ".hpp", ".java", ".go",
        ".sql", ".bat", ".ps1", ".vim", ".gitignore", ".dockerignore", ".editorconfig", ".csproj",
        ".sln", ".slnx", ".cs", ".css", ".scss",
    };

    private static bool IsTextExtension(string name)
    {
        var ext = System.IO.Path.GetExtension(name);
        if (TextExtensions.Contains(ext))
        {
            return true;
        }
        // 无扩展名且非特殊隐藏文件（如 LICENSE、README、Dockerfile）按文本处理。
        return ext.Length == 0 && !name.StartsWith(".", StringComparison.Ordinal);
    }

    // ---------- 请求模型 ----------

    public sealed record PathRequest
    {
        public required string Path { get; init; }
    }

    public sealed record SaveTextRequest
    {
        public required string Path { get; init; }
        public string? Content { get; init; }
    }

    public sealed record RenameRequest
    {
        public required string From { get; init; }
        public required string To { get; init; }
    }
}
