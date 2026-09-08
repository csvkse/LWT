using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;

namespace LinuxWebTool.Infrastructure.Transcode;

/// <summary>
/// 文件夹监听服务：发现新增 / 变更的媒体文件后自动入队转码。
/// - 轮询模式（默认，网络挂载盘 / SMB 可靠）：快照对比 + 两段稳定判定（无变化后稳定 10 秒才算写完），
///   首轮扫描仅建立基线、不触发历史文件；
/// - 文件系统事件模式（本地盘实时）：inotify 事件进候选集，复核大小稳定后入队；
/// - 防循环三重排除：临时文件（.lwt-tmp 等）、队列活动路径（源 + 输出）、转码完成登记路径（recentOutputs）。
/// </summary>
public sealed class WatchFolderService(
    WatchRuleStore ruleStore,
    TranscodeJobStore jobStore,
    TranscodePresetStore presetStore,
    TranscodeQueueService queueService,
    ILogger<WatchFolderService> logger) : BackgroundService
{
    private const int StableAgeSeconds = 10;
    private const int PendingRecheckMs = 15_000;
    private const int ReconcileMs = 30_000;

    private readonly ConcurrentDictionary<Guid, WatchRule> _rules = new();
    private readonly ConcurrentDictionary<Guid, (Task Task, CancellationTokenSource Cts)> _loops = new();
    private readonly ConcurrentDictionary<Guid, RuleState> _states = new();
    private readonly ConcurrentDictionary<string, DateTime> _recentOutputs = new();

    private sealed class RuleState
    {
        /// <summary>轮询快照：路径 → (大小, 最后写入时间, 稳定起始时间)。</summary>
        public ConcurrentDictionary<string, (long Size, DateTime LastWrite, DateTime? StableSince)> Snapshot { get; } = new();
        /// <summary>文件系统事件候选：路径 → 首次事件时间。</summary>
        public ConcurrentDictionary<string, DateTime> Pending { get; } = new();
        public DateTime LastScan { get; set; } = DateTime.MinValue;
    }

    // ---------- 生命周期 ----------

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        queueService.OutputCompleted += OnOutputCompleted;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ReconcileAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "监听规则装载失败");
                }
                try
                {
                    await Task.Delay(ReconcileMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            queueService.OutputCompleted -= OnOutputCompleted;
            foreach (var (_, cts) in _loops.Values)
            {
                cts.Cancel();
            }
        }
    }
    /// <summary>转码完成登记：成功落地的输出路径在 24 小时内不再触发（防替换模式自循环）。</summary>
    private void OnOutputCompleted(string path, long size) => _recentOutputs[path] = DateTime.Now;

    /// <summary>每 30 秒与数据库规则表对齐：新增 / 变更 / 删除 / 禁用。</summary>
    private async Task ReconcileAsync(CancellationToken stoppingToken)
    {
        var current = (await ruleStore.GetAllAsync()).ToDictionary(r => r.Id);
        var snapshotKeys = _rules.Keys.ToArray();

        foreach (var id in snapshotKeys)
        {
            _rules.TryGetValue(id, out var old);
            var exists = current.TryGetValue(id, out var next);
            var changed = old is not null && next is not null && !RuleEquals(old, next);
            if (!exists || changed || (next is { Enabled: false }))
            {
                StopLoop(id);
                _rules.TryRemove(id, out _);
            }
        }

        foreach (var rule in current.Values.Where(r => r.Enabled))
        {
            if (!_rules.TryGetValue(rule.Id, out var existing) || !RuleEquals(existing, rule))
            {
                StartLoop(rule, stoppingToken);
                _rules[rule.Id] = rule;
            }
        }

        // 清理已消失规则对应循环
        foreach (var id in _rules.Keys.Where(id => !current.ContainsKey(id)).ToArray())
        {
            StopLoop(id);
            _rules.TryRemove(id, out _);
        }

        // 清理过期完成登记
        var deadline = DateTime.Now.AddHours(-24);
        foreach (var key in _recentOutputs.Where(kv => kv.Value < deadline).Select(kv => kv.Key))
        {
            _recentOutputs.TryRemove(key, out _);
        }
    }

    private void StartLoop(WatchRule rule, CancellationToken stoppingToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        _states[rule.Id] = new RuleState();
        var task = rule.Mode == (int)WatchScanMode.FileSystem
            ? RunFsLoopAsync(rule, _states[rule.Id], cts.Token)
            : RunPollingLoopAsync(rule, _states[rule.Id], cts.Token);
        _loops[rule.Id] = (task, cts);
        logger.LogInformation("监听规则已启动：{Name}（{Mode}，{Interval}）", rule.Name,
            rule.Mode == (int)WatchScanMode.FileSystem ? "文件事件" : "轮询", rule.PollSeconds);
    }

    private void StopLoop(Guid id)
    {
        if (_loops.TryRemove(id, out var entry))
        {
            entry.Cts.Cancel();
            _states.TryRemove(id, out _);
        }
    }

    private static bool RuleEquals(WatchRule a, WatchRule b) =>
        a.Name == b.Name && a.WatchPath == b.WatchPath && a.FilePatterns == b.FilePatterns
        && a.PresetId == b.PresetId && a.OutputMode == b.OutputMode && a.Recursive == b.Recursive
        && a.Mode == b.Mode && a.PollSeconds == b.PollSeconds && a.Enabled == b.Enabled;

    // ---------- 轮询模式 ----------

    private async Task RunPollingLoopAsync(WatchRule rule, RuleState state, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ScanPollingOnceAsync(rule, state, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "监听扫描异常：{Name}", rule.Name);
            }
            try
            {
                await Task.Delay(Math.Max(10, rule.PollSeconds) * 1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ScanPollingOnceAsync(WatchRule rule, RuleState state, CancellationToken ct)
    {
        // 目录不可用（SMB 未挂载 / 网络盘掉线）→ 静默等待下一轮
        if (!Directory.Exists(rule.WatchPath))
        {
            state.LastScan = DateTime.Now;
            return;
        }

        var extensions = MediaExtensions.Parse(rule.FilePatterns);
        var activePaths = await jobStore.GetActivePathsAsync();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, size) in MediaExtensions.WalkFiles(rule.WatchPath, rule.Recursive))
        {
            ct.ThrowIfCancellationRequested();
            if (!MatchesFile(rule, path, extensions))
            {
                continue;
            }
            seen.Add(path);

            if (!state.Snapshot.TryGetValue(path, out var prev))
            {
                // 首现：写入时间已稳定 → 立即入队；仍在写入 → 记录后交给稳定检测
                var lwt = GetLastWrite(path);
                if (lwt is not null && IsReady(path, lwt.Value) && await EnqueueIfNotActiveAsync(rule, path, activePaths))
                {
                    continue;
                }
                state.Snapshot[path] = (size, lwt ?? DateTime.MinValue, null);
                continue;
            }

            var currentWrite = GetLastWrite(path);
            var changed = size != prev.Size || currentWrite != prev.LastWrite;
            if (changed)
            {
                // 变化后稳定 → 立即入队；仍在写入 → 记录
                if (currentWrite is { } lw && IsReady(path, lw) && await EnqueueIfNotActiveAsync(rule, path, activePaths))
                {
                    continue;
                }
                state.Snapshot[path] = (size, currentWrite ?? prev.LastWrite, null);
                continue;
            }

            // 无变化：累计稳定时长，达到阈值后入队
            var stableSince = prev.StableSince ?? DateTime.Now;
            if ((DateTime.Now - stableSince).TotalSeconds >= StableAgeSeconds
                && currentWrite is { } stableWrite
                && await EnqueueIfNotActiveAsync(rule, path, activePaths))
            {
                continue;
            }
            state.Snapshot[path] = (prev.Size, prev.LastWrite, stableSince);
        }

        // 清理已消失的文件快照
        foreach (var gone in state.Snapshot.Keys.Where(k => !seen.Contains(k)).ToArray())
        {
            state.Snapshot.TryRemove(gone, out _);
        }
        state.LastScan = DateTime.Now;
    }

    // ---------- 文件系统事件模式 ----------

    private async Task RunFsLoopAsync(WatchRule rule, RuleState state, CancellationToken ct)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            if (!Directory.Exists(rule.WatchPath))
            {
                logger.LogWarning("监听目录不存在，等待重试：{Path}", rule.WatchPath);
                return; // 30 秒后 Reconcile 会再次尝试
            }
            watcher = new FileSystemWatcher(rule.WatchPath)
            {
                IncludeSubdirectories = rule.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, e) => OnFsEvent(state, e.FullPath);
            watcher.Renamed += (_, e) => OnFsEvent(state, e.FullPath);
            watcher.Changed += (_, e) => OnFsEvent(state, e.FullPath);
            logger.LogInformation("文件事件监听已建立：{Path}", rule.WatchPath);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "文件事件监听初始化失败（网络挂载盘通常不支持 inotify，建议改用轮询模式）：{Path}", rule.WatchPath);
            return;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await RecheckPendingAsync(rule, state, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "监听候选复核异常：{Name}", rule.Name);
                }
                try
                {
                    await Task.Delay(PendingRecheckMs, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        finally
        {
            watcher.Dispose();
        }
    }

    private void OnFsEvent(RuleState state, string path)
    {
        state.Pending[path] = DateTime.Now;
    }

    private async Task RecheckPendingAsync(WatchRule rule, RuleState state, CancellationToken ct)
    {
        if (state.Pending.IsEmpty)
        {
            return;
        }
        var extensions = MediaExtensions.Parse(rule.FilePatterns);
        var activePaths = await jobStore.GetActivePathsAsync();
        var deadline = DateTime.Now.AddHours(-1);

        foreach (var (path, firstSeen) in state.Pending.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (firstSeen < deadline)
            {
                state.Pending.TryRemove(path, out _); // 事件后 1 小时仍未稳定 → 放弃（文件可能已删除）
                continue;
            }
            if (!MatchesFile(rule, path, extensions))
            {
                state.Pending.TryRemove(path, out _);
                continue;
            }
            var lwt = GetLastWrite(path);
            if (lwt is not null && IsReady(path, lwt.Value) && await EnqueueIfNotActiveAsync(rule, path, activePaths))
            {
                state.Pending.TryRemove(path, out _);
            }
        }
    }

    // ---------- 判定与入队 ----------

    private static bool MatchesFile(WatchRule rule, string path, HashSet<string>? extensions)
    {
        if (MediaExtensions.IsTemporaryFile(path))
        {
            return false;
        }
        return extensions is null || extensions.Contains(Path.GetExtension(path));
    }

    private bool IsReady(string path, DateTime lastWrite)
    {
        if ((DateTime.Now - lastWrite).TotalSeconds < StableAgeSeconds)
        {
            return false;
        }
        return !_recentOutputs.TryGetValue(path, out var completed) || DateTime.Now - completed > TimeSpan.FromHours(24);
    }

    private async Task<bool> EnqueueIfNotActiveAsync(WatchRule rule, string path, HashSet<string> activePaths)
    {
        if (activePaths.Contains(path))
        {
            return false; // 已在排队 / 运行（含源与输出路径）
        }

        var preset = await presetStore.GetByIdAsync(rule.PresetId);
        if (preset is null)
        {
            logger.LogWarning("监听规则 {Name} 引用的预设不存在，已跳过：{Path}", rule.Name, path);
            _rules.TryGetValue(rule.Id, out _);
            return true; // 视为已处理，避免每轮重复告警刷屏
        }

        try
        {
            var size = new FileInfo(path).Length;
            if (size <= 0)
            {
                return false;
            }
            var job = new TranscodeJob
            {
                SourcePath = path,
                PresetId = preset.Id,
                PresetName = preset.Name,
                OutputMode = rule.OutputMode,
                Trigger = (int)TranscodeTrigger.Watch,
                WatchRuleId = rule.Id,
                SourceSizeBytes = size,
                QueueTime = DateTime.Now,
                Status = (int)TranscodeJobStatus.Queued,
            };
            await jobStore.InsertAsync(job);
            queueService.Enqueue(job.Id);
            logger.LogInformation("监听触发转码入队：{Path}（规则 {Name}）", path, rule.Name);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "监听入队失败：{Path}", path);
            return false;
        }
    }

    private static DateTime? GetLastWrite(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTime(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
