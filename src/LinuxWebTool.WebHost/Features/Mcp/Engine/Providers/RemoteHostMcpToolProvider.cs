using System.Diagnostics;
using System.Text;
using System.Text.Json;
using LinuxWebTool.Application.Features.Security;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Infrastructure.Features.RemoteHost.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 远程受控主机纳管、终端指令与 SFTP 文件系统 MCP 工具提供者
/// </summary>
public sealed class RemoteHostMcpToolProvider(
    RemoteHostStore remoteHostStore,
    RemoteHostClientFactory remoteHostFactory,
    IOperationLogger operationLogger,
    ILogger<RemoteHostMcpToolProvider> logger) : IMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly JsonElement EmptyObjectSchema = Schema("""{"type":"object","properties":{}}""");

    private static readonly JsonElement RemoteHostIdRequiredSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "host_id": { "type": "string", "description": "受控主机 ID" }
        },
        "required": ["host_id"]
    }
    """);

    private static readonly JsonElement RemoteExecuteCommandSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "host_id": { "type": "string", "description": "受控主机 ID" },
            "command": { "type": "string", "description": "要在远程受控主机上执行的 Shell 命令行或脚本" },
            "timeout_seconds": { "type": "integer", "description": "执行超时秒数，默认 30，上限 300" }
        },
        "required": ["host_id", "command"]
    }
    """);

    private static readonly JsonElement RemoteListDirectorySchema = Schema("""
    {
        "type": "object",
        "properties": {
            "host_id": { "type": "string", "description": "受控主机 ID" },
            "path": { "type": "string", "description": "远程目录绝对路径，默认 /" }
        },
        "required": ["host_id"]
    }
    """);

    private static readonly JsonElement RemoteReadFileSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "host_id": { "type": "string", "description": "受控主机 ID" },
            "path": { "type": "string", "description": "远程文件绝对路径" },
            "max_bytes": { "type": "integer", "description": "最大读取字节数，默认 2097152 (2MB)" }
        },
        "required": ["host_id", "path"]
    }
    """);

    private static readonly JsonElement RemoteWriteFileSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "host_id": { "type": "string", "description": "受控主机 ID" },
            "path": { "type": "string", "description": "远程文件绝对路径" },
            "content": { "type": "string", "description": "待写入的文本内容" }
        },
        "required": ["host_id", "path", "content"]
    }
    """);

    public string ModuleName => "remote";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowRemoteHosts;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition(
                "remote_list_hosts",
                "查询已纳管的远程受控主机列表（自动根据 API Key 授权白名单进行范围投影）",
                EmptyObjectSchema),

            new McpToolDefinition(
                "remote_test_connection",
                "对指定远程受控主机发起网络连通与认证探测，返回往返延迟(ms)",
                RemoteHostIdRequiredSchema),

            new McpToolDefinition(
                "remote_execute_command",
                "在指定远程受控主机上执行非交互式 Shell 指令，获取 stdout/stderr、退出码与执行耗时",
                RemoteExecuteCommandSchema),

            new McpToolDefinition(
                "remote_list_directory",
                "浏览指定远程受控主机的 SFTP 目录，列出文件与子目录的名称、大小、权限与更新时间",
                RemoteListDirectorySchema),

            new McpToolDefinition(
                "remote_read_file",
                "读取指定远程受控主机上的文本文件内容（单次读取上限 2MB）",
                RemoteReadFileSchema),

            new McpToolDefinition(
                "remote_write_file",
                "向指定远程受控主机写入或覆盖保存文本文件内容",
                RemoteWriteFileSchema)
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
            case "remote_list_hosts":
            {
                var entities = await remoteHostStore.GetAllAsync();
                var visibleHosts = context.CallerKey == null
                    ? entities
                    : entities.Where(h => ApiKeyPermissionMatrix.CanAccessRemoteHost(context.CallerKey.AllowRemoteHosts, context.CallerKey.AllowedHostIds, h.Id)).ToList();

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartArray();
                    foreach (var h in visibleHosts)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", h.Id);
                        writer.WriteString("name", h.Name);
                        writer.WriteString("host", h.Host);
                        writer.WriteNumber("port", h.Port);
                        writer.WriteString("protocol", h.Protocol);
                        writer.WriteString("auth_type", h.AuthType);
                        writer.WriteString("username", h.Username);
                        if (!string.IsNullOrEmpty(h.HostKeyFingerprint)) writer.WriteString("host_key_fingerprint", h.HostKeyFingerprint);
                        if (!string.IsNullOrEmpty(h.Tags)) writer.WriteString("tags", h.Tags);
                        if (!string.IsNullOrEmpty(h.Remark)) writer.WriteString("remark", h.Remark);
                        writer.WriteNumber("sort_order", h.SortOrder);
                        if (h.LastConnectedAt.HasValue) writer.WriteString("last_connected_at", h.LastConnectedAt.Value.ToString("O"));
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "remote_test_connection":
            {
                if (!args.TryGetProperty("host_id", out var hProp))
                    return McpToolExecutionResult.Error("Missing host_id parameter");

                var hostId = hProp.GetString()!;
                if (context.CallerKey != null && !ApiKeyPermissionMatrix.CanAccessRemoteHost(context.CallerKey.AllowRemoteHosts, context.CallerKey.AllowedHostIds, hostId))
                {
                    return McpToolExecutionResult.Error($"Permission Denied: API Key is not authorized to access host '{hostId}'.");
                }

                var entity = await remoteHostStore.GetByIdAsync(hostId);
                if (entity == null) return McpToolExecutionResult.Error($"Remote host not found: '{hostId}'.");

                var sw = Stopwatch.StartNew();
                try
                {
                    await using var adapter = remoteHostFactory.CreateAdapter(entity);
                    var ok = await adapter.TestConnectionAsync(context.CancellationToken);
                    sw.Stop();
                    if (ok)
                    {
                        return McpToolExecutionResult.Success($"{{\"success\":true, \"latency_ms\":{sw.ElapsedMilliseconds}}}");
                    }
                    return McpToolExecutionResult.Success("{\"success\":false, \"error\":\"连接或认证失败\"}");
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        writer.WriteBoolean("success", false);
                        writer.WriteString("error", ex.Message);
                        writer.WriteEndObject();
                    }
                    return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }

            case "remote_execute_command":
            {
                if (!args.TryGetProperty("host_id", out var hProp) || !args.TryGetProperty("command", out var cProp))
                    return McpToolExecutionResult.Error("Missing host_id or command parameter");

                var hostId = hProp.GetString()!;
                if (context.CallerKey != null && !ApiKeyPermissionMatrix.CanAccessRemoteHost(context.CallerKey.AllowRemoteHosts, context.CallerKey.AllowedHostIds, hostId))
                {
                    return McpToolExecutionResult.Error($"Permission Denied: API Key is not authorized to access host '{hostId}'.");
                }

                var entity = await remoteHostStore.GetByIdAsync(hostId);
                if (entity == null) return McpToolExecutionResult.Error($"Remote host not found: '{hostId}'.");

                var command = cProp.GetString() ?? string.Empty;
                var timeout = args.TryGetProperty("timeout_seconds", out var tP) ? tP.GetInt32() : 30;
                timeout = Math.Clamp(timeout, 1, 300);

                try
                {
                    await using var adapter = remoteHostFactory.CreateAdapter(entity);
                    var res = await adapter.ExecuteCommandAsync(command, TimeSpan.FromSeconds(timeout), context.CancellationToken);
                    await remoteHostStore.TouchConnectedAsync(entity.Id);
                    await operationLogger.LogAsync("MCP远程执行指令", "远程运维", entity.Name, $"{command[..Math.Min(command.Length, 60)]} (Exit={res.ExitCode})", clientIp: context.ClientIp);

                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("exit_code", res.ExitCode);
                        writer.WriteString("stdout", res.Output);
                        writer.WriteString("stderr", res.ErrorOutput);
                        writer.WriteNumber("duration_ms", res.DurationMs);
                        writer.WriteEndObject();
                    }
                    return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP 远程执行指令异常: HostId={HostId}, Cmd={Cmd}", hostId, command);
                    return McpToolExecutionResult.Error($"Remote execution failed: {ex.Message}");
                }
            }

            case "remote_list_directory":
            {
                if (!args.TryGetProperty("host_id", out var hProp))
                    return McpToolExecutionResult.Error("Missing host_id parameter");

                var hostId = hProp.GetString()!;
                if (context.CallerKey != null && !ApiKeyPermissionMatrix.CanAccessRemoteHost(context.CallerKey.AllowRemoteHosts, context.CallerKey.AllowedHostIds, hostId))
                {
                    return McpToolExecutionResult.Error($"Permission Denied: API Key is not authorized to access host '{hostId}'.");
                }

                var entity = await remoteHostStore.GetByIdAsync(hostId);
                if (entity == null) return McpToolExecutionResult.Error($"Remote host not found: '{hostId}'.");

                var path = args.TryGetProperty("path", out var pP) ? pP.GetString() : "/";
                path = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();

                try
                {
                    await using var adapter = remoteHostFactory.CreateAdapter(entity);
                    await using var fs = await adapter.OpenFileSystemAsync(context.CancellationToken);
                    var entries = await fs.ListDirectoryAsync(path, context.CancellationToken);
                    await remoteHostStore.TouchConnectedAsync(entity.Id);

                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("current_path", path);
                        writer.WriteStartArray("entries");
                        foreach (var e in entries)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("name", e.Name);
                            writer.WriteString("path", e.Path);
                            writer.WriteNumber("size", e.Size);
                            writer.WriteBoolean("is_directory", e.IsDirectory);
                            if (e.LastModified.HasValue) writer.WriteString("last_modified", e.LastModified.Value.ToString("O"));
                            if (!string.IsNullOrEmpty(e.Permissions)) writer.WriteString("permissions", e.Permissions);
                            writer.WriteEndObject();
                        }
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                    return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP 远程读取目录异常: HostId={HostId}, Path={Path}", hostId, path);
                    return McpToolExecutionResult.Error($"Remote list directory failed: {ex.Message}");
                }
            }

            case "remote_read_file":
            {
                if (!args.TryGetProperty("host_id", out var hProp) || !args.TryGetProperty("path", out var pProp))
                    return McpToolExecutionResult.Error("Missing host_id or path parameter");

                var hostId = hProp.GetString()!;
                if (context.CallerKey != null && !ApiKeyPermissionMatrix.CanAccessRemoteHost(context.CallerKey.AllowRemoteHosts, context.CallerKey.AllowedHostIds, hostId))
                {
                    return McpToolExecutionResult.Error($"Permission Denied: API Key is not authorized to access host '{hostId}'.");
                }

                var entity = await remoteHostStore.GetByIdAsync(hostId);
                if (entity == null) return McpToolExecutionResult.Error($"Remote host not found: '{hostId}'.");

                var path = pProp.GetString()!;
                if (string.IsNullOrWhiteSpace(path)) return McpToolExecutionResult.Error("Path cannot be empty.");

                try
                {
                    await using var adapter = remoteHostFactory.CreateAdapter(entity);
                    await using var fs = await adapter.OpenFileSystemAsync(context.CancellationToken);
                    var content = await fs.ReadTextAsync(path.Trim(), context.CancellationToken);
                    await remoteHostStore.TouchConnectedAsync(entity.Id);

                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("path", path.Trim());
                        writer.WriteNumber("size", content.Length);
                        writer.WriteString("content", content);
                        writer.WriteEndObject();
                    }
                    return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP 远程读取文件异常: HostId={HostId}, Path={Path}", hostId, path);
                    return McpToolExecutionResult.Error($"Remote read file failed: {ex.Message}");
                }
            }

            case "remote_write_file":
            {
                if (!args.TryGetProperty("host_id", out var hProp) || !args.TryGetProperty("path", out var pProp) || !args.TryGetProperty("content", out var cProp))
                    return McpToolExecutionResult.Error("Missing host_id, path, or content parameter");

                var hostId = hProp.GetString()!;
                if (context.CallerKey != null && !ApiKeyPermissionMatrix.CanAccessRemoteHost(context.CallerKey.AllowRemoteHosts, context.CallerKey.AllowedHostIds, hostId))
                {
                    return McpToolExecutionResult.Error($"Permission Denied: API Key is not authorized to access host '{hostId}'.");
                }

                var entity = await remoteHostStore.GetByIdAsync(hostId);
                if (entity == null) return McpToolExecutionResult.Error($"Remote host not found: '{hostId}'.");

                var path = pProp.GetString()!;
                if (string.IsNullOrWhiteSpace(path)) return McpToolExecutionResult.Error("Path cannot be empty.");

                var content = cProp.GetString() ?? string.Empty;

                try
                {
                    await using var adapter = remoteHostFactory.CreateAdapter(entity);
                    await using var fs = await adapter.OpenFileSystemAsync(context.CancellationToken);
                    await fs.WriteTextAsync(path.Trim(), content, context.CancellationToken);
                    await remoteHostStore.TouchConnectedAsync(entity.Id);
                    await operationLogger.LogAsync("MCP远程写入文件", "远程运维", entity.Name, path, clientIp: context.ClientIp);

                    using var ms = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(ms))
                    {
                        writer.WriteStartObject();
                        writer.WriteString("path", path.Trim());
                        writer.WriteNumber("bytes_written", Encoding.UTF8.GetByteCount(content));
                        writer.WriteBoolean("success", true);
                        writer.WriteEndObject();
                    }
                    return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "MCP 远程写入文件异常: HostId={HostId}, Path={Path}", hostId, path);
                    return McpToolExecutionResult.Error($"Remote write file failed: {ex.Message}");
                }
            }

            default:
                return null;
        }
    }
}
