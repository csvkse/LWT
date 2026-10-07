using System.Text;
using System.Text.Json;
using LinuxWebTool.WebHost.Composition;
using Microsoft.Extensions.Logging;
namespace LinuxWebTool.WebHost.Features.Mcp.Engine;

/// <summary>
/// 纯原生 Native AOT 兼容的 MCP (Model Context Protocol) 核心分发引擎
/// 支持标准 JSON-RPC 2.0、动态权限投影与工业级安全沙箱
/// </summary>
public sealed class McpServerEngine(
    IShellExecutor shellExecutor,
    IPtySessionManager ptySessionManager,
    ScheduleStore scheduleStore,
    IScheduleManager scheduleManager,
    DataPaths dataPaths,
    IDiskStatusCache diskStatusCache,
    MountStateMachineService mountHealth,
    FfmpegLocator ffmpegLocator,
    TranscodePresetStore transcodePresetStore,
    TranscodeJobStore transcodeJobStore,
    TranscodeQueueService transcodeQueueService,
    GatewayStore gatewayStore,
    IOperationLogger operationLogger,
    ILogger<McpServerEngine> logger)
{
    private const long MaxTextReadBytes = 2 * 1024 * 1024; // 2MB 单次读取上限

    private static readonly JsonElement EmptyElement = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly JsonElement InitializeElement = JsonDocument.Parse("""
    {
        "protocolVersion": "2024-11-05",
        "capabilities": {
            "tools": {}
        },
        "serverInfo": {
            "name": "LinuxWebTool-MCP-Server",
            "version": "2.0.0"
        }
    }
    """).RootElement.Clone();

    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly JsonElement EmptyObjectSchema = Schema("""{"type":"object","properties":{}}""");

    private static readonly JsonElement TerminalExecuteSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "command": { "type": "string", "description": "待执行的命令或脚本" },
            "working_directory": { "type": "string", "description": "执行工作目录（可选绝对路径）" },
            "timeout_seconds": { "type": "integer", "description": "执行超时秒数，默认 60，上限 300" }
        },
        "required": ["command"]
    }
    """);

    private static readonly JsonElement TerminalCreateSessionSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "executable": { "type": "string", "description": "终端程序，如 bash 或 sh" },
            "working_directory": { "type": "string", "description": "初始工作目录" }
        }
    }
    """);

    private static readonly JsonElement TerminalCloseSessionSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "session_id": { "type": "string", "description": "会话 ID" }
        },
        "required": ["session_id"]
    }
    """);

    private static readonly JsonElement ScheduleListSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "enabled_only": { "type": "boolean", "description": "是否仅返回已启用的任务" }
        }
    }
    """);

    private static readonly JsonElement ScheduleCreateSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "name": { "type": "string", "description": "任务名称" },
            "command_text": { "type": "string", "description": "待调度的执行命令" },
            "cron_expression": { "type": "string", "description": "Cron 表达式" },
            "arguments": { "type": "string", "description": "脚本参数" },
            "timeout_seconds": { "type": "integer", "description": "单次超时秒数" }
        },
        "required": ["name", "command_text", "cron_expression"]
    }
    """);

    private static readonly JsonElement ScheduleIdRequiredSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "schedule_id": { "type": "string", "description": "定时任务 ID" }
        },
        "required": ["schedule_id"]
    }
    """);

    private static readonly JsonElement ScheduleToggleSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "schedule_id": { "type": "string", "description": "定时任务 ID" },
            "enabled": { "type": "boolean", "description": "是否启用" }
        },
        "required": ["schedule_id", "enabled"]
    }
    """);

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

    private static readonly JsonElement TranscodeListJobsSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "page": { "type": "integer", "description": "页码，默认 1" },
            "page_size": { "type": "integer", "description": "每页大小，默认 10" }
        }
    }
    """);

    private static readonly JsonElement TranscodeSubmitJobSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "source_path": { "type": "string", "description": "源媒体文件绝对路径" },
            "preset_id": { "type": "string", "description": "转码预设 ID" },
            "custom_args": { "type": "string", "description": "自定义 ffmpeg 参数" },
            "output_dir": { "type": "string", "description": "目标输出目录" },
            "use_hardware_accel": { "type": "boolean", "description": "是否启用硬件加速" }
        },
        "required": ["source_path"]
    }
    """);

    private static readonly JsonElement TranscodeJobIdSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "job_id": { "type": "string", "description": "任务 ID" }
        },
        "required": ["job_id"]
    }
    """);

    public async Task<McpRpcResponse> HandleRequestAsync(McpRpcRequest request, ApiKeyEntity? callerKey, HttpContext httpContext)
    {
        try
        {
            return request.Method switch
            {
                "initialize" => new McpRpcResponse("2.0", request.Id, InitializeElement, null),
                "notifications/initialized" => new McpRpcResponse("2.0", request.Id, EmptyElement, null),
                "ping" => new McpRpcResponse("2.0", request.Id, EmptyElement, null),
                "tools/list" => HandleToolsList(request, callerKey),
                "tools/call" => await HandleToolCallAsync(request, callerKey, httpContext),
                _ => new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32601, $"Method not found: {request.Method}"))
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "处理 MCP 请求异常: {Method}", request.Method);
            return new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32603, $"Internal error: {ex.Message}"));
        }
    }

    private McpRpcResponse HandleToolsList(McpRpcRequest request, ApiKeyEntity? callerKey)
    {
        var tools = new List<McpToolDefinition>();

        // 依据 callerKey 进行动态投影过滤
        var allowTerminal = callerKey == null || callerKey.AllowTerminal;
        var allowSchedules = callerKey == null || callerKey.AllowSchedules;
        var allowFiles = callerKey == null || callerKey.AllowFiles;
        var allowTranscode = callerKey == null || callerKey.AllowTranscode;
        var allowGateway = callerKey == null || callerKey.AllowGateway;

        // 1. 终端模块工具
        if (allowTerminal)
        {
            tools.Add(new McpToolDefinition(
                "terminal_execute_command",
                "执行 Linux Shell 命令行或 Bash 脚本，获取 stdout/stderr 及耗时",
                TerminalExecuteSchema));

            tools.Add(new McpToolDefinition(
                "terminal_list_sessions",
                "列出当前活跃的交互式 PTY 虚拟终端会话",
                EmptyObjectSchema));

            tools.Add(new McpToolDefinition(
                "terminal_create_session",
                "新建一个后台运行的 PTY 虚拟终端会话",
                TerminalCreateSessionSchema));

            tools.Add(new McpToolDefinition(
                "terminal_close_session",
                "关闭并回收指定的 PTY 终端会话",
                TerminalCloseSessionSchema));
        }

        // 2. 定时任务模块工具
        if (allowSchedules)
        {
            tools.Add(new McpToolDefinition(
                "schedule_list",
                "查询系统中配置的定时调度任务列表",
                ScheduleListSchema));

            tools.Add(new McpToolDefinition(
                "schedule_create",
                "新建一条定时任务并自动挂载至 Quartz 调度器",
                ScheduleCreateSchema));

            tools.Add(new McpToolDefinition(
                "schedule_trigger_now",
                "立即触发一次指定的定时任务执行",
                ScheduleIdRequiredSchema));

            tools.Add(new McpToolDefinition(
                "schedule_toggle",
                "启用或暂停指定的定时任务",
                ScheduleToggleSchema));

            tools.Add(new McpToolDefinition(
                "schedule_delete",
                "删除指定的定时任务",
                ScheduleIdRequiredSchema));
        }

        // 3. 文件管理模块工具
        if (allowFiles)
        {
            tools.Add(new McpToolDefinition(
                "file_list_directory",
                "获取指定绝对路径下的文件与子目录列表",
                FilePathRequiredSchema));

            tools.Add(new McpToolDefinition(
                "file_read_text",
                "读取指定文本文件的内容（保护区核心数据库文件拒绝读取）",
                FileReadTextSchema));

            tools.Add(new McpToolDefinition(
                "file_write_text",
                "保存或覆写文本文件内容（保护区核心数据库文件拒绝写入）",
                FileWriteTextSchema));

            tools.Add(new McpToolDefinition(
                "file_create_directory",
                "创建多级目录",
                FilePathRequiredSchema));

            tools.Add(new McpToolDefinition(
                "file_delete",
                "删除文件或空目录（根目录受保护禁止删除）",
                FileDeleteSchema));

            tools.Add(new McpToolDefinition(
                "file_get_storage_status",
                "查询磁盘容量、使用率及 SMB/WebDAV/Rclone 存储挂载健康状态",
                EmptyObjectSchema));
        }

        // 4. 媒体转码模块工具
        if (allowTranscode)
        {
            tools.Add(new McpToolDefinition(
                "transcode_get_capabilities",
                "探查系统 FFmpeg 版本及显卡硬件加速（VAAPI/NVENC/QSV）支持状态",
                EmptyObjectSchema));

            tools.Add(new McpToolDefinition(
                "transcode_list_presets",
                "查询已配置的音视频转码规格预设列表",
                EmptyObjectSchema));

            tools.Add(new McpToolDefinition(
                "transcode_list_jobs",
                "分页查询转码排队队列与历史任务执行状态",
                TranscodeListJobsSchema));

            tools.Add(new McpToolDefinition(
                "transcode_submit_job",
                "提交单个媒体文件转码任务至异步处理队列",
                TranscodeSubmitJobSchema));

            tools.Add(new McpToolDefinition(
                "transcode_cancel_job",
                "取消正在排队或执行中的转码任务",
                TranscodeJobIdSchema));
        }

        // 5. 家庭网关模块工具
        if (allowGateway)
        {
            tools.Add(new McpToolDefinition(
                "gateway_list_routes",
                "查询家庭网关中已配置的 L7 HTTP 反向代理路由与网站代理",
                EmptyObjectSchema));
        }

        var element = JsonSerializer.SerializeToElement(new McpToolsListResult(tools), AppJsonSerializerContext.Default.McpToolsListResult);
        return new McpRpcResponse("2.0", request.Id, element, null);
    }

    private async Task<McpRpcResponse> HandleToolCallAsync(McpRpcRequest request, ApiKeyEntity? callerKey, HttpContext httpContext)
    {
        if (!request.Params.HasValue)
        {
            return new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32602, "Missing params"));
        }

        var root = request.Params.Value;
        if (!root.TryGetProperty("name", out var nameProp))
        {
            return new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32602, "Missing tool name"));
        }

        var toolName = nameProp.GetString() ?? string.Empty;
        var args = root.TryGetProperty("arguments", out var argsProp) ? argsProp : default;

        // 模块级双重安全防护拦截
        if (toolName.StartsWith("terminal_") && callerKey != null && !callerKey.AllowTerminal)
            return ToolErrorResponse(request.Id, "Permission Denied: API Key does not have 'Terminal' permission.");

        if (toolName.StartsWith("schedule_") && callerKey != null && !callerKey.AllowSchedules)
            return ToolErrorResponse(request.Id, "Permission Denied: API Key does not have 'Schedules' permission.");

        if (toolName.StartsWith("file_") && callerKey != null && !callerKey.AllowFiles)
            return ToolErrorResponse(request.Id, "Permission Denied: API Key does not have 'Files' permission.");

        if (toolName.StartsWith("transcode_") && callerKey != null && !callerKey.AllowTranscode)
            return ToolErrorResponse(request.Id, "Permission Denied: API Key does not have 'Transcode' permission.");

        if (toolName.StartsWith("gateway_") && callerKey != null && !callerKey.AllowGateway)
            return ToolErrorResponse(request.Id, "Permission Denied: API Key does not have 'Gateway' permission.");

        var clientIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        // 分发执行具体工具
        switch (toolName)
        {
            // === 终端工具 ===
            case "terminal_execute_command":
            {
                var command = args.GetProperty("command").GetString() ?? string.Empty;
                var cwd = args.TryGetProperty("working_directory", out var cwdP) ? cwdP.GetString() : null;
                var timeout = args.TryGetProperty("timeout_seconds", out var tP) ? tP.GetInt32() : 60;
                timeout = Math.Clamp(timeout, 1, 300);

                var shellReq = new ShellRequest
                {
                    CommandText = command,
                    WorkingDirectory = cwd,
                    TimeoutSeconds = timeout
                };
                var shellRes = await shellExecutor.ExecuteAsync(shellReq, httpContext.RequestAborted);
                await operationLogger.LogAsync("MCP执行指令", "终端", command[..Math.Min(command.Length, 60)], $"ExitCode={shellRes.ExitCode}", clientIp: clientIp);

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    if (shellRes.ExitCode.HasValue) writer.WriteNumber("exit_code", shellRes.ExitCode.Value);
                    else writer.WriteNull("exit_code");
                    writer.WriteString("stdout", shellRes.StandardOutput);
                    writer.WriteString("stderr", shellRes.ErrorOutput);
                    writer.WriteNumber("duration_ms", shellRes.DurationMs);
                    writer.WriteBoolean("timed_out", shellRes.TimedOut);
                    writer.WriteBoolean("truncated", shellRes.Truncated);
                    writer.WriteEndObject();
                }
                return ToolSuccessResponse(request.Id, Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "terminal_list_sessions":
            {
                var sessions = ptySessionManager.ListSessions();
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartArray();
                    foreach (var s in sessions)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("session_id", s.SessionId);
                        writer.WriteNumber("process_id", s.ProcessId);
                        writer.WriteString("working_dir", s.WorkingDirectory);
                        writer.WriteBoolean("has_exited", s.ExitCode.HasValue);
                        writer.WriteString("created_at", s.CreatedAt.ToString("O"));
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                return ToolSuccessResponse(request.Id, Encoding.UTF8.GetString(ms.ToArray()));
            }

            // === 定时任务工具 ===
            case "schedule_list":
            {
                var enabledOnly = args.TryGetProperty("enabled_only", out var eoP) && eoP.GetBoolean();
                var tasks = (await scheduleStore.GetAllAsync())
                    .Where(t => !enabledOnly || t.Enabled);
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartArray();
                    foreach (var t in tasks)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", t.Id.ToString());
                        writer.WriteString("name", t.Name);
                        writer.WriteString("cron", t.CronExpression);
                        writer.WriteBoolean("enabled", t.Enabled);
                        if (t.NextRunTime.HasValue) writer.WriteString("next_run", t.NextRunTime.Value.ToString("O"));
                        else writer.WriteNull("next_run");
                        if (t.LastRunTime.HasValue) writer.WriteString("last_run", t.LastRunTime.Value.ToString("O"));
                        else writer.WriteNull("last_run");
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                return ToolSuccessResponse(request.Id, Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "schedule_trigger_now":
            {
                var schedId = Guid.Parse(args.GetProperty("schedule_id").GetString()!);
                var task = await scheduleStore.GetByIdAsync(schedId);
                if (task == null) return ToolErrorResponse(request.Id, "Schedule task not found");
                await scheduleManager.TriggerNowAsync(task);
                await operationLogger.LogAsync("MCP触发定时任务", "定时任务", task.Name, schedId.ToString(), clientIp: clientIp);
                return ToolSuccessResponse(request.Id, $"{{\"success\":true, \"message\":\"Triggered {task.Name}\"}}");
            }

            case "schedule_toggle":
            {
                var schedId = Guid.Parse(args.GetProperty("schedule_id").GetString()!);
                var enabled = args.GetProperty("enabled").GetBoolean();
                var task = await scheduleStore.GetByIdAsync(schedId);
                if (task == null) return ToolErrorResponse(request.Id, "Schedule task not found");
                task.Enabled = enabled;
                await scheduleStore.UpdateAsync(task);
                if (enabled) await scheduleManager.SyncAsync(task);
                else await scheduleManager.RemoveAsync(schedId);
                await operationLogger.LogAsync("MCP切换定时任务", "定时任务", task.Name, $"Enabled={enabled}", clientIp: clientIp);
                return ToolSuccessResponse(request.Id, $"{{\"success\":true, \"enabled\":{enabled.ToString().ToLower()}}}");
            }

            // === 文件管理工具 ===
            case "file_list_directory":
            {
                var dirPath = args.GetProperty("path").GetString()!;
                if (!Directory.Exists(dirPath)) return ToolErrorResponse(request.Id, $"Directory not found: {dirPath}");

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
                return ToolSuccessResponse(request.Id, Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "file_read_text":
            {
                var filePath = args.GetProperty("path").GetString()!;
                if (!File.Exists(filePath)) return ToolErrorResponse(request.Id, $"File not found: {filePath}");
                if (IsProtectedFile(filePath)) return ToolErrorResponse(request.Id, "Access denied: protected system file");

                var fi = new FileInfo(filePath);
                if (fi.Length > MaxTextReadBytes)
                {
                    return ToolErrorResponse(request.Id, $"File size ({fi.Length / 1024 / 1024}MB) exceeds text read limit (2MB)");
                }
                var content = await File.ReadAllTextAsync(filePath, httpContext.RequestAborted);
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteString("path", filePath.Replace('\\', '/'));
                    writer.WriteNumber("size", fi.Length);
                    writer.WriteString("content", content);
                    writer.WriteEndObject();
                }
                return ToolSuccessResponse(request.Id, Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "file_write_text":
            {
                var filePath = args.GetProperty("path").GetString()!;
                var content = args.GetProperty("content").GetString() ?? string.Empty;
                var overwrite = !args.TryGetProperty("overwrite", out var owP) || owP.GetBoolean();
                if (IsProtectedFile(filePath)) return ToolErrorResponse(request.Id, "Access denied: protected system file");
                if (File.Exists(filePath) && !overwrite) return ToolErrorResponse(request.Id, "File already exists and overwrite is false");

                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(filePath, content, httpContext.RequestAborted);
                await operationLogger.LogAsync("MCP写文件", "文件管理", Path.GetFileName(filePath), filePath, clientIp: clientIp);
                return ToolSuccessResponse(request.Id, $"{{\"success\":true, \"path\":\"{filePath.Replace('\\', '/')}\", \"bytes\":{content.Length}}}");
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
                return ToolSuccessResponse(request.Id, Encoding.UTF8.GetString(ms.ToArray()));
            }

            // === 媒体转码工具 ===
            case "transcode_get_capabilities":
            {
                var det = await ffmpegLocator.DetectAsync();
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("available", det.Available);
                    writer.WriteString("version", det.Version);
                    writer.WriteStartArray("backends");
                    foreach (var b in det.HwBackends) writer.WriteStringValue(b);
                    writer.WriteEndArray();
                    writer.WriteStartArray("accels");
                    foreach (var a in det.HardwareAccels) writer.WriteStringValue(a);
                    writer.WriteEndArray();
                    writer.WriteString("gpu", det.GpuName);
                    writer.WriteEndObject();
                }
                return ToolSuccessResponse(request.Id, Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "transcode_list_presets":
            {
                var presets = await transcodePresetStore.GetAllAsync();
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartArray();
                    foreach (var p in presets)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", p.Id);
                        writer.WriteString("name", p.Name);
                        writer.WriteString("container", p.Container);
                        writer.WriteString("video_codec", p.VideoCodec);
                        writer.WriteBoolean("builtin", p.IsBuiltin);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                return ToolSuccessResponse(request.Id, Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "transcode_cancel_job":
            {
                var jobId = Guid.Parse(args.GetProperty("job_id").GetString()!);
                var job = await transcodeJobStore.GetByIdAsync(jobId);
                if (job == null) return ToolErrorResponse(request.Id, "Transcode job not found");
                await transcodeQueueService.CancelAsync(jobId);
                await operationLogger.LogAsync("MCP取消转码", "转码任务", Path.GetFileName(job.SourcePath), jobId.ToString(), clientIp: clientIp);
                return ToolSuccessResponse(request.Id, $"{{\"success\":true, \"message\":\"Cancelled job {jobId}\"}}");
            }

            // === 网关路由工具 ===
            case "gateway_list_routes":
            {
                var routes = await gatewayStore.GetAllRoutesAsync();
                var websites = await gatewayStore.GetAllWebsitesAsync();
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteStartArray("routes");
                    foreach (var r in routes)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("route_id", r.RouteId);
                        writer.WriteString("cluster_id", r.ClusterId);
                        writer.WriteString("match_path", r.MatchPath);
                        writer.WriteBoolean("enabled", r.IsEnabled);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteStartArray("websites");
                    foreach (var w in websites)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("name", w.Name);
                        writer.WriteString("target_url", w.TargetUrl);
                        writer.WriteBoolean("enabled", w.IsEnabled);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                return ToolSuccessResponse(request.Id, Encoding.UTF8.GetString(ms.ToArray()));
            }

            default:
                return new McpRpcResponse("2.0", request.Id, null, new McpRpcError(-32601, $"Unknown tool: {toolName}"));
        }
    }

    private static McpRpcResponse ToolSuccessResponse(string? id, string text)
    {
        var result = new McpCallToolResult([new McpToolContent("text", text)], false);
        var element = JsonSerializer.SerializeToElement(result, AppJsonSerializerContext.Default.McpCallToolResult);
        return new McpRpcResponse("2.0", id, element, null);
    }

    private static McpRpcResponse ToolErrorResponse(string? id, string errorMessage)
    {
        var result = new McpCallToolResult([new McpToolContent("text", errorMessage)], true);
        var element = JsonSerializer.SerializeToElement(result, AppJsonSerializerContext.Default.McpCallToolResult);
        return new McpRpcResponse("2.0", id, element, null);
    }

    private bool IsProtectedFile(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        var dataDir = Path.GetFullPath(dataPaths.Root).Replace('\\', '/').ToLowerInvariant();
        return normalized.StartsWith(dataDir, StringComparison.OrdinalIgnoreCase);
    }
}
