using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Infrastructure.Features.Workers.Adapters;
using LinuxWebTool.Infrastructure.Features.Workers.Supervisor;
using LinuxWebTool.Contracts.Shared.Support;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Workers;

/// <summary>
/// SaaS 函数微服务与长时守护进程管理服务核心实现。
/// </summary>
public sealed partial class WorkersManager : IWorkersManager, IHostedService
{
    private readonly string _workersRoot;
    private readonly WorkerPortManager _portManager = new();
    private readonly ConcurrentDictionary<string, WorkerProcessContext> _activeProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WorkerStateMachine> _stateMachines = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, WorkerProcessRingBuffer> _logBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _instanceLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _restartCts = new(StringComparer.OrdinalIgnoreCase);
    private readonly IReadOnlyDictionary<WorkerRuntimeType, IWorkerRuntimeAdapter> _adapters;
    private readonly ConcurrentDictionary<WorkerRuntimeType, (bool Available, string? Reason, IReadOnlyList<string> Missing, DateTime ExpireAt)> _runtimeProbeCache = new();
    private readonly ILogger<WorkersManager> _logger;
    private CancellationTokenSource? _stoppingCts;
    private long _generationCounter;

    private SemaphoreSlim GetInstanceLock(string id) => _instanceLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));

    public WorkersManager(ILogger<WorkersManager> logger, string? dataRoot = null)
    {
        _logger = logger;
        var baseDir = dataRoot ?? AppContext.BaseDirectory;
        _workersRoot = Path.Combine(baseDir, "data", "workers");

        _adapters = new Dictionary<WorkerRuntimeType, IWorkerRuntimeAdapter>
        {
            [WorkerRuntimeType.Node] = new NodeRuntimeAdapter(),
            [WorkerRuntimeType.Python] = new PythonRuntimeAdapter(),
            [WorkerRuntimeType.DotNet] = new DotNetRuntimeAdapter(),
            [WorkerRuntimeType.Bash] = new ShellRuntimeAdapter(),
            [WorkerRuntimeType.PowerShell] = new PowerShellRuntimeAdapter(),
            [WorkerRuntimeType.Batch] = new BatchRuntimeAdapter(),
            [WorkerRuntimeType.Native] = new NativeRuntimeAdapter()
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = new CancellationTokenSource();
        EnsureDefaultWorkersCreated();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts?.Cancel();
        foreach (var (id, ctx) in _activeProcesses)
        {
            try
            {
                KillProcessTree(ctx.Process);
                _portManager.ReleasePort(id);
            }
            catch { }
        }
        _activeProcesses.Clear();
        return Task.CompletedTask;
    }

    public async ValueTask<IReadOnlyList<WorkerInstanceDto>> GetAllAsync(CancellationToken ct = default)
    {
        EnsureDefaultWorkersCreated();
        var result = new List<WorkerInstanceDto>();
        if (!Directory.Exists(_workersRoot)) return result;

        foreach (var dir in Directory.GetDirectories(_workersRoot))
        {
            var manifestFile = Path.Combine(dir, "worker.json");
            if (!File.Exists(manifestFile)) continue;

            try
            {
                var json = await File.ReadAllTextAsync(manifestFile, ct);
                var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
                if (manifest != null)
                {
                    result.Add(BuildDto(manifest, dir));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取 Worker 清单异常: {Dir}", dir);
            }
        }

        return result;
    }

    public async ValueTask<WorkerInstanceDto?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        var manifestFile = Path.Combine(dir, "worker.json");
        if (!File.Exists(manifestFile)) return null;

        var json = await File.ReadAllTextAsync(manifestFile, ct);
        var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
        return manifest != null ? BuildDto(manifest, dir) : null;
    }

    public async ValueTask<bool> StartAsync(string id, CancellationToken ct = default)
    {
        var (success, _) = await StartWithResultAsync(id, ct);
        return success;
    }

    public async ValueTask<(bool Success, string? ErrorMessage)> StartWithResultAsync(string id, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id))
        {
            return (false, $"Worker ID '{id}' 格式无效。");
        }

        string safeDir;
        try
        {
            safeDir = PluginSecurityGuard.EnsureSafePluginDirectory(_workersRoot, id);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }

        var manifestFile = Path.Combine(safeDir, "worker.json");
        if (!File.Exists(manifestFile)) return (false, $"Worker '{id}' 不存在。");

        var json = await File.ReadAllTextAsync(manifestFile, ct);
        var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
        if (manifest == null || !PluginSecurityGuard.IsValidPluginId(manifest.Id))
        {
            return (false, "清单文件损坏或格式无效。");
        }

        if (!manifest.IsEnabled || File.Exists(Path.Combine(safeDir, ".disabled")))
        {
            _logger.LogWarning("Worker {Id} 已被显式禁用，无法启动。", id);
            return (false, $"Worker '{id}' 已被显式禁用，请先启用。");
        }

        var sem = GetInstanceLock(id);
        await sem.WaitAsync(ct);
        try
        {
            var sm = _stateMachines.GetOrAdd(id, _ => new WorkerStateMachine(id, manifest.Supervision));
            sm.Reset();
            return StartProcessInternal(manifest, safeDir, sm);
        }
        finally
        {
            sem.Release();
        }
    }

    public async ValueTask<bool> StopAsync(string id, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return false;

        // 1. 取消正在等待的崩溃退避自愈任务
        if (_restartCts.TryRemove(id, out var pendingCts))
        {
            try { pendingCts.Cancel(); pendingCts.Dispose(); } catch { }
        }

        // 2. 状态机记录人工停止意图
        if (_stateMachines.TryGetValue(id, out var sm))
        {
            sm.NotifyStopped();
        }

        // 3. 实例串行锁保护
        var sem = GetInstanceLock(id);
        await sem.WaitAsync(ct);
        var stopped = true;
        try
        {
            if (_activeProcesses.TryRemove(id, out var ctx))
            {
                try
                {
                    KillProcessTree(ctx.Process);
                    ctx.Process.WaitForExit(3000);
                    stopped = ctx.Process.HasExited;
                }
                catch
                {
                    stopped = ctx.Process.HasExited;
                }
            }
            _portManager.ReleasePort(id);
        }
        finally
        {
            sem.Release();
        }

        var logBuffer = GetLogBuffer(id);
        logBuffer.AppendLine(stopped ? "进程已被用户手动停止。" : "进程停止指令已下发，但进程仍处于终止等待中。");
        return stopped;
    }

    public async ValueTask<bool> RestartAsync(string id, CancellationToken ct = default)
    {
        await StopAsync(id, ct);
        await Task.Delay(300, ct);
        return await StartAsync(id, ct);
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(id)) return false;

        await StopAsync(id, ct);
        _stateMachines.TryRemove(id, out _);
        _logBuffers.TryRemove(id, out _);

        var sem = GetInstanceLock(id);
        await sem.WaitAsync(ct);
        try
        {
            var dir = PluginSecurityGuard.EnsureSafePluginDirectory(_workersRoot, id);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
                return true;
            }
            return false;
        }
        finally
        {
            sem.Release();
        }
    }

    public async ValueTask<WorkerInstanceDto> CreateOrUpdateAsync(WorkerCreateOrUpdateRequest request, CancellationToken ct = default)
    {
        if (!PluginSecurityGuard.IsValidPluginId(request.Id))
        {
            throw new ArgumentException($"Worker ID '{request.Id}' 格式无效，必须为 1-64 位的安全标识。");
        }

        var dir = PluginSecurityGuard.EnsureSafePluginDirectory(_workersRoot, request.Id);
        Directory.CreateDirectory(dir);

        var manifest = new WorkerManifest
        {
            ManifestVersion = 1,
            HostApiVersion = "1",
            Id = request.Id,
            Name = request.Name,
            Description = request.Description,
            Mode = request.Mode,
            Runtime = request.Runtime,
            Entrypoint = request.Entrypoint,
            Arguments = request.Arguments,
            Supervision = new WorkerSupervisionConfig { AutoRestart = request.AutoRestart },
            Storage = new WorkerStorageConfig { EnableSqlite = request.EnableSqlite, EnableKv = request.EnableKv },
            EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>()
        };

        var manifestFile = Path.Combine(dir, "worker.json");
        await File.WriteAllTextAsync(manifestFile, JsonSerializer.Serialize(manifest, WorkerJsonContext.Default.WorkerManifest), ct);

        if (!string.IsNullOrWhiteSpace(request.ScriptContent))
        {
            var entryFile = Path.Combine(dir, request.Entrypoint);
            var parentDir = Path.GetDirectoryName(entryFile);
            if (!string.IsNullOrEmpty(parentDir)) Directory.CreateDirectory(parentDir);
            await File.WriteAllTextAsync(entryFile, request.ScriptContent, ct);
        }

        return BuildDto(manifest, dir);
    }

    public ValueTask<IReadOnlyList<string>> GetLogsAsync(string id, int limit = 200, CancellationToken ct = default)
    {
        var buffer = GetLogBuffer(id);
        return ValueTask.FromResult(buffer.GetRecentLines(limit));
    }

    private (bool Success, string? ErrorMessage) StartProcessInternal(WorkerManifest manifest, string dir, WorkerStateMachine sm)
    {
        if (_activeProcesses.TryGetValue(manifest.Id, out var existing))
        {
            if (!existing.Process.HasExited) return (true, null);
            _activeProcesses.TryRemove(manifest.Id, out _);
        }

        if (!_adapters.TryGetValue(manifest.Runtime, out var adapter))
        {
            var err = $"未支持的运行时: {manifest.Runtime}";
            sm.RecordExit(-1, 0, err);
            return (false, err);
        }

        if (!adapter.CheckAvailable(out var missingReason, out var missingRunners))
        {
            _runtimeProbeCache[manifest.Runtime] = (false, missingReason, missingRunners, DateTime.UtcNow.AddSeconds(60));
            sm.MarkMissingRuntime(missingReason ?? "缺少必需运行时");
            GetLogBuffer(manifest.Id).AppendLine($"[ERROR] 环境检查失败: {missingReason}");
            return (false, missingReason);
        }

        _runtimeProbeCache[manifest.Runtime] = (true, null, [], DateTime.UtcNow.AddMinutes(5));

        int? port = null;
        if (manifest.Mode == WorkerServiceMode.Service)
        {
            port = _portManager.AllocatePort(manifest.Id, manifest.Network.InternalPort);
        }

        var spec = adapter.BuildStartSpec(manifest, dir, port);
        var logBuffer = GetLogBuffer(manifest.Id);

        var runId = Interlocked.Increment(ref _generationCounter);
        try
        {
            sm.NotifyStarting(runId);
            var startInfo = new ProcessStartInfo
            {
                FileName = spec.FileName,
                WorkingDirectory = spec.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (spec.ArgumentList != null && spec.ArgumentList.Count > 0)
            {
                foreach (var arg in spec.ArgumentList)
                {
                    startInfo.ArgumentList.Add(arg);
                }
            }
            else if (!string.IsNullOrEmpty(spec.Arguments))
            {
                startInfo.Arguments = spec.Arguments;
            }

            foreach (var (k, v) in spec.EnvironmentVariables)
            {
                startInfo.EnvironmentVariables[k] = v;
            }

            startInfo.EnvironmentVariables["LWT_RUN_ID"] = runId.ToString();

            var proc = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) logBuffer.AppendLine(e.Data);
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) logBuffer.AppendLine($"[STDERR] {e.Data}");
            };

            proc.Exited += (_, _) =>
            {
                HandleProcessExit(manifest, dir, sm, proc.ExitCode, runId);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _activeProcesses[manifest.Id] = new WorkerProcessContext(proc, port, DateTime.UtcNow, runId);
            logBuffer.AppendLine($"Worker 进程启动成功 (PID: {proc.Id}, RUN_ID: {runId}{(port.HasValue ? $", PORT: {port}" : "")})");

            if (manifest.Mode == WorkerServiceMode.Daemon)
            {
                sm.NotifyRunning(runId, readiness: "Unsupported");
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    if (_activeProcesses.TryGetValue(manifest.Id, out var cur) && cur.RunId == runId && !cur.Process.HasExited)
                    {
                        sm.NotifyRunning(runId, readiness: port.HasValue ? "Ready" : "Unsupported");
                    }
                });
            }

            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 Worker 失败: {Id}", manifest.Id);
            sm.RecordExit(-1, runId, ex.Message);
            logBuffer.AppendLine($"[FATAL] 启动异常: {ex.Message}");
            return (false, ex.Message);
        }
    }

    private void HandleProcessExit(WorkerManifest manifest, string dir, WorkerStateMachine sm, int exitCode, long runId)
    {
        if (_activeProcesses.TryGetValue(manifest.Id, out var cur) && cur.RunId != runId)
        {
            return;
        }

        _activeProcesses.TryRemove(manifest.Id, out _);
        _portManager.ReleasePort(manifest.Id);

        var logBuffer = GetLogBuffer(manifest.Id);
        logBuffer.AppendLine($"Worker 进程已退出 (RUN_ID: {runId})，ExitCode: {exitCode}");

        if (sm.IsStopRequested || sm.State == WorkerState.Stopped) return;

        if (!manifest.IsEnabled || File.Exists(Path.Combine(dir, ".disabled")))
        {
            logBuffer.AppendLine("Worker 已被显式禁用，取消自动拉起守护。");
            return;
        }

        var (shouldRestart, delay, nextState) = sm.RecordExit(exitCode, runId);
        if (!manifest.Supervision.AutoRestart || !shouldRestart)
        {
            logBuffer.AppendLine(sm.LastError ?? "进程退出且不再自动重启。");
            return;
        }

        logBuffer.AppendLine($"触发自动自愈，将在 {delay.TotalSeconds} 秒后重新拉起进程 (重试计数: {sm.FastCrashCount})...");
        var cts = new CancellationTokenSource();
        _restartCts[manifest.Id] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _stoppingCts?.Token ?? CancellationToken.None);
                await Task.Delay(delay, linked.Token);
                if (cts.IsCancellationRequested || sm.IsStopRequested) return;

                var sem = GetInstanceLock(manifest.Id);
                await sem.WaitAsync(linked.Token);
                try
                {
                    if (!sm.IsStopRequested && (sm.State == WorkerState.Backoff || sm.State == WorkerState.Starting))
                    {
                        StartProcessInternal(manifest, dir, sm);
                    }
                }
                finally
                {
                    sem.Release();
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                _restartCts.TryRemove(manifest.Id, out _);
                cts.Dispose();
            }
        });
    }

    private WorkerInstanceDto BuildDto(WorkerManifest manifest, string dir)
    {
        _activeProcesses.TryGetValue(manifest.Id, out var ctx);
        var isAlive = ctx != null && !ctx.Process.HasExited;

        var sm = _stateMachines.GetOrAdd(manifest.Id, _ => new WorkerStateMachine(manifest.Id, manifest.Supervision));
        var state = isAlive ? (sm.State == WorkerState.Starting ? WorkerState.Starting : WorkerState.Running) : sm.State;

        var isEnabled = manifest.IsEnabled && !File.Exists(Path.Combine(dir, ".disabled"));
        var (isAvailable, missingReason, missingRunners) = CheckRuntimeAvailability(manifest.Runtime);
        var missingDependencies = isAvailable ? [] : missingRunners;
        string? installGuideText = null;
        if (!isAvailable && _adapters.TryGetValue(manifest.Runtime, out var currentAdapter))
        {
            var distroKey = ResolveCurrentDistroKey();
            installGuideText = currentAdapter.ResolveInstallGuide(distroKey);
            if (!isAlive && isEnabled && state != WorkerState.Degraded)
            {
                state = WorkerState.MissingRuntime;
            }
        }

        int? boundPort = ctx?.BoundPort ?? _portManager.GetPort(manifest.Id);
        string? accessUrl = manifest.Mode == WorkerServiceMode.Service ? $"/w/{manifest.Id}/" : null;

        var sqlitePath = Path.Combine(dir, "data.db");
        var kvPath = Path.Combine(dir, "kv.db");

        WorkerSourceMetadata? source = null;
        var sourceFile = Path.Combine(dir, "source.json");
        if (File.Exists(sourceFile))
        {
            try
            {
                var sJson = File.ReadAllText(sourceFile);
                source = JsonSerializer.Deserialize(sJson, WorkerJsonContext.Default.WorkerSourceMetadata);
            }
            catch { }
        }

        return new WorkerInstanceDto
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            Description = manifest.Description,
            Author = manifest.Author,
            Mode = manifest.Mode,
            Runtime = manifest.Runtime,
            Entrypoint = manifest.Entrypoint,
            State = isEnabled ? state : WorkerState.Stopped,
            Readiness = isEnabled ? (isAlive ? sm.Readiness : "Stopped") : "Disabled",
            ProcessId = isAlive ? ctx!.Process.Id : null,
            BoundPort = boundPort,
            AccessUrl = accessUrl,
            StartedAt = ctx?.StartedAt ?? sm.StartedAt,
            UptimeSeconds = isAlive && ctx != null ? (long)(DateTime.UtcNow - ctx.StartedAt).TotalSeconds : 0,
            FastCrashCount = sm.FastCrashCount,
            LastError = sm.LastError,
            AutoRestart = manifest.Supervision.AutoRestart,
            EnableSqlite = manifest.Storage.EnableSqlite,
            EnableKv = manifest.Storage.EnableKv,
            SqliteSizeBytes = File.Exists(sqlitePath) ? new FileInfo(sqlitePath).Length : 0,
            KvSizeBytes = File.Exists(kvPath) ? new FileInfo(kvPath).Length : 0,
            DataDirectory = dir,
            IsEnabled = isEnabled,
            Source = source,
            MissingDependencies = missingDependencies,
            InstallGuideText = installGuideText
        };
    }

    private (bool Available, string? Reason, IReadOnlyList<string> Missing) CheckRuntimeAvailability(WorkerRuntimeType runtime)
    {
        var now = DateTime.UtcNow;
        if (_runtimeProbeCache.TryGetValue(runtime, out var cached) && now < cached.ExpireAt)
        {
            return (cached.Available, cached.Reason, cached.Missing);
        }

        if (!_adapters.TryGetValue(runtime, out var adapter))
        {
            var res = (false, $"未支持的运行时: {runtime}", (IReadOnlyList<string>)[runtime.ToString()]);
            _runtimeProbeCache[runtime] = (res.Item1, res.Item2, res.Item3, now.AddMinutes(5));
            return res;
        }

        var available = adapter.CheckAvailable(out var reason, out var missing);
        var entry = (available, reason, missing, now.AddSeconds(60));
        _runtimeProbeCache[runtime] = entry;
        return (entry.available, entry.reason, entry.missing);
    }

    private static string ResolveCurrentDistroKey()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (File.Exists("/etc/alpine-release")) return "alpine";
        if (File.Exists("/etc/redhat-release")) return "rhel";
        return "debian";
    }

    private WorkerProcessRingBuffer GetLogBuffer(string id)
    {
        return _logBuffers.GetOrAdd(id, _ => new WorkerProcessRingBuffer());
    }

    private static void KillProcessTree(Process proc)
    {
        try
        {
            if (proc.HasExited) return;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill.exe",
                    Arguments = $"/T /F /PID {proc.Id}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                killer?.WaitForExit(3000);
            }
            else
            {
                // Linux: 杀死整个进程组
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "kill",
                    Arguments = $"-TERM -{proc.Id}",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                killer?.WaitForExit(2000);
            }

            if (!proc.HasExited) proc.Kill(true);
        }
        catch { }
    }

    private sealed record WorkerProcessContext(Process Process, int? BoundPort, DateTime StartedAt, long RunId);
}
