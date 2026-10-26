using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Infrastructure.Features.Transcode.Adapters;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using LinuxWebTool.Infrastructure.Shared.Persistence.Entities;
using LinuxWebTool.Infrastructure.Shared.Support;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

/// <summary>
/// 媒体转码 MCP 工具提供者
/// </summary>
public sealed class TranscodeMcpToolProvider(
    FfmpegLocator ffmpegLocator,
    TranscodePresetStore transcodePresetStore,
    TranscodeJobStore transcodeJobStore,
    TranscodeQueueService transcodeQueueService,
    IOperationLogger operationLogger) : IMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly JsonElement EmptyObjectSchema = Schema("""{"type":"object","properties":{}}""");

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
            "preset_id": { "type": "string", "description": "转码预设 ID (Guid)" },
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

    public string ModuleName => "transcode";

    public bool IsPermitted(ApiKeyEntity? callerKey) => callerKey == null || callerKey.AllowTranscode;

    public IReadOnlyList<McpToolDefinition> GetTools(ApiKeyEntity? callerKey)
    {
        return
        [
            new McpToolDefinition(
                "transcode_get_capabilities",
                "探查系统 FFmpeg 版本及显卡硬件加速（VAAPI/NVENC/QSV）支持状态",
                EmptyObjectSchema),

            new McpToolDefinition(
                "transcode_list_presets",
                "查询已配置的音视频转码规格预设列表",
                EmptyObjectSchema),

            new McpToolDefinition(
                "transcode_list_jobs",
                "分页查询转码排队队列与历史任务执行状态",
                TranscodeListJobsSchema),

            new McpToolDefinition(
                "transcode_submit_job",
                "提交单个媒体文件转码任务至异步处理队列",
                TranscodeSubmitJobSchema),

            new McpToolDefinition(
                "transcode_cancel_job",
                "取消正在排队或执行中的转码任务",
                TranscodeJobIdSchema)
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
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
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
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "transcode_list_jobs":
            {
                var page = args.TryGetProperty("page", out var pP) ? pP.GetInt32() : 1;
                var pageSize = args.TryGetProperty("page_size", out var psP) ? psP.GetInt32() : 10;
                var (items, total) = await transcodeJobStore.QueryAsync(page, pageSize, null, null);

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(ms))
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("total", total);
                    writer.WriteNumber("page", page);
                    writer.WriteNumber("page_size", pageSize);
                    writer.WriteStartArray("items");
                    foreach (var j in items)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("id", j.Id.ToString());
                        writer.WriteString("source_path", j.SourcePath);
                        if (!string.IsNullOrEmpty(j.OutputPath)) writer.WriteString("output_path", j.OutputPath);
                        if (!string.IsNullOrEmpty(j.PresetName)) writer.WriteString("preset_name", j.PresetName);
                        writer.WriteNumber("status", j.Status);
                        writer.WriteNumber("progress", j.Progress);
                        if (!string.IsNullOrEmpty(j.SpeedText)) writer.WriteString("speed", j.SpeedText);
                        if (!string.IsNullOrEmpty(j.ErrorOutput)) writer.WriteString("error", j.ErrorOutput);
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                return McpToolExecutionResult.Success(Encoding.UTF8.GetString(ms.ToArray()));
            }

            case "transcode_submit_job":
            {
                if (!args.TryGetProperty("source_path", out var spProp))
                    return McpToolExecutionResult.Error("Missing source_path parameter");

                var sourcePath = spProp.GetString()!;
                if (!File.Exists(sourcePath)) return McpToolExecutionResult.Error($"Source media file not found: {sourcePath}");

                Guid? presetId = null;
                if (args.TryGetProperty("preset_id", out var prProp) && Guid.TryParse(prProp.GetString(), out var parsedId))
                {
                    presetId = parsedId;
                }

                var customArgs = args.TryGetProperty("custom_args", out var caProp) ? caProp.GetString() : null;
                var outputDir = args.TryGetProperty("output_dir", out var odProp) ? odProp.GetString() : Path.GetDirectoryName(sourcePath);
                var useHw = !args.TryGetProperty("use_hardware_accel", out var uhProp) || uhProp.GetBoolean();

                var ext = "mp4";
                var baseName = Path.GetFileNameWithoutExtension(sourcePath);
                var outPath = Path.Combine(outputDir ?? string.Empty, $"{baseName}.transcoded.{ext}");

                var job = new TranscodeJob
                {
                    Id = Guid.NewGuid(),
                    SourcePath = sourcePath,
                    OutputPath = outPath,
                    PresetId = presetId,
                    PresetName = presetId.HasValue ? "自定义预设" : null,
                    CustomArgs = customArgs,
                    UseHardwareAccel = useHw,
                    Status = 0, // 0 = 排队中
                    Progress = 0,
                    QueueTime = DateTime.UtcNow,
                    CreateTime = DateTime.UtcNow,
                    UpdateTime = DateTime.UtcNow
                };

                await transcodeJobStore.InsertAsync(job);
                transcodeQueueService.Enqueue(job.Id);
                await operationLogger.LogAsync("MCP提交转码", "转码任务", Path.GetFileName(sourcePath), job.Id.ToString(), clientIp: context.ClientIp);

                return McpToolExecutionResult.Success($"{{\"success\":true, \"job_id\":\"{job.Id}\", \"output_path\":\"{outPath.Replace('\\', '/')}\"}}");
            }

            case "transcode_cancel_job":
            {
                if (!args.TryGetProperty("job_id", out var jProp))
                    return McpToolExecutionResult.Error("Missing job_id parameter");

                if (!Guid.TryParse(jProp.GetString(), out var jobId))
                    return McpToolExecutionResult.Error("Invalid job_id format");

                var job = await transcodeJobStore.GetByIdAsync(jobId);
                if (job == null) return McpToolExecutionResult.Error("Transcode job not found");

                await transcodeQueueService.CancelAsync(jobId);
                await operationLogger.LogAsync("MCP取消转码", "转码任务", Path.GetFileName(job.SourcePath), jobId.ToString(), clientIp: context.ClientIp);
                return McpToolExecutionResult.Success($"{{\"success\":true, \"message\":\"Cancelled job {jobId}\"}}");
            }

            default:
                return null;
        }
    }
}
