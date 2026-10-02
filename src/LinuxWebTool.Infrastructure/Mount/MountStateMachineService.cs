using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.SystemInfo;
using Microsoft.Extensions.Configuration;

namespace LinuxWebTool.Infrastructure.Mount;

/// <summary>One scheduler owns startup, checks and manual operations for all mount backends.</summary>
public sealed class MountStateMachineService(MountBackendCatalog catalog, MountOperationCoordinator coordinator,
    IOperationLogger operationLogger, IConfiguration configuration, ILogger<MountStateMachineService> logger) : BackgroundService
{
    private sealed class Runtime(MountDescriptor descriptor)
    {
        public MountDescriptor Descriptor = descriptor;
        public MountHealthSnapshot Snapshot = new() { LocalPath = descriptor.Path };
        public bool Paused;
        public bool Running;
        public SmbMountStatus ObservedStatus = descriptor.Supported ? SmbMountStatus.NotMounted : SmbMountStatus.Unsupported;
        public DateTime? RemoteVerifiedAt;
        public CancellationTokenSource? ActiveCancellation;
        public string? ActiveAction;
        public Job? ActiveJob;
    }
    private sealed record Job(MountKey Key, DateTime Version, string Action, string Trigger, DateTime Due,
        Guid? TaskId = null, bool Lazy = false, string? ClientIp = null)
    {
        public bool DidMutate { get; set; }
        public Guid CorrelationId { get; } = Guid.NewGuid();
    }
    private readonly object gate = new();
    private readonly Dictionary<MountKey, Runtime> runtimes = [];
    private readonly HashSet<MountKey> deletedKeys = [];
    private readonly Dictionary<MountKey, Job> jobs = [];
    private readonly ConcurrentDictionary<Guid, MountTaskInfo> tasks = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private bool stopping;
    private int Concurrency => Math.Clamp(configuration.GetValue("MountScheduler:Concurrency", 3), 1, 16);
    private int BudgetSeconds => Math.Clamp(configuration.GetValue("MountScheduler:TaskTimeoutSeconds", 120), 10, 600);

    public MountHealthSnapshot? GetSnapshot(string path)
    {
        lock (gate) return runtimes.Values.FirstOrDefault(r => SamePath(r.Descriptor.Path, path))?.Snapshot;
    }
    public MountHealthSnapshot? GetSnapshot(string backend, Guid id)
    {
        lock (gate) return runtimes.TryGetValue(new(backend, id), out var r) ? r.Snapshot : null;
    }
    public MountTaskInfo? GetTask(Guid taskId) => tasks.GetValueOrDefault(taskId);
    public IReadOnlyList<MountHealthSnapshot> GetSnapshots()
    {
        lock (gate) return runtimes.Values.Select(r => r.Snapshot).ToArray();
    }
    public SmbMountStatus GetCachedStatus(string backend, Guid id)
    {
        lock (gate) return runtimes.TryGetValue(new(backend, id), out var r) ? r.ObservedStatus : SmbMountStatus.NotMounted;
    }
    public void RequestImmediateCheck(string path)
    {
        lock (gate)
            foreach (var r in runtimes.Values.Where(r => ContainsPath(r.Descriptor.Path, path) && r.Descriptor.Enabled))
                EnqueueCheck(r, "Immediate", DateTime.UtcNow);
        Signal();
    }
    public string? GetSmbMountRoot(string path)
    {
        lock (gate) return runtimes.Values.Where(r => r.Descriptor.Key.Backend == "smb" && ContainsPath(r.Descriptor.Path, path))
            .OrderByDescending(r => r.Descriptor.Path.Length).Select(r => r.Descriptor.Path).FirstOrDefault();
    }
    private static bool ContainsPath(string root, string path)
    {
        root = MountOperationCoordinator.NormalizePath(root);
        path = MountOperationCoordinator.NormalizePath(path);
        return path == root || path.StartsWith(root == "/" ? "/" : root + "/", StringComparison.Ordinal);
    }
    // Called while the controller holds the configuration lock.
    public void ConfigurationChanged(MountDescriptor descriptor)
    {
        lock (gate)
        {
            if (runtimes.TryGetValue(descriptor.Key, out var old))
                SystemStatusProvider.ManagedMountPoints.TryRemove(MountOperationCoordinator.NormalizePath(old.Descriptor.Path), out _);
            CancelQueued(descriptor.Key, "配置已变化，旧任务取消");
            runtimes[descriptor.Key] = new Runtime(descriptor) { Paused = old?.Paused == true,
                ObservedStatus = old?.ObservedStatus ?? SmbMountStatus.NotMounted };
            var updated = runtimes[descriptor.Key];
            updated.Snapshot = updated.Snapshot with { Backend = descriptor.Key.Backend, ManagementMode = Mode(updated) };
            Register(descriptor);
            EnqueueCheck(updated, "Configuration", DateTime.UtcNow);
        }
        Signal();
    }
    public void ConfigurationDeleted(string backend, Guid id)
    {
        lock (gate)
        {
            var key = new MountKey(backend, id);
            deletedKeys.Add(key);
            CancelQueued(key, "配置已删除");
            if (runtimes.Remove(key, out var r))
                SystemStatusProvider.ManagedMountPoints.TryRemove(MountOperationCoordinator.NormalizePath(r.Descriptor.Path), out _);
        }
    }

    public async Task<MountTaskInfo?> SubmitAsync(string backend, Guid id, string action, bool lazy = false, string? clientIp = null)
    {
            var descriptor = await catalog.LoadAsync(new(backend, id));
            if (descriptor is null) return null;
            lock (gate)
            {
                if (stopping) throw new InvalidOperationException("挂载服务正在停止");
                var queued = jobs.GetValueOrDefault(descriptor.Key);
                if (queued?.TaskId is null) queued = null;
                if (queued?.Action == action && queued.TaskId is { } queuedId && tasks.TryGetValue(queuedId, out var queuedTask)) return queuedTask;
                if (queued is null)
                {
                    var existing = tasks.Values.FirstOrDefault(t => t.Backend == backend && t.MountId == id && !t.Completed && t.Action == action);
                    if (existing is not null) return existing;
                }
                var runtime = runtimes.TryGetValue(descriptor.Key, out var current) && current.Descriptor.Version > descriptor.Version
                    ? current : Ensure(descriptor);
                descriptor = runtime.Descriptor;
                runtime.Paused = action is "Unmount" or "Delete";
                if (runtime.ActiveAction == "Check") runtime.ActiveCancellation?.Cancel();
                CancelQueued(descriptor.Key, "手动操作替代检查任务");
                var task = new MountTaskInfo { TaskId = Guid.NewGuid(), Backend = backend, MountId = id, Action = action };
                tasks[task.TaskId] = task;
                jobs[descriptor.Key] = new(descriptor.Key, descriptor.Version, action, "Manual", DateTime.UtcNow, task.TaskId, lazy, clientIp);
                runtime.Snapshot = runtime.Snapshot with { ExecutionPhase = runtime.Running ? runtime.Snapshot.ExecutionPhase : MountExecutionPhase.Queued,
                    ManagementMode = Mode(runtime), Trigger = "Manual", TaskId = task.TaskId, NextAttemptAt = null };
                Signal();
                return task;
            }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // HTTP startup does not wait for configuration or network operations.
        await Task.Yield();
        var workers = Enumerable.Range(0, Concurrency).Select(_ => WorkerAsync(stoppingToken)).ToArray();
        try
        {
            var nextSync = DateTime.MinValue;
            var initialSync = true;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            do
            {
                try
                {
                    if (DateTime.UtcNow >= nextSync)
                    {
                    var syncStarted = DateTime.Now;
                    var descriptors = await catalog.ListAsync();
                    lock (gate)
                    {
                        foreach (var descriptor in descriptors)
                        {
                            if (deletedKeys.Contains(descriptor.Key)) continue;
                            if (runtimes.TryGetValue(descriptor.Key, out var current) && (current.Running || current.Descriptor.Version > descriptor.Version)) continue;
                            var r = Ensure(descriptor);
                            Register(descriptor);
                            if (r.Snapshot.ExecutionPhase == MountExecutionPhase.WaitingAction || r.Running) continue;
                            if (!jobs.ContainsKey(descriptor.Key))
                                EnqueueCheck(r, initialSync ? "Startup" : "Periodic", r.Snapshot.NextCheckAt ?? DateTime.UtcNow);
                        }
                        var keys = descriptors.Select(d => d.Key).ToHashSet();
                        foreach (var key in runtimes.Keys.Where(k => !keys.Contains(k)).ToArray())
                        {
                            if (runtimes[key].Running || runtimes[key].Descriptor.Version > syncStarted) continue;
                            CancelQueued(key, "配置不存在");
                            var removed = runtimes[key];
                            runtimes.Remove(key);
                            SystemStatusProvider.ManagedMountPoints.TryRemove(MountOperationCoordinator.NormalizePath(removed.Descriptor.Path), out _);
                        }
                        deletedKeys.RemoveWhere(k => !keys.Contains(k));
                    }
                    PruneTasks();
                    nextSync = DateTime.UtcNow.AddSeconds(30);
                    initialSync = false;
                    }
                    Signal();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { logger.LogWarning(ex, "挂载配置同步失败，将再次尝试"); }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                stopping = true;
                foreach (var key in runtimes.Keys) CancelQueued(key, "挂载服务已停止");
            }
            await Task.WhenAll(workers);
        }
    }

    private async Task WorkerAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            Job? job;
            lock (gate)
            {
                job = jobs.Values.Where(j => j.Due <= DateTime.UtcNow && runtimes.TryGetValue(j.Key, out var r) && !r.Running)
                    .OrderByDescending(j => j.TaskId.HasValue).ThenBy(j => j.Due).FirstOrDefault();
                if (job is not null) { jobs.Remove(job.Key); runtimes[job.Key].Running = true; runtimes[job.Key].ActiveJob = job; }
            }
            if (job is null)
            {
                try { await wake.WaitAsync(TimeSpan.FromSeconds(1), stoppingToken); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            budget.CancelAfter(TimeSpan.FromSeconds(BudgetSeconds));
            lock (gate) if (runtimes.TryGetValue(job.Key, out var active) && ReferenceEquals(active.ActiveJob, job))
                { active.ActiveCancellation = budget; active.ActiveAction = job.Action; }
            if (job.Action == "Check" && HasManualJob(job.Key)) budget.Cancel();
            MountDescriptor? executed = null;
            using var logScope = logger.BeginScope(new Dictionary<string, object> { ["MountCorrelationId"] = job.CorrelationId, ["MountId"] = job.Key.Id });
            var taskClock = System.Diagnostics.Stopwatch.StartNew();
            logger.LogDebug("Mount task start CorrelationId={CorrelationId} MountId={MountId} Action={Action} Trigger={Trigger}", job.CorrelationId, job.Key.Id, job.Action, job.Trigger);
            try
            {
                await coordinator.RunWithConfigurationLockAsync(job.Key.Backend, job.Key.Id, async () =>
                {
                    var fresh = await catalog.LoadAsync(job.Key);
                    if (fresh is null || fresh.Version != job.Version)
                    {
                        Complete(job, new(false, MountFailureKind.Cancelled, "配置已变化，任务取消"));
                        lock (gate)
                        {
                            if (fresh is null) ConfigurationDeleted(job.Key.Backend, job.Key.Id);
                            else if (runtimes.TryGetValue(job.Key, out var changed) && ReferenceEquals(changed.ActiveJob, job)) Ensure(fresh);
                        }
                        return false;
                    }
                    executed = fresh;
                    await coordinator.RunWithMountLockAsync(fresh.Path, () => ExecuteJobAsync(job, fresh, budget.Token), budget.Token);
                    return true;
                }, budget.Token);
            }
            catch (OperationCanceledException)
            {
                var kind = stoppingToken.IsCancellationRequested || (job.Action == "Check" && HasManualJob(job.Key))
                    ? MountFailureKind.Cancelled : MountFailureKind.Timeout;
                Failed(job, new(false, kind, kind == MountFailureKind.Cancelled ? "任务已取消" : "任务超过总时间预算"));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "挂载任务失败：{Backend}/{Id}", job.Key.Backend, job.Key.Id);
                Failed(job, new(false, MountFailureKind.Failed, "挂载任务执行异常，请查看服务日志"));
            }
            finally
            {
                if (executed is not null)
                {
                    var actual = executed.Status();
                    lock (gate) if (runtimes.TryGetValue(job.Key, out var current) && ReferenceEquals(current.ActiveJob, job)) current.ObservedStatus = actual;
                }
                logger.LogDebug("Mount task end CorrelationId={CorrelationId} ElapsedMs={Elapsed}", job.CorrelationId, taskClock.ElapsedMilliseconds);
                if (executed is not null && (job.TaskId.HasValue || job.DidMutate))
                {
                    var task = job.TaskId is { } taskId ? GetTask(taskId) : null;
                    var health = GetSnapshot(job.Key.Backend, job.Key.Id);
                    try
                    {
                        await operationLogger.LogAsync(job.Trigger == "Manual" ? job.Action : "自动挂载恢复",
                            executed.Key.Backend + "挂载", executed.Name,
                            $"{executed.Path}；{task?.Message ?? health?.LastError ?? health?.State.ToString() ?? "任务已结束"}",
                            task?.Success ?? health?.State == MountHealthState.Healthy, clientIp: job.ClientIp);
                    }
                    catch (Exception ex) { logger.LogWarning(ex, "挂载任务操作日志写入失败"); }
                }
                lock (gate) if (runtimes.TryGetValue(job.Key, out var r) && ReferenceEquals(r.ActiveJob, job))
                {
                    r.Running = false; r.ActiveCancellation = null; r.ActiveAction = null; r.ActiveJob = null;
                    if (!stoppingToken.IsCancellationRequested && r.Snapshot.ExecutionPhase != MountExecutionPhase.WaitingAction
                        && !jobs.ContainsKey(job.Key)) EnqueueCheck(r, "Periodic", r.Snapshot.NextCheckAt ?? DateTime.UtcNow);
                }
                Signal();
            }
        }
    }

    private async Task ExecuteJobAsync(Job job, MountDescriptor descriptor, CancellationToken ct)
    {
        Runtime runtime;
        lock (gate) runtime = Ensure(descriptor);
        SetPhase(runtime, job, MountExecutionPhase.Probing);
        ct.ThrowIfCancellationRequested();
        if (MountProbeProcessGuard.IsBlocked(descriptor.Path))
        {
            Failed(job, new(false, MountFailureKind.Busy, "上次文件系统探针尚未退出，暂停创建探针和挂载操作")); return;
        }
        var status = descriptor.Status();
        lock (gate) runtime.ObservedStatus = status;
        if (status == SmbMountStatus.Abnormal)
        {
            Failed(job, new(false, MountFailureKind.Conflict, "挂载身份与配置不一致，拒绝自动操作")); return;
        }
        if (job.Action is "Unmount" or "Delete")
        {
            SetPhase(runtime, job, MountExecutionPhase.Unmounting);
            var result = status is SmbMountStatus.NotMounted or SmbMountStatus.Unsupported ? new MountOperationResult(true)
                : await descriptor.Unmount(job.Lazy, ct);
            ct.ThrowIfCancellationRequested();
            if (!result.Success) Failed(job, result);
            else if (job.Action == "Delete")
            {
                await descriptor.Delete(ct);
                ConfigurationDeleted(descriptor.Key.Backend, descriptor.Key.Id);
                Complete(job, new(true, Error: "配置已删除；待上传缓存保留"));
            }
            else
            {
                lock (gate) runtime.ObservedStatus = descriptor.Status();
                Finish(runtime, job, MountHealthState.NotMounted, "已卸载，自动恢复已暂停");
            }
            return;
        }
        if (!descriptor.Supported)
        {
            lock (gate) runtime.ObservedStatus = SmbMountStatus.Unsupported;
            Failed(job, new(false, MountFailureKind.Unsupported, "当前环境缺少挂载能力或依赖")); return;
        }
        if (!descriptor.Enabled && job.Action == "Check")
        {
            Finish(runtime, job, status == SmbMountStatus.Mounted ? MountHealthState.Unknown : MountHealthState.NotMounted, "配置已禁用"); return;
        }
        bool automatic;
        lock (gate) automatic = descriptor.Enabled && descriptor.AutoMount && !runtime.Paused;
        if (status == SmbMountStatus.NotMounted && !automatic && job.Action != "Mount")
        {
            Finish(runtime, job, MountHealthState.NotMounted, "未挂载"); return;
        }
        // Mounted FUSE filesystems do a remote bypass check every five minutes; always check before recovery.
        var needRemote = descriptor.Key.Backend == "smb" || status != SmbMountStatus.Mounted || job.Action == "Mount"
            || runtime.RemoteVerifiedAt is null || DateTime.UtcNow - runtime.RemoteVerifiedAt > TimeSpan.FromMinutes(5);
        if (needRemote)
        {
            var remote = await descriptor.Remote(ct);
            ct.ThrowIfCancellationRequested();
            if (!remote.Success) { Failed(job, remote); return; }
            runtime.RemoteVerifiedAt = DateTime.UtcNow;
        }
        if (status == SmbMountStatus.Mounted)
        {
            var startupSmb = job.Trigger == "Startup" && job.Action == "Check" && descriptor.Key.Backend == "smb";
            var startupIdentity = startupSmb ? SmbMountService.ReadIdentity(descriptor.Path) : null;
            if (startupSmb)
                logger.LogInformation("SMB startup existing mount CorrelationId={CorrelationId} MountId={MountId} Identity={Identity}",
                    job.CorrelationId, descriptor.Key.Id, startupIdentity);
            var verified = await descriptor.Verify(ct);
            ct.ThrowIfCancellationRequested();
            var fastRecovery = false;
            if (startupSmb && automatic && verified.Kind == MountFailureKind.Timeout)
            {
                logger.LogWarning("SMB startup recheck CorrelationId={CorrelationId} MountId={MountId} Error={Error} DelayMs=1500",
                    job.CorrelationId, descriptor.Key.Id, verified.Error);
                await Task.Delay(1500, ct);
                if (MountProbeProcessGuard.IsBlocked(descriptor.Path))
                {
                    Failed(job, new(false, MountFailureKind.Busy, "启动探针尚未退出，暂停快速恢复")); return;
                }
                if (startupIdentity is null || SmbMountService.ReadIdentity(descriptor.Path) != startupIdentity)
                {
                    Failed(job, new(false, MountFailureKind.Conflict, "启动复检时挂载身份变化，停止快速恢复")); return;
                }
                verified = await descriptor.Verify(ct);
                ct.ThrowIfCancellationRequested();
                fastRecovery = verified.Kind == MountFailureKind.Timeout;
                if (fastRecovery)
                    logger.LogWarning("SMB startup fast recovery CorrelationId={CorrelationId} MountId={MountId} Identity={Identity} Reason=TwoAccessTimeouts",
                        job.CorrelationId, descriptor.Key.Id, startupIdentity);
            }
            if (verified.Kind == MountFailureKind.PermissionDenied) { Failed(job, verified); return; }
            if (verified.Success) { Finish(runtime, job, MountHealthState.Healthy, "现有挂载访问验证通过，本轮未重新挂载"); return; }
            int failures;
            lock (gate)
            {
                failures = Math.Min(1000, runtime.Snapshot.FailureCount + 1);
                runtime.Snapshot = runtime.Snapshot with { FailureCount = failures };
            }
            if ((!fastRecovery && failures < 3) || !automatic)
            {
                Failed(job, verified, MountHealthState.Stale, incrementAttempt: false); return;
            }
            var remote = await descriptor.Remote(ct);
            ct.ThrowIfCancellationRequested();
            if (!remote.Success) { Failed(job, remote); return; }
            SetPhase(runtime, job, MountExecutionPhase.Unmounting);
            ct.ThrowIfCancellationRequested();
            if (MountProbeProcessGuard.IsBlocked(descriptor.Path))
            {
                Failed(job, new(false, MountFailureKind.Busy, "文件系统探针尚未退出，保留原挂载")); return;
            }
            var unmounted = await descriptor.Unmount(descriptor.Key.Backend == "smb", ct);
            ct.ThrowIfCancellationRequested();
            if (!unmounted.Success) { Failed(job, unmounted); return; }
        }
        SetPhase(runtime, job, MountExecutionPhase.Mounting);
        ct.ThrowIfCancellationRequested();
        var mounted = await descriptor.Mount(ct);
        ct.ThrowIfCancellationRequested();
        if (!mounted.Success) { Failed(job, mounted); return; }
        SetPhase(runtime, job, MountExecutionPhase.Verifying);
        lock (gate) runtime.ObservedStatus = descriptor.Status();
        var final = runtime.ObservedStatus == SmbMountStatus.Mounted ? await descriptor.Verify(ct)
            : new MountOperationResult(false, MountFailureKind.Conflict, "挂载后身份验证失败");
        ct.ThrowIfCancellationRequested();
        if (final.Success) Finish(runtime, job, MountHealthState.Healthy, "挂载与访问验证通过");
        else Failed(job, final);
    }

    private Runtime Ensure(MountDescriptor descriptor)
    {
        if (!runtimes.TryGetValue(descriptor.Key, out var r))
            runtimes[descriptor.Key] = r = new Runtime(descriptor) { ObservedStatus = descriptor.Supported ? descriptor.Status() : SmbMountStatus.Unsupported };
        if (r.Descriptor.Version != descriptor.Version)
        {
            SystemStatusProvider.ManagedMountPoints.TryRemove(MountOperationCoordinator.NormalizePath(r.Descriptor.Path), out _);
            CancelQueued(descriptor.Key, "配置已变化");
            r.Snapshot = new() { LocalPath = descriptor.Path };
            r.RemoteVerifiedAt = null;
            r.ObservedStatus = descriptor.Supported ? descriptor.Status() : SmbMountStatus.Unsupported;
            Register(descriptor);
        }
        r.Descriptor = descriptor;
        r.Snapshot = r.Snapshot with { ManagementMode = Mode(r), Backend = descriptor.Key.Backend };
        return r;
    }
    private static MountManagementMode Mode(Runtime r) => !r.Descriptor.Enabled ? MountManagementMode.Disabled
        : r.Paused ? MountManagementMode.ManualPaused : r.Descriptor.AutoMount ? MountManagementMode.Automatic : MountManagementMode.MonitorOnly;
    private void SetPhase(Runtime r, Job job, MountExecutionPhase phase)
    {
        logger.LogDebug("Mount phase CorrelationId={CorrelationId} MountId={MountId} Phase={Phase}", job.CorrelationId, job.Key.Id, phase);
        if (phase is MountExecutionPhase.Mounting or MountExecutionPhase.Unmounting) job.DidMutate = true;
        lock (gate) r.Snapshot = r.Snapshot with { ExecutionPhase = phase, Trigger = job.Trigger,
            TaskId = job.TaskId, ManagementMode = Mode(r), NextCheckAt = null, NextAttemptAt = null,
            State = phase is MountExecutionPhase.Mounting or MountExecutionPhase.Unmounting or MountExecutionPhase.Verifying
                ? MountHealthState.Recovering : r.Snapshot.State };
    }
    private void Finish(Runtime r, Job job, MountHealthState state, string message)
    {
        if (r.Snapshot.State != state || job.DidMutate || job.TaskId.HasValue)
            logger.LogInformation("Mount state CorrelationId={CorrelationId} MountId={MountId} Before={Before} After={After} Reason={Reason}", job.CorrelationId, job.Key.Id, r.Snapshot.State, state, message);
        lock (gate) r.Snapshot = r.Snapshot with { State = state, ExecutionPhase = MountExecutionPhase.Idle,
            ManagementMode = Mode(r), LastCheckedAt = DateTime.UtcNow, NextCheckAt = DateTime.UtcNow.AddSeconds(30 + Random.Shared.Next(0, 6)),
            FailureKind = MountFailureKind.None, LastError = null, FailureCount = 0, RecoveryAttemptCount = 0, NextAttemptAt = null };
        Complete(job, new(true, Error: message));
    }
    private void Failed(Job job, MountOperationResult result, MountHealthState? state = null, bool incrementAttempt = true)
    {
        logger.LogWarning("Mount task failed CorrelationId={CorrelationId} Backend={Backend} MountId={MountId} Action={Action} Trigger={Trigger} TaskId={TaskId} Kind={Kind} Error={Error}",
            job.CorrelationId, job.Key.Backend, job.Key.Id, job.Action, job.Trigger, job.TaskId, result.Kind, result.Error);
        lock (gate)
        {
            if (runtimes.TryGetValue(job.Key, out var r) && ReferenceEquals(r.ActiveJob, job))
            {
                var waitAction = result.Kind is MountFailureKind.AuthenticationFailed or MountFailureKind.Conflict or MountFailureKind.Unsupported or MountFailureKind.PermissionDenied;
                var attempts = r.Snapshot.RecoveryAttemptCount + (incrementAttempt && result.Kind != MountFailureKind.Cancelled ? 1 : 0);
                var seconds = incrementAttempt ? new[] { 15, 30, 60, 120, 300, 600 }[Math.Clamp(attempts - 1, 0, 5)] : 30;
                var due = DateTime.UtcNow.AddSeconds(seconds + Random.Shared.Next(0, Math.Max(2, seconds / 5)));
                r.Snapshot = r.Snapshot with { State = state ?? (result.Kind == MountFailureKind.Cancelled ? MountHealthState.Unknown
                    : result.Kind == MountFailureKind.Unreachable ? MountHealthState.ServerUnreachable
                    : result.Kind == MountFailureKind.Unsupported ? MountHealthState.Unsupported : MountHealthState.RecoveryFailed),
                    ExecutionPhase = waitAction ? MountExecutionPhase.WaitingAction : result.Kind == MountFailureKind.Cancelled ? MountExecutionPhase.Idle : MountExecutionPhase.WaitingRetry,
                    ManagementMode = Mode(r), FailureKind = result.Kind, LastError = result.Error, LastCheckedAt = DateTime.UtcNow,
                    FailureCount = result.Kind is MountFailureKind.Unreachable or MountFailureKind.AuthenticationFailed or MountFailureKind.Conflict or MountFailureKind.Unsupported ? 0 : r.Snapshot.FailureCount,
                    RecoveryAttemptCount = attempts, NextCheckAt = waitAction ? null : result.Kind == MountFailureKind.Cancelled ? DateTime.UtcNow : due,
                    NextAttemptAt = waitAction || result.Kind == MountFailureKind.Cancelled ? null : due };
                logger.LogWarning("Mount retry CorrelationId={CorrelationId} MountId={MountId} Phase={Phase} Failures={Failures} Attempts={Attempts} NextCheck={NextCheck}",
                    job.CorrelationId, job.Key.Id, r.Snapshot.ExecutionPhase, r.Snapshot.FailureCount, attempts, r.Snapshot.NextCheckAt);
            }
        }
        Complete(job, result);
    }
    private void Complete(Job job, MountOperationResult result)
    {
        if (job.TaskId is { } id && tasks.TryGetValue(id, out var task))
            tasks[id] = task with { Completed = true, Success = result.Success, Message = result.Error ?? (result.Success ? "操作完成" : "操作失败"),
                FailureKind = result.Kind, CompletedAt = DateTime.UtcNow };
    }
    private void EnqueueCheck(Runtime r, string trigger, DateTime due)
    {
        if (!r.Descriptor.Enabled || r.Running) return;
        if (trigger == "Immediate" && r.Snapshot.ExecutionPhase is MountExecutionPhase.WaitingAction or MountExecutionPhase.WaitingRetry) return;
        var job = new Job(r.Descriptor.Key, r.Descriptor.Version, "Check", trigger, due);
        if (!jobs.TryGetValue(r.Descriptor.Key, out var existing)) jobs[r.Descriptor.Key] = job;
        else if (existing.Action == "Check" && existing.Due > due) jobs[r.Descriptor.Key] = job;
        if (due <= DateTime.UtcNow) r.Snapshot = r.Snapshot with { ExecutionPhase = MountExecutionPhase.Queued, Trigger = trigger };
    }
    private void CancelQueued(MountKey key, string reason)
    {
        if (jobs.Remove(key, out var job)) Complete(job, new(false, MountFailureKind.Cancelled, reason));
    }
    private static bool SamePath(string a, string b) => MountOperationCoordinator.NormalizePath(a) == MountOperationCoordinator.NormalizePath(b);
    private static void Register(MountDescriptor descriptor)
    {
        if (descriptor.Enabled) SystemStatusProvider.ManagedMountPoints[MountOperationCoordinator.NormalizePath(descriptor.Path)] = 0;
    }
    private void Signal() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    private bool HasManualJob(MountKey key) { lock (gate) return jobs.TryGetValue(key, out var job) && job.TaskId.HasValue; }
    private void PruneTasks()
    {
        foreach (var task in tasks.Values.Where(t => t.Completed && t.CompletedAt < DateTime.UtcNow.AddMinutes(-30))) tasks.TryRemove(task.TaskId, out _);
        foreach (var task in tasks.Values.Where(t => t.Completed).OrderByDescending(t => t.CompletedAt).Skip(256)) tasks.TryRemove(task.TaskId, out _);
    }
}
