using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Support;

namespace LinuxWebTool.Infrastructure.Transcode;

/// <summary>
/// 转码队列执行器（BackgroundService）：
/// - Channel 队列 + 并发闸门（Media:MaxConcurrent，默认 1，ffmpeg 极耗 CPU）；
/// - 进度：ffprobe 取时长 + ffmpeg -progress pipe:1 输出时间差值，节流回写（≥2 秒或 speed 变化）；
/// - 日志：stderr 全量写 data/logs/transcode/&lt;jobId&gt;.log，库内仅存尾部摘要；
/// - 取消：Kill 整棵进程树，替换模式的临时文件随之清理；
/// - 恢复：启动时 Running → Interrupted、Queued 重新入队；OutputCompleted 事件供监听服务排除自产文件。
/// </summary>
public sealed class TranscodeQueueService(
    TranscodeJobStore jobStore,
    TranscodePresetStore presetStore,
    FfmpegLocator locator,
    TranscodeOptions options,
    DataPaths dataPaths,
    ILogger<TranscodeQueueService> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly SemaphoreSlim _gate = new(Math.Clamp(options.MaxConcurrent, 1, 4));
    private readonly object _runningLock = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _running = new();

    /// <summary>任务完成后（含替换改名）的输出登记：(输出路径, 大小)。监听服务据此排除自产文件防循环。</summary>
    public event Action<string, long>? OutputCompleted;

    private string LogDirectory => dataPaths.Resolve(Path.Combine("logs", "transcode"));

    // ---------- 对外操作 ----------

    public void Enqueue(Guid jobId) => _queue.Writer.TryWrite(jobId);

    /// <summary>取消任务：排队中直接标记取消；运行中杀进程树（由工作循环负责落库）。返回是否发出取消。</summary>
    public async Task<bool> CancelAsync(Guid jobId)
    {
        CancellationTokenSource? cts;
        lock (_runningLock)
        {
            _running.TryGetValue(jobId, out cts);
        }
        if (cts is not null)
        {
            await Task.Run(() => cts.Cancel());
            return true;
        }

        var job = await jobStore.GetByIdAsync(jobId);
        if (job is null || job.Status != (int)TranscodeJobStatus.Queued)
        {
            return false;
        }
        job.Status = (int)TranscodeJobStatus.Cancelled;
        job.EndTime = DateTime.Now;
        await jobStore.UpdateAsync(job);
        return true;
    }

    /// <summary>路径是否正处于排队 / 运行任务中（监听排除自身输出与重复入队用）。</summary>
    public async Task<bool> IsPathActiveAsync(string path)
    {
        var (items, _) = await jobStore.QueryAsync(1, 100, TranscodeJobStatus.Running, null);
        var (queued, _) = await jobStore.QueryAsync(1, 100, TranscodeJobStatus.Queued, null);
        var full = items.Concat(queued);
        return full.Any(j =>
            string.Equals(j.SourcePath, path, StringComparison.OrdinalIgnoreCase)
            || string.Equals(j.OutputPath, path, StringComparison.OrdinalIgnoreCase));
    }

    // ---------- 队列主循环 ----------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 等数据库就绪，避免启动早期 IsAutoCloseConnection 连接未初始化时恢复查询失败
        try
        {
            await Task.Delay(2000, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await RecoverAsync(stoppingToken);
            CleanOldLogs();

            var workers = Enumerable.Range(0, _gate.CurrentCount)
                .Select(_ => WorkerLoopAsync(stoppingToken));
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
            // 正常停机
        }
    }

    private async Task RecoverAsync(CancellationToken stoppingToken)
    {
        try
        {
            var interrupted = await jobStore.MarkRunningAsInterruptedAsync();
            if (interrupted > 0)
            {
                logger.LogWarning("启动恢复：{Count} 个中断的转码任务已标记", interrupted);
            }
            foreach (var jobId in await jobStore.GetQueuedIdsAsync())
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                Enqueue(jobId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "转码队列启动恢复失败");
        }
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var jobId in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                await _gate.WaitAsync(stoppingToken);
                try
                {
                    await ExecuteJobAsync(jobId, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return; // 停机：ExecuteJobAsync 内部已标记中断
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "转码任务执行异常：{JobId}", jobId);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 停机
        }
    }

    // ---------- 单任务执行 ----------

    private async Task ExecuteJobAsync(Guid jobId, CancellationToken appStopping)
    {
        var job = await jobStore.GetByIdAsync(jobId);
        if (job is null || job.Status != (int)TranscodeJobStatus.Queued)
        {
            return; // 已被取消或删除
        }

        using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(appStopping);
        lock (_runningLock)
        {
            _running[jobId] = jobCts;
        }
        try
        {
            await RunJobCoreAsync(job, jobCts.Token, appStopping);
        }
        catch (OperationCanceledException)
        {
            var interrupted = appStopping.IsCancellationRequested;
            await SetTerminalAsync(job, interrupted ? TranscodeJobStatus.Interrupted : TranscodeJobStatus.Cancelled,
                interrupted ? "应用停机，任务被中断（可重试）" : "已取消", null);
            logger.LogInformation("转码任务{Result}：{Source}", interrupted ? "中断" : "取消", job.SourcePath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "转码任务异常：{Source}", job.SourcePath);
            await SetTerminalAsync(job, TranscodeJobStatus.Failed, ex.Message, null);
        }
        finally
        {
            lock (_runningLock)
            {
                _running.Remove(jobId);
            }
        }
    }

    private async Task RunJobCoreAsync(TranscodeJob job, CancellationToken jobStopping, CancellationToken appStopping)
    {
        // ffmpeg 可用性
        var detection = await locator.DetectAsync();
        if (!detection.Available)
        {
            await SetTerminalAsync(job, TranscodeJobStatus.Failed, detection.Message, null);
            return;
        }

        // 源文件与预设校验
        if (!File.Exists(job.SourcePath))
        {
            await SetTerminalAsync(job, TranscodeJobStatus.Failed, "源文件不存在（可能已被删除或替换）", null);
            return;
        }
        TranscodePreset? preset = null;
        if (job.PresetId is { } presetId)
        {
            preset = await presetStore.GetByIdAsync(presetId);
            if (preset is null)
            {
                await SetTerminalAsync(job, TranscodeJobStatus.Failed, "转码预设不存在（可能已被删除）", null);
                return;
            }
        }
        if (preset is null && string.IsNullOrWhiteSpace(job.CustomArgs))
        {
            await SetTerminalAsync(job, TranscodeJobStatus.Failed, "未指定预设或自定义参数", null);
            return;
        }

        // 输出规划（输出路径在运行时定，避免排队期间目标被占用）
        var mode = (TranscodeOutputMode)job.OutputMode;
        var container = job.OutputContainer ?? preset?.Container ?? "mp4";
        var (finalPath, tempPath) = OutputPathPlanner.Plan(job.SourcePath, container, mode, job.OutputDir);

        var logPath = Path.Combine(LogDirectory, $"{job.Id:N}.log");
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(finalPath)!);

        job.Status = (int)TranscodeJobStatus.Running;
        job.StartTime = DateTime.Now;
        job.OutputPath = finalPath;
        job.LogFile = logPath;
        job.Progress = 0;
        job.ErrorOutput = null;
        try
        {
            job.SourceSizeBytes = new FileInfo(job.SourcePath).Length;
        }
        catch
        {
            // 网络盘取大小失败不阻断
        }
        await jobStore.UpdateAsync(job);

        // 时长探测（失败不阻断，仅无百分比）
        var totalSeconds = await ProbeDurationAsync(job.SourcePath, detection.FfprobePath);

        logger.LogInformation("转码开始：{Source} → {Output}（{Mode}，硬件加速={Hw}）", job.SourcePath, finalPath, mode == TranscodeOutputMode.Replace ? "替换" : "并存", job.UseHardwareAccel);

        var buildResult = FfmpegArgsBuilder.BuildWithHw(job.SourcePath, tempPath ?? finalPath, preset, job.CustomArgs,
            new FfmpegArgsBuilder.HwEncodeContext(job.UseHardwareAccel, detection.HwEncoders));
        var args = buildResult.Args;
        // 记录实际执行的完整命令（供任务队列回显）与实际是否用了硬件编码器
        job.CommandLine = string.Join(' ', (new[] { detection.FfmpegPath }).Concat(args));
        job.UsedHardwareAccel = buildResult.UsedHardwareAccel;
        await jobStore.UpdateAsync(job);
        logger.LogInformation("转码命令：{Command}（{Mode}）", job.CommandLine, buildResult.UsedHardwareAccel ? "硬件加速" : "软件编码");

        var tailBuffer = new StringBuilder();
        long outputSize = 0;

        using (var process = new Process())
        {
            process.StartInfo = new ProcessStartInfo
            {
                FileName = detection.FfmpegPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            await using var logStream = new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            process.Start();

            var stdoutTask = ConsumeProgressAsync(process, job, totalSeconds, jobStopping);
            var stderrTask = ConsumeLogAsync(process, logStream, tailBuffer);

            try
            {
                await process.WaitForExitAsync(jobStopping);
            }
            catch (OperationCanceledException)
            {
                TryKillTree(process);
                throw; // 由外层统一标记取消 / 中断并清理
            }
            await stdoutTask;
            await stderrTask;

            if (process.ExitCode != 0)
            {
                var tail = Tail(tailBuffer);
                await SetTerminalAsync(job, TranscodeJobStatus.Failed, $"ffmpeg 退出码 {process.ExitCode}：{tail}", tail);
                OutputPathPlanner.CleanupTemp(tempPath);
                logger.LogWarning("转码失败（exit {Code}）：{Source}", process.ExitCode, job.SourcePath);
                return;
            }
        }

        // 成功校验 + 替换落地（失败抛异常 → 外层记为失败并清理临时文件）
        try
        {
            OutputPathPlanner.FinalizeOutput(finalPath, tempPath, job.SourcePath, mode);
        }
        catch (Exception ex)
        {
            OutputPathPlanner.CleanupTemp(tempPath);
            await SetTerminalAsync(job, TranscodeJobStatus.Failed, ex.Message, Tail(tailBuffer));
            return;
        }

        try
        {
            outputSize = new FileInfo(finalPath).Length;
        }
        catch
        {
            // 网络盘取大小失败不阻断
        }

        job.Status = (int)TranscodeJobStatus.Success;
        job.Progress = 100;
        job.SpeedText = null;
        job.OutputSizeBytes = outputSize;
        job.DurationMs = (long)(DateTime.Now - (job.StartTime ?? DateTime.Now)).TotalMilliseconds;
        job.EndTime = DateTime.Now;
        await jobStore.UpdateAsync(job);
        OutputCompleted?.Invoke(finalPath, outputSize);
        logger.LogInformation("转码成功：{Source} → {Output}（{DurationMs}ms）", job.SourcePath, finalPath, job.DurationMs);
    }

    // ---------- 进度 / 日志消费 ----------

    private async Task ConsumeProgressAsync(Process process, TranscodeJob job, double? totalSeconds, CancellationToken stopping)
    {
        double lastSeconds = 0;
        var lastWrite = DateTime.MinValue;
        string? lastSpeed = null;

        try
        {
            // 不用 EndOfStream（同步属性会阻塞），直接循环 ReadLineAsync 直到流关闭返回 null
            while (await process.StandardOutput.ReadLineAsync(stopping) is { } line)
            {
                if (line.StartsWith("out_time=", StringComparison.Ordinal) && TimeSpan.TryParse(line[9..], System.Globalization.CultureInfo.InvariantCulture, out var time))
                {
                    lastSeconds = Math.Max(0, time.TotalSeconds);
                }
                else if (line.StartsWith("out_time_ms=", StringComparison.Ordinal) && double.TryParse(line[12..], System.Globalization.CultureInfo.InvariantCulture, out var micros))
                {
                    // ffmpeg 的 out_time_ms 实为微秒（历史命名问题）
                    lastSeconds = Math.Max(0, micros / 1_000_000);
                }
                else if (line.StartsWith("speed=", StringComparison.Ordinal))
                {
                    lastSpeed = line[6..].Trim();
                }

                var now = DateTime.Now;
                if ((now - lastWrite).TotalSeconds < 2)
                {
                    continue;
                }
                lastWrite = now;
                job.SpeedText = lastSpeed;
                job.Progress = totalSeconds is > 0 ? Math.Clamp(100.0 * lastSeconds / totalSeconds.Value, 0, 99.5) : 0;
                await jobStore.UpdateAsync(job);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 进度流异常不影响转码本体
        }
    }

    private static async Task ConsumeLogAsync(Process process, FileStream logStream, StringBuilder tail)
    {
        try
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await logStream.WriteAsync(bytes);
                AppendTail(tail, line);
            }
        }
        catch
        {
            // 日志流异常不影响转码本体
        }
    }

    private static readonly object TailLock = new();

    private static void AppendTail(StringBuilder tail, string line)
    {
        lock (TailLock)
        {
            tail.AppendLine(line);
            if (tail.Length > 16 * 1024)
            {
                tail.Remove(0, tail.Length - 16 * 1024);
            }
        }
    }

    private static string Tail(StringBuilder tail)
    {
        lock (TailLock)
        {
            var text = tail.ToString().Trim();
            return text.Length > 1800 ? text[^1800..] : text;
        }
    }

    // ---------- 状态与工具 ----------

    private async Task SetTerminalAsync(TranscodeJob job, TranscodeJobStatus status, string? message, string? tail)
    {
        job.Status = (int)status;
        job.SpeedText = null;
        job.ErrorOutput = message ?? tail;
        job.DurationMs = job.StartTime is { } start ? (long)(DateTime.Now - start).TotalMilliseconds : null;
        job.EndTime = DateTime.Now;
        await jobStore.UpdateAsync(job);
    }

    private async Task<double?> ProbeDurationAsync(string path, string? ffprobePath)
    {
        if (string.IsNullOrEmpty(ffprobePath))
        {
            return null;
        }
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ffprobePath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.StartInfo.ArgumentList.Add("-v");
            process.StartInfo.ArgumentList.Add("error");
            process.StartInfo.ArgumentList.Add("-show_entries");
            process.StartInfo.ArgumentList.Add("format=duration");
            process.StartInfo.ArgumentList.Add("-of");
            process.StartInfo.ArgumentList.Add("default=noprint_wrappers=1:nokey=1");
            process.StartInfo.ArgumentList.Add(path);

            process.Start();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKillTree(process);
                return null;
            }
            if (process.ExitCode != 0)
            {
                return null;
            }
            var text = (await process.StandardOutput.ReadToEndAsync()).Trim();
            return double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能恰好在杀掉前退出
        }
    }

    /// <summary>清理过期转码日志（Media:LogRetentionDays，默认 7 天）。</summary>
    private void CleanOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                return;
            }
            var deadline = DateTime.Now.AddDays(-Math.Max(1, options.LogRetentionDays));
            foreach (var file in Directory.EnumerateFiles(LogDirectory))
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < deadline)
                {
                    info.Delete();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "转码日志清理失败");
        }
    }
}
