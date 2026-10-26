using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Infrastructure.Features.Mount.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 文件系统与存储挂载 MCP 工具提供者
/// </summary>
public sealed class FileMcpToolProvider(
    DataPaths dataPaths,
    IDiskStatusCache diskStatusCache,
    MountStateMachineService mountHealth,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    private const long MaxTextReadBytes = 2 * 1024 * 1024; // 2MB 单次读取上限

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly JsonElement EmptyObjectSchema = Schema("""{"type":"object","properties":{}}""");

    private static readonly JsonElement FilePathRequiredSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "path": { "type": "string", "description": "绝对路径" }
        },
        "required": ["path"]
    }
    """);

    private static readonly JsonElement FileReadTextSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "path": { "type": "string", "description": "文件绝对路径" },
            "max_bytes": { "type": "integer", "description": "最大读取字节数" }
        },
        "required": ["path"]
    }
    """);

    private static readonly JsonElement FileWriteTextSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "path": { "type": "string", "description": "文件绝对路径" },
            "content": { "type": "string", "description": "待写入的文本内容" },
            "overwrite": { "type": "boolean", "description": "是否覆盖已存在文件" }
        },
        "required": ["path", "content"]
    }
    """);

    private static readonly JsonElement FileDeleteSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "path": { "type": "string", "description": "待删除路径" },
            "recursive": { "type": "boolean", "description": "非空目录是否递归删除" }
        },
        "required": ["path"]
    }
    """);

    public string ModuleName => "files";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowFiles;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition(
                "file_list_directory",
                "获取指定绝对路径下的文件与子目录列表",
                FilePathRequiredSchema),

            new McpToolDefinition(
                "file_read_text",
                "读取指定文本文件的内容（保护区核心数据库文件拒绝读取）",
                FileReadTextSchema),

            new McpToolDefinition(
                "file_write_text",
                "保存或覆写文本文件内容（保护区核心数据库文件拒绝写入）",
                FileWriteTextSchema),

            new McpToolDefinition(
                "file_create_directory",
                "创建多级目录",
                FilePathRequiredSchema),

            new McpToolDefinition(
                "file_delete",
                "删除文件或空目录（根目录受保护禁止删除）",
                FileDeleteSchema),

            new McpToolDefinition(
                "file_get_storage_status",
                "查询磁盘容量、使用率及 SMB/WebDAV/Rclone 存储挂载健康状态",
                EmptyObjectSchema)
        ];
    }

    public async ValueTask<McpToolExecutionResult?> TryExecuteAsync(
        string toolName,
        JsonElement? arguments,
        McpRequestContext context)
    {
        var args = arguments ?? default;

        switch (toolName)
        {
            case "file_list_directory":
            {
                if (!args.TryGetProperty("path", out var pProp))
                    return McpToolExecutionResult.Error("Missing path parameter");

                var dirPath = pProp.GetString()!;
                if (!PathScopeGuard.IsAllowed(dirPath, context.CallerKey?.AllowedPaths, out var pathErr))
                    return McpToolExecutionResult.Error($"Access Denied: {pathErr}");
                if (!Directory.Exists(dirPath)) return McpToolExecutionResult.Error($"Directory not found: {dirPath}");

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", dirPath.Replace('\\', '/'));
                    writer.WriteStartArray("entries");
                    foreach (var d in Directory.EnumerateDirectories(dirPath).Take(200))
                    {
                        var di = new DirectoryInfo(d);
                        writer.WriteStartObject();
                        writer.WriteString("name", di.Name);
                        writer.WriteString("path", di.FullName.Replace('\\', '/'));
                        writer.WriteBoolean("is_dir", true);
                        writer.WriteString("modified", di.LastWriteTimeUtc.ToString("O"));
                        writer.WriteEndObject();
                    }
                    foreach (var f in Directory.EnumerateFiles(dirPath).Take(300))
                    {
                        var fi = new FileInfo(f);
                        writer.WriteStartObject();
                        writer.WriteString("name", fi.Name);
                        writer.WriteString("path", fi.FullName.Replace('\\', '/'));
                        writer.WriteBoolean("is_dir", false);
                        writer.WriteNumber("size", fi.Length);
                        writer.WriteString("modified", fi.LastWriteTimeUtc.ToString("O"));
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "file_read_text":
            {
                if (!args.TryGetProperty("path", out var pProp))
                    return McpToolExecutionResult.Error("Missing path parameter");

                var filePath = pProp.GetString()!;
                if (!PathScopeGuard.IsAllowed(filePath, context.CallerKey?.AllowedPaths, out var pathErr))
                    return McpToolExecutionResult.Error($"Access Denied: {pathErr}");
                if (!File.Exists(filePath)) return McpToolExecutionResult.Error($"File not found: {filePath}");
                if (IsProtectedFile(filePath)) return McpToolExecutionResult.Error("Access denied: protected system file");

                var fi = new FileInfo(filePath);
                if (fi.Length > MaxTextReadBytes)
                {
                    return McpToolExecutionResult.Error($"File size ({fi.Length / 1024 / 1024}MB) exceeds text read limit (2MB)");
                }
                var content = await File.ReadAllTextAsync(filePath, context.CancellationToken);
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", filePath.Replace('\\', '/'));
                    writer.WriteNumber("size", fi.Length);
                    writer.WriteString("content", content);
                    writer.WriteEndObject();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "file_write_text":
            {
                if (!args.TryGetProperty("path", out var pProp))
                    return McpToolExecutionResult.Error("Missing path parameter");

                var filePath = pProp.GetString()!;
                var content = args.TryGetProperty("content", out var cProp) ? cProp.GetString() ?? string.Empty : string.Empty;
                var overwrite = !args.TryGetProperty("overwrite", out var owP) || owP.GetBoolean();
                if (!PathScopeGuard.IsAllowed(filePath, context.CallerKey?.AllowedPaths, out var pathErr))
                    return McpToolExecutionResult.Error($"Access Denied: {pathErr}");
                if (IsProtectedFile(filePath)) return McpToolExecutionResult.Error("Access denied: protected system file");
                if (File.Exists(filePath) && !overwrite) return McpToolExecutionResult.Error("File already exists and overwrite is false");

                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(filePath, content, context.CancellationToken);
                await operationLogger.LogAsync("MCP写文件", "文件管理", Path.GetFileName(filePath), filePath, clientIp: context.ClientIp);
                return McpToolExecutionResult.Success($"{{\"success\":true, \"path\":\"{filePath.Replace('\\', '/')}\", \"bytes\":{content.Length}}}");
            }

            case "file_create_directory":
            {
                if (!args.TryGetProperty("path", out var pProp))
                    return McpToolExecutionResult.Error("Missing path parameter");

                var dirPath = pProp.GetString()!;
                if (!PathScopeGuard.IsAllowed(dirPath, context.CallerKey?.AllowedPaths, out var pathErr))
                    return McpToolExecutionResult.Error($"Access Denied: {pathErr}");
                if (IsProtectedFile(dirPath)) return McpToolExecutionResult.Error("Access denied: protected system file");
                if (!Directory.Exists(dirPath)) Directory.CreateDirectory(dirPath);
                await operationLogger.LogAsync("MCP建目录", "文件管理", Path.GetFileName(dirPath), dirPath, clientIp: context.ClientIp);
                return McpToolExecutionResult.Success($"{{\"success\":true, \"path\":\"{dirPath.Replace('\\', '/')}\"}}");
            }

            case "file_delete":
            {
                if (!args.TryGetProperty("path", out var pProp))
                    return McpToolExecutionResult.Error("Missing path parameter");

                var filePath = pProp.GetString()!;
                if (!PathScopeGuard.IsAllowed(filePath, context.CallerKey?.AllowedPaths, out var pathErr))
                    return McpToolExecutionResult.Error($"Access Denied: {pathErr}");
                if (IsProtectedFile(filePath)) return McpToolExecutionResult.Error("Access denied: protected system file");
                if (File.Exists(filePath)) File.Delete(filePath);
                else if (Directory.Exists(filePath)) Directory.Delete(filePath, recursive: true);
                else return McpToolExecutionResult.Error($"Target not found: {filePath}");
                await operationLogger.LogAsync("MCP删除", "文件管理", Path.GetFileName(filePath), filePath, clientIp: context.ClientIp);
                return McpToolExecutionResult.Success($"{{\"success\":true, \"path\":\"{filePath.Replace('\\', '/')}\"}}");
            }

            case "file_get_storage_status":
            {
                var disks = diskStatusCache.GetSnapshot();
                var mounts = mountHealth.GetSnapshots();
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("disks");
                    foreach (var p in disks)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("mount", p.Mount);
                        writer.WriteString("total_gb", (p.TotalBytes / 1024.0 / 1024.0 / 1024.0).ToString("F1"));
                        writer.WriteNumber("used_percent", p.UsagePercent);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteStartArray("mounts");
                    foreach (var m in mounts)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("target", m.LocalPath);
                        writer.WriteString("healthy", m.State.ToString());
                        writer.WriteString("stage", m.ExecutionPhase.ToString());
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            default:
                return null;
        }
    }

    private bool IsProtectedFile(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        var dataDir = Path.GetFullPath(dataPaths.Root).Replace('\\', '/').ToLowerInvariant();
        return normalized.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase);
    }
}
