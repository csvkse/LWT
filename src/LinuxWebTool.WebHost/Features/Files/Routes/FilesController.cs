using LinuxWebTool.WebHost.Features.Files.Adapters;
using LinuxWebTool.WebHost.Features.Files.Factories;

namespace LinuxWebTool.WebHost.Features.Files.Routes;

/// <summary>
/// 文件管理器 API 控制器：
/// 浏览、查看、编辑、新建、重命名、删除、上传、打包下载文件与目录。
/// 支持多目标源调度：📦 当前环境（container）与 🖥️ 宿主主机（host，通过特权容器 nsenter 穿透）。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class FilesController(FileSystemAdapterFactory adapterFactory) : MinimalApi.ControllerBase
{
    private IFileSystemTargetAdapter ResolveAdapter(string? target)
    {
        if (!string.IsNullOrWhiteSpace(target))
        {
            HttpContext.Items["FilesTarget"] = target;
        }
        return adapterFactory.GetAdapter(target);
    }

    // ---------- 目录列表 ----------

    /// <summary>列出目录内容。支持通过 target=container|host 切换执行环境目标。</summary>
    [HttpGet]
    public async Task<IResult> List([FromQuery] string path, [FromQuery] string target = "container")
    {
        var adapter = ResolveAdapter(target);
        return await adapter.ListAsync(path, HttpContext, HttpContext.RequestAborted);
    }

    // ---------- 文本读取 / 写入 ----------

    /// <summary>读取文本文件内容（二进制/超限拒绝）。</summary>
    [HttpGet("Content")]
    public async Task<IResult> ReadContent([FromQuery] string path, [FromQuery] string target = "container")
    {
        var adapter = ResolveAdapter(target);
        return await adapter.ReadContentAsync(path, HttpContext, HttpContext.RequestAborted);
    }

    /// <summary>写文本文件（新建或覆盖）。父目录必须存在。</summary>
    [HttpPost("Content")]
    public async Task<IResult> WriteContent([FromBody] SaveTextRequest request)
    {
        var adapter = ResolveAdapter(request.Target);
        return await adapter.WriteContentAsync(request.Path, request.Content, HttpContext, HttpContext.RequestAborted);
    }

    // ---------- 增删改 ----------

    /// <summary>创建文件夹。支持通过 target 切换目标环境。</summary>
    [HttpPost("Mkdir")]
    public async Task<IResult> Mkdir([FromBody] PathRequest request)
    {
        var adapter = ResolveAdapter(request.Target);
        return await adapter.MkdirAsync(request.Path, HttpContext, HttpContext.RequestAborted);
    }

    /// <summary>重命名或移动文件 / 目录。不能移动到已存在的路径。</summary>
    [HttpPost("Rename")]
    public async Task<IResult> Rename([FromBody] RenameRequest request)
    {
        var adapter = ResolveAdapter(request.Target);
        return await adapter.RenameAsync(request.From, request.To, HttpContext, HttpContext.RequestAborted);
    }

    /// <summary>删除文件或目录（目录递归需 recursive=true）。受保护核心目录禁止删除。</summary>
    [HttpDelete]
    public async Task<IResult> Delete([FromQuery] string path, [FromQuery] bool recursive = false, [FromQuery] string target = "container")
    {
        var adapter = ResolveAdapter(target);
        return await adapter.DeleteAsync(path, recursive, HttpContext, HttpContext.RequestAborted);
    }

    /// <summary>上传文件到目录（一次一个）。重名自动加序号。</summary>
    [HttpPost("Upload")]
    public async Task<IResult> Upload([FromQuery] string path, IFormFile? file, [FromQuery] string target = "container")
    {
        var adapter = ResolveAdapter(target);
        return await adapter.UploadAsync(path, file, HttpContext, HttpContext.RequestAborted);
    }

    /// <summary>下载文件或将文件夹打包为 zip 下载。</summary>
    [HttpGet("Download")]
    public async Task<IResult> Download([FromQuery] string path, [FromQuery] string target = "container")
    {
        var adapter = ResolveAdapter(target);
        return await adapter.DownloadAsync(path, HttpContext, HttpContext.RequestAborted);
    }

    // ---------- 请求模型 ----------

    public sealed record PathRequest
    {
        public required string Path { get; init; }
        public string Target { get; init; } = "container";
    }

    public sealed record SaveTextRequest
    {
        public required string Path { get; init; }
        public string? Content { get; init; }
        public string Target { get; init; } = "container";
    }

    public sealed record RenameRequest
    {
        public required string From { get; init; }
        public required string To { get; init; }
        public string Target { get; init; } = "container";
    }
}
