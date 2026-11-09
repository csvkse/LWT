namespace LinuxWebTool.WebHost.Features.Files.Adapters;

/// <summary>
/// 文件系统目标适配器统一契约：
/// 隔离当前容器环境与宿主穿透环境的底层文件系统实现差异，提供统一的文件浏览、读写与传输能力。
/// </summary>
public interface IFileSystemTargetAdapter
{
    /// <summary>
    /// 目标源唯一标识（container | host）
    /// </summary>
    string TargetId { get; }

    /// <summary>
    /// 列出目录条目
    /// </summary>
    Task<IResult> ListAsync(string path, HttpContext httpContext, CancellationToken ct = default);

    /// <summary>
    /// 读取文本内容（支持大文本阻断与二进制文件探测）
    /// </summary>
    Task<IResult> ReadContentAsync(string path, HttpContext httpContext, CancellationToken ct = default);

    /// <summary>
    /// 写入文本文件（支持新建与覆盖）
    /// </summary>
    Task<IResult> WriteContentAsync(string path, string? content, HttpContext httpContext, CancellationToken ct = default);

    /// <summary>
    /// 创建目录
    /// </summary>
    Task<IResult> MkdirAsync(string path, HttpContext httpContext, CancellationToken ct = default);

    /// <summary>
    /// 重命名或移动文件/目录
    /// </summary>
    Task<IResult> RenameAsync(string from, string to, HttpContext httpContext, CancellationToken ct = default);

    /// <summary>
    /// 删除文件或目录
    /// </summary>
    Task<IResult> DeleteAsync(string path, bool recursive, HttpContext httpContext, CancellationToken ct = default);

    /// <summary>
    /// 上传文件
    /// </summary>
    Task<IResult> UploadAsync(string path, IFormFile? file, HttpContext httpContext, CancellationToken ct = default);

    /// <summary>
    /// 下载文件或将目录打包为 ZIP 下载
    /// </summary>
    Task<IResult> DownloadAsync(string path, HttpContext httpContext, CancellationToken ct = default);
}
