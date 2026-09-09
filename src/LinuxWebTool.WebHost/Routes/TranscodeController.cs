using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Transcode;
using LinuxWebTool.WebHost.Extensions;

namespace LinuxWebTool.WebHost.Routes;

/// <summary>
/// FFmpeg 媒体转码：
/// - 一次性转码（文件 / 文件夹批量入队，替换 / 并存两种输出模式）；
/// - 转码预设 CRUD（内置预设可编辑可删除）；
/// - 任务队列查询 / 取消 / 清理；
/// - 文件夹监听规则 CRUD（自动转码）；
/// - ffmpeg 可用性检测。
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class TranscodeController(
    TranscodeJobStore jobStore,
    TranscodePresetStore presetStore,
    WatchRuleStore watchRuleStore,
    TranscodeQueueService queueService,
    FfmpegLocator locator,
    TranscodeOptions transcodeOptions,
    IOperationLogger operationLogger) : ControllerBase
{
    // ---------- 转码任务 ----------

    [HttpGet("Jobs")]
    public async Task<IActionResult> Jobs([FromQuery] int page = 1, [FromQuery] int pageSize = 20,
        [FromQuery] TranscodeJobStatus? status = null, [FromQuery] Guid? watchRuleId = null)
    {
        var (items, total) = await jobStore.QueryAsync(page, pageSize, status, watchRuleId);
        var mapped = items.Select(j => new
        {
            j.Id,
            j.SourcePath,
            j.OutputPath,
            j.PresetName,
            j.OutputMode,
            j.Trigger,
            j.WatchRuleId,
            j.Status,
            j.Progress,
            j.SpeedText,
            j.DurationMs,
            j.ErrorOutput,
            j.SourceSizeBytes,
            j.OutputSizeBytes,
            j.QueueTime,
            j.StartTime,
            j.EndTime,
            j.UseHardwareAccel,
            j.HardwareBackend,
            j.UsedHardwareAccel,
            j.CommandLine,
            j.FallbackReason,
            j.FallbackFromCommand,
        });
        return Ok(new { items = mapped, total });
    }

    /// <summary>一次性转码提交：SourcePath 为文件 → 单任务；为文件夹 → 按扩展名过滤扫描批量入队。</summary>
    [HttpPost("Submit")]
    public async Task<IActionResult> Submit([FromBody] TranscodeSubmitRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.SourcePath))
        {
            return BadRequest(new { message = "请填写源文件 / 源文件夹路径" });
        }
        if (string.IsNullOrWhiteSpace(request.CustomArgs) && request.PresetId is null)
        {
            return BadRequest(new { message = "请选择预设或填写自定义 ffmpeg 参数" });
        }
        if (!string.IsNullOrWhiteSpace(request.CustomArgs) && request.PresetId is not null)
        {
            return BadRequest(new { message = "预设与自定义参数不能同时使用" });
        }

        var source = request.SourcePath.Trim();
        if (System.IO.File.Exists(source))
        {
            var (valid, message) = await ValidateSubmitPresetAsync(request);
            if (!valid)
            {
                return BadRequest(new { message });
            }
            var preset = request.PresetId is { } presetId ? await presetStore.GetByIdAsync(presetId) : null;
            var job = await CreateJobAsync(source, preset, preset?.Name, request.CustomArgs,
                request.OutputContainer, request.OutputMode, TranscodeTrigger.Manual, request.OutputDir, watchRuleId: null,
                request.UseHardwareAccel, request.HardwareBackend);
            await operationLogger.LogAsync("提交转码", "转码任务", Path.GetFileName(source),
                $"{source}（预设: {request.PresetId?.ToString() ?? "自定义参数"}）", clientIp: HttpContext.GetClientIp());
            return Ok(new { message = "已加入转码队列", count = 1, jobId = job.Id });
        }

        if (Directory.Exists(source))
        {
            var (valid, message) = await ValidateSubmitPresetAsync(request);
            if (!valid)
            {
                return BadRequest(new { message });
            }
            var extensions = MediaExtensions.Parse(request.FilePatterns);
            var preset = request.PresetId is { } presetId ? await presetStore.GetByIdAsync(presetId) : null;
            var count = 0;
            var queueTime = DateTime.Now;
            foreach (var (path, _) in MediaExtensions.WalkFiles(source, request.Recursive))
            {
                var ext = Path.GetExtension(path);
                if (extensions is not null && !extensions.Contains(ext))
                {
                    continue;
                }
                if (MediaExtensions.IsTemporaryFile(path))
                {
                    continue;
                }
                if (count >= transcodeOptions.MaxBatchSubmit)
                {
                    break;
                }
                var job = await CreateJobAsync(path, preset, preset?.Name, request.CustomArgs,
                    request.OutputContainer, request.OutputMode, TranscodeTrigger.Manual, request.OutputDir, watchRuleId: null,
                    request.UseHardwareAccel, request.HardwareBackend);
                count++;
            }
            await operationLogger.LogAsync("提交文件夹转码", "转码任务", source,
                $"批量入队 {count} 个文件", count > 0, clientIp: HttpContext.GetClientIp());
            return Ok(new { message = count > 0 ? $"已加入转码队列 {count} 个文件" : "未发现匹配的媒体文件", count });
        }

        return BadRequest(new { message = "源路径不存在（文件或文件夹均未找到），请检查路径是否为服务器本地可访问路径" });
    }

    [HttpPost("Jobs/{id:guid}/Cancel")]
    public async Task<IActionResult> Cancel(Guid id)
    {
        var job = await jobStore.GetByIdAsync(id);
        if (job is null)
        {
            return NotFound(new { message = "任务不存在" });
        }
        if (job.Status != (int)TranscodeJobStatus.Queued && job.Status != (int)TranscodeJobStatus.Running)
        {
            return BadRequest(new { message = "任务已结束，无需取消" });
        }
        var cancelled = await queueService.CancelAsync(id);
        if (!cancelled)
        {
            return BadRequest(new { message = "任务状态已变化，请刷新后重试" });
        }
        await operationLogger.LogAsync("取消转码任务", "转码任务", job.SourcePath, clientIp: HttpContext.GetClientIp());
        return Ok(new { message = job.Status == (int)TranscodeJobStatus.Running ? "已发出取消指令，等待进程终止" : "已取消" });
    }

    [HttpPost("Jobs/Retry/{id:guid}")]
    public async Task<IActionResult> Retry(Guid id)
    {
        var job = await jobStore.GetByIdAsync(id);
        if (job is null)
        {
            return NotFound(new { message = "任务不存在" });
        }
        if (job.Status != (int)TranscodeJobStatus.Failed && job.Status != (int)TranscodeJobStatus.Cancelled
            && job.Status != (int)TranscodeJobStatus.Interrupted)
        {
            return BadRequest(new { message = "仅失败的 / 已取消 / 已中断的任务可重试" });
        }
        if (!System.IO.File.Exists(job.SourcePath))
        {
            return BadRequest(new { message = "源文件不存在，无法重试" });
        }

        job.Status = (int)TranscodeJobStatus.Queued;
        job.Progress = 0;
        job.ErrorOutput = null;
        job.SpeedText = null;
        job.StartTime = null;
        job.EndTime = null;
        job.QueueTime = DateTime.Now;
        await jobStore.UpdateAsync(job);
        queueService.Enqueue(job.Id);
        await operationLogger.LogAsync("重试转码任务", "转码任务", job.SourcePath, clientIp: HttpContext.GetClientIp());
        return Ok(new { message = "已重新加入转码队列" });
    }

    [HttpPost("Jobs/ClearFinished")]
    public async Task<IActionResult> ClearFinished()
    {
        var deleted = await jobStore.DeleteFinishedAsync();
        await operationLogger.LogAsync("清理转码记录", "转码任务", $"{deleted} 条", clientIp: HttpContext.GetClientIp());
        return Ok(new { message = $"已清理 {deleted} 条已结束记录" });
    }

    private async Task<TranscodeJob> CreateJobAsync(string source, TranscodePreset? preset, string? presetName,
        string? customArgs, string? outputContainer, TranscodeOutputMode outputMode,
        TranscodeTrigger trigger, string? outputDir, Guid? watchRuleId, bool useHardwareAccel = true,
        string? hardwareBackend = "auto")
    {
        var job = new TranscodeJob
        {
            SourcePath = source,
            PresetId = preset?.Id,
            PresetName = presetName ?? preset?.Name,
            CustomArgs = string.IsNullOrWhiteSpace(customArgs) ? null : customArgs.Trim(),
            OutputMode = (int)outputMode,
            Trigger = (int)trigger,
            WatchRuleId = watchRuleId,
            OutputDir = string.IsNullOrWhiteSpace(outputDir) ? null : outputDir.Trim(),
            OutputContainer = preset is null ? (string.IsNullOrWhiteSpace(outputContainer) ? null : outputContainer.Trim()) : null,
            UseHardwareAccel = useHardwareAccel,
            HardwareBackend = string.IsNullOrWhiteSpace(hardwareBackend) ? "auto" : hardwareBackend.Trim(),
            QueueTime = DateTime.Now,
        };
        try
        {
            job.SourceSizeBytes = new FileInfo(source).Length;
        }
        catch
        {
            // 取大小失败不阻断
        }
        await jobStore.InsertAsync(job);
        queueService.Enqueue(job.Id);
        return job;
    }

    private async Task<(bool Valid, string Message)> ValidateSubmitPresetAsync(TranscodeSubmitRequest request)
    {
        if (request.PresetId is { } presetId)
        {
            var preset = await presetStore.GetByIdAsync(presetId);
            if (preset is null)
            {
                return (false, "转码预设不存在");
            }
            // 允许对自定义参数 null 的 preset 使用 request.OutputContainer
            if (string.IsNullOrWhiteSpace(request.OutputContainer))
            {
                request = request with { OutputContainer = preset.Container };
            }
        }
        else if (string.IsNullOrWhiteSpace(request.OutputContainer))
        {
            // 自定义参数必填输出容器
            return (false, "自定义参数模式请填写输出扩展名（如 mp4 / mkv / mp3）");
        }
        return (true, string.Empty);
    }

    // ---------- 转码预设 ----------

    [HttpGet("Presets")]
    public async Task<IActionResult> Presets()
    {
        var presets = await presetStore.GetAllAsync();
        return Ok(presets.Select(p => new
        {
            p.Id,
            p.Name,
            p.Container,
            p.VideoCodec,
            p.VideoQuality,
            p.AudioCodec,
            p.AudioBitrate,
            p.ExtraArgs,
            p.Description,
            p.IsBuiltin,
            p.CreateTime,
        }));
    }

    [HttpPost("Presets")]
    public async Task<IActionResult> CreatePreset([FromBody] SavePresetRequest request)
    {
        var (valid, message) = await ValidatePresetAsync(request);
        if (!valid)
        {
            return BadRequest(new { message });
        }
        var preset = new TranscodePreset
        {
            Name = request.Name.Trim(),
            Container = request.Container.Trim().TrimStart('.'),
            VideoCodec = request.VideoCodec?.Trim(),
            VideoQuality = request.VideoQuality,
            AudioCodec = request.AudioCodec?.Trim(),
            AudioBitrate = request.AudioBitrate?.Trim(),
            ExtraArgs = request.ExtraArgs?.Trim(),
            Description = request.Description?.Trim(),
            IsBuiltin = false,
        };
        await presetStore.InsertAsync(preset);
        await operationLogger.LogAsync("新增转码预设", "转码预设", preset.Name, DescribePreset(preset), clientIp: HttpContext.GetClientIp());
        return Ok(new { preset.Id });
    }

    [HttpPut("Presets/{id:guid}")]
    public async Task<IActionResult> UpdatePreset(Guid id, [FromBody] SavePresetRequest request)
    {
        var preset = await presetStore.GetByIdAsync(id);
        if (preset is null)
        {
            return NotFound(new { message = "转码预设不存在" });
        }
        var (valid, message) = await ValidatePresetAsync(request, excludeId: id);
        if (!valid)
        {
            return BadRequest(new { message });
        }
        preset.Name = request.Name.Trim();
        preset.Container = request.Container.Trim().TrimStart('.');
        preset.VideoCodec = request.VideoCodec?.Trim();
        preset.VideoQuality = request.VideoQuality;
        preset.AudioCodec = request.AudioCodec?.Trim();
        preset.AudioBitrate = request.AudioBitrate?.Trim();
        preset.ExtraArgs = request.ExtraArgs?.Trim();
        preset.Description = request.Description?.Trim();
        await presetStore.UpdateAsync(preset);
        await operationLogger.LogAsync("修改转码预设", "转码预设", preset.Name, DescribePreset(preset), clientIp: HttpContext.GetClientIp());
        return Ok(new { preset.Id });
    }

    [HttpDelete("Presets/{id:guid}")]
    public async Task<IActionResult> DeletePreset(Guid id)
    {
        var preset = await presetStore.GetByIdAsync(id);
        if (preset is null)
        {
            return NotFound(new { message = "转码预设不存在" });
        }
        if (await presetStore.CountWatchRuleUsageAsync(id) > 0)
        {
            return BadRequest(new { message = "该预设正被监听规则使用，无法删除（请先修改或删除对应规则）" });
        }
        if (await jobStore.ExistsActiveForPresetAsync(id))
        {
            return BadRequest(new { message = "该预设存在排队 / 运行中的任务，无法删除" });
        }
        await presetStore.DeleteAsync(id);
        await operationLogger.LogAsync("删除转码预设", "转码预设", preset.Name, DescribePreset(preset), clientIp: HttpContext.GetClientIp());
        return Ok(new { message = "已删除" });
    }

    /// <summary>导出全部预设为 JSON 文件（含内置），供备份 / 迁移 / 导入。</summary>
    [HttpGet("Presets/Export")]
    public async Task<IActionResult> ExportPresets()
    {
        var presets = await presetStore.GetAllAsync();
        var items = presets.Select(p => new
        {
            name = p.Name,
            container = p.Container,
            videoCodec = p.VideoCodec,
            videoQuality = p.VideoQuality,
            audioCodec = p.AudioCodec,
            audioBitrate = p.AudioBitrate,
            extraArgs = p.ExtraArgs,
            description = p.Description,
            isBuiltin = p.IsBuiltin,
        });
        var json = System.Text.Json.JsonSerializer.Serialize(items, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        var fileName = $"transcode-presets-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        return File(bytes, "application/json; charset=utf-8", fileName);
    }

    /// <summary>从 JSON 数组导入预设。同名预设跳过（含内置，不覆盖）；内置标记按文件还原。</summary>
    [HttpPost("Presets/Import")]
    public async Task<IActionResult> ImportPresets([FromBody] List<PresetImportItem> items)
    {
        if (items is null)
        {
            return BadRequest(new { message = "导入内容为空" });
        }
        if (items.Count > 500)
        {
            return BadRequest(new { message = "单次最多导入 500 个预设" });
        }

        var imported = 0;
        var skipped = 0;
        var messages = new List<string>();
        foreach (var item in items)
        {
            var name = item.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                skipped++;
                messages.Add("存在一项缺少名称，已跳过");
                continue;
            }
            if (await presetStore.ExistsNameAsync(name, excludeId: null))
            {
                skipped++;
                messages.Add($"「{name}」已存在同名，已跳过");
                continue;
            }
            var container = (item.Container ?? string.Empty).Trim().TrimStart('.');
            if (container.Length == 0)
            {
                skipped++;
                messages.Add($"「{name}」缺少输出格式，已跳过");
                continue;
            }
            if (string.IsNullOrWhiteSpace(item.VideoCodec) && string.IsNullOrWhiteSpace(item.AudioCodec))
            {
                skipped++;
                messages.Add($"「{name}」视频 / 音频至少保留一项，已跳过");
                continue;
            }
            if (item.VideoQuality is < 0 or > 51)
            {
                skipped++;
                messages.Add($"「{name}」CRF 必须在 0~51 之间，已跳过");
                continue;
            }

            var preset = new TranscodePreset
            {
                Name = name,
                Container = container,
                VideoCodec = item.VideoCodec?.Trim(),
                VideoQuality = item.VideoQuality,
                AudioCodec = item.AudioCodec?.Trim(),
                AudioBitrate = item.AudioBitrate?.Trim(),
                ExtraArgs = item.ExtraArgs?.Trim(),
                Description = item.Description?.Trim(),
                IsBuiltin = item.IsBuiltin,
            };
            await presetStore.InsertAsync(preset);
            imported++;
        }

        await operationLogger.LogAsync("导入转码预设", "转码预设", $"导入 {imported} 个 / 跳过 {skipped} 个", string.Empty, clientIp: HttpContext.GetClientIp());
        return Ok(new { imported, skipped, messages });
    }

    private async Task<(bool Valid, string Message)> ValidatePresetAsync(SavePresetRequest request, Guid? excludeId = null)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (false, "预设名称不能为空");
        }
        if (request.Name.Trim().Length > 100)
        {
            return (false, "预设名称不能超过 100 个字符");
        }
        if (string.IsNullOrWhiteSpace(request.Container))
        {
            return (false, "输出格式不能为空（如 mp4 / mkv / mp3）");
        }
        if (request.VideoQuality is < 0 or > 51)
        {
            return (false, "CRF 值必须在 0~51 之间");
        }
        var audioCodec = request.AudioCodec?.Trim();
        var videoCodec = request.VideoCodec?.Trim();
        if (videoCodec is null && audioCodec is null)
        {
            return (false, "视频 / 音频至少保留一项（视频置空 = 提取音频；音频置空 = 去音频；都空 = 无输出）");
        }
        return (true, string.Empty);
    }

    private static string DescribePreset(TranscodePreset preset)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(preset.VideoCodec))
        {
            parts.Add($"视频 {preset.VideoCodec}{(preset.VideoQuality is { } q ? $" crf{q}" : "")}");
        }
        if (!string.IsNullOrEmpty(preset.AudioCodec))
        {
            parts.Add($"音频 {preset.AudioCodec}{(string.IsNullOrEmpty(preset.AudioBitrate) ? "" : $" {preset.AudioBitrate}")}");
        }
        if (parts.Count == 0)
        {
            parts.Add("仅转容器");
        }
        return $"{preset.Container} | {string.Join("，", parts)}";
    }

    // ---------- 监听规则 ----------

    [HttpGet("WatchRules")]
    public async Task<IActionResult> WatchRules()
    {
        var rules = await watchRuleStore.GetAllAsync();
        return Ok(rules.Select(r => new
        {
            r.Id,
            r.Name,
            r.WatchPath,
            r.FilePatterns,
            r.PresetId,
            r.OutputMode,
            r.Recursive,
            r.Mode,
            r.PollSeconds,
            r.Enabled,
            r.UseHardwareAccel,
            r.HardwareBackend,
            r.LastScanTime,
        }));
    }

    [HttpPost("WatchRules")]
    public async Task<IActionResult> CreateWatchRule([FromBody] SaveWatchRuleRequest request)
    {
        var (valid, message) = await ValidateWatchRuleAsync(request);
        if (!valid)
        {
            return BadRequest(new { message });
        }
        var rule = new WatchRule
        {
            Name = request.Name.Trim(),
            WatchPath = request.WatchPath.Trim().TrimEnd('/'),
            FilePatterns = request.FilePatterns?.Trim(),
            PresetId = request.PresetId,
            OutputMode = (int)request.OutputMode,
            Recursive = request.Recursive,
            Mode = (int)request.Mode,
            PollSeconds = request.PollSeconds,
            Enabled = request.Enabled,
            UseHardwareAccel = request.UseHardwareAccel,
            HardwareBackend = string.IsNullOrWhiteSpace(request.HardwareBackend) ? "auto" : request.HardwareBackend.Trim(),
        };
        await watchRuleStore.InsertAsync(rule);
        await operationLogger.LogAsync("新增监听规则", "监听转码", rule.Name,
            $"{rule.WatchPath} → 预设 {request.PresetId}", clientIp: HttpContext.GetClientIp());
        return Ok(new { rule.Id });
    }

    [HttpPut("WatchRules/{id:guid}")]
    public async Task<IActionResult> UpdateWatchRule(Guid id, [FromBody] SaveWatchRuleRequest request)
    {
        var rule = await watchRuleStore.GetByIdAsync(id);
        if (rule is null)
        {
            return NotFound(new { message = "监听规则不存在" });
        }
        var (valid, message) = await ValidateWatchRuleAsync(request, excludeId: id);
        if (!valid)
        {
            return BadRequest(new { message });
        }
        rule.Name = request.Name.Trim();
        rule.WatchPath = request.WatchPath.Trim().TrimEnd('/');
        rule.FilePatterns = request.FilePatterns?.Trim();
        rule.PresetId = request.PresetId;
        rule.OutputMode = (int)request.OutputMode;
        rule.Recursive = request.Recursive;
        rule.Mode = (int)request.Mode;
        rule.PollSeconds = request.PollSeconds;
        rule.Enabled = request.Enabled;
        rule.UseHardwareAccel = request.UseHardwareAccel;
        rule.HardwareBackend = string.IsNullOrWhiteSpace(request.HardwareBackend) ? "auto" : request.HardwareBackend.Trim();
        await watchRuleStore.UpdateAsync(rule);
        await operationLogger.LogAsync("修改监听规则", "监听转码", rule.Name, $"{rule.WatchPath}", clientIp: HttpContext.GetClientIp());
        return Ok(new { rule.Id });
    }

    [HttpDelete("WatchRules/{id:guid}")]
    public async Task<IActionResult> DeleteWatchRule(Guid id)
    {
        var rule = await watchRuleStore.GetByIdAsync(id);
        if (rule is null)
        {
            return NotFound(new { message = "监听规则不存在" });
        }
        await watchRuleStore.DeleteAsync(id);
        await operationLogger.LogAsync("删除监听规则", "监听转码", rule.Name, $"{rule.WatchPath}", clientIp: HttpContext.GetClientIp());
        return Ok(new { message = "已删除" });
    }

    [HttpPost("WatchRules/{id:guid}/Toggle")]
    public async Task<IActionResult> ToggleWatchRule(Guid id)
    {
        var rule = await watchRuleStore.GetByIdAsync(id);
        if (rule is null)
        {
            return NotFound(new { message = "监听规则不存在" });
        }
        rule.Enabled = !rule.Enabled;
        await watchRuleStore.UpdateAsync(rule);
        return Ok(new { rule.Enabled, rule.Id });
    }

    private async Task<(bool Valid, string Message)> ValidateWatchRuleAsync(SaveWatchRuleRequest request, Guid? excludeId = null)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return (false, "规则名称不能为空");
        }
        if (request.Name.Trim().Length > 100)
        {
            return (false, "规则名称不能超过 100 个字符");
        }
        if (string.IsNullOrWhiteSpace(request.WatchPath))
        {
            return (false, "监听目录不能为空");
        }
        if (!request.WatchPath.StartsWith('/'))
        {
            return (false, "监听目录必须为 Linux 绝对路径（如 /mnt/media/movies）");
        }
        var preset = await presetStore.GetByIdAsync(request.PresetId);
        if (preset is null)
        {
            return (false, "所选转码预设不存在");
        }
        if (request.PollSeconds is < 30 or > 86400)
        {
            return (false, "轮询间隔必须在 30~86400 秒之间");
        }
        return (true, string.Empty);
    }

    // ---------- ffmpeg 检测 ----------

    [HttpGet("DetectFfmpeg")]
    public async Task<IActionResult> DetectFfmpeg()
    {
        var detection = await locator.DetectAsync();
        return Ok(detection);
    }
}
