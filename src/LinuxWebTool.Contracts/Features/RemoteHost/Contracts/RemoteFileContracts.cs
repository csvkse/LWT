using System.Text.Json.Serialization;

namespace LinuxWebTool.Contracts.Features.RemoteHost.Contracts;

/// <summary>远程文件项元数据</summary>
public sealed record RemoteFileEntry(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("isDirectory")] bool IsDirectory,
    [property: JsonPropertyName("lastModified")] DateTimeOffset? LastModified,
    [property: JsonPropertyName("permissions")] string? Permissions);

/// <summary>远程文件列表响应契约</summary>
public sealed record RemoteFileListResponse(
    [property: JsonPropertyName("currentPath")] string CurrentPath,
    [property: JsonPropertyName("entries")] IReadOnlyList<RemoteFileEntry> Entries);

/// <summary>远程文本文件内容响应契约</summary>
public sealed record RemoteFileContentResponse(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("size")] long Size);

/// <summary>保存远程文本文件内容请求契约</summary>
public sealed record SaveRemoteFileContentRequest(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("content")] string Content);

/// <summary>远程文件目录管理动作请求契约</summary>
public sealed record RemoteFileActionRequest(
    [property: JsonPropertyName("action")] string Action, // "mkdir", "delete", "rename"
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("destination")] string? Destination = null,
    [property: JsonPropertyName("recursive")] bool Recursive = false);

/// <summary>远程文件系统抽象契约</summary>
public interface IRemoteFileSystem : IAsyncDisposable
{
    Task<IReadOnlyList<RemoteFileEntry>> ListDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task<Stream> OpenReadStreamAsync(string path, CancellationToken cancellationToken = default);
    Task WriteFileStreamAsync(string path, Stream content, CancellationToken cancellationToken = default);
    Task<string> ReadTextAsync(string path, CancellationToken cancellationToken = default);
    Task WriteTextAsync(string path, string content, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default);
    Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken = default);
    Task RenameAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken = default);
}
