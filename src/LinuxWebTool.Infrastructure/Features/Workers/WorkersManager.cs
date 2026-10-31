using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Workers.Contracts;
using LinuxWebTool.Infrastructure.Features.Workers.Adapters;
using LinuxWebTool.Infrastructure.Features.Workers.Supervisor;
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
    private readonly IReadOnlyDictionary<WorkerRuntimeType, IWorkerRuntimeAdapter> _adapters;
    private readonly ILogger<WorkersManager> _logger;
    private CancellationTokenSource? _stoppingCts;

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
            [WorkerRuntimeType.Batch] = new BatchRuntimeAdapter()
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
        var dir = Path.Combine(_workersRoot, id);
        var manifestFile = Path.Combine(dir, "worker.json");
        if (!File.Exists(manifestFile)) return false;

        var json = await File.ReadAllTextAsync(manifestFile, ct);
        var manifest = JsonSerializer.Deserialize(json, WorkerJsonContext.Default.WorkerManifest);
        if (manifest == null) return false;

        var sm = _stateMachines.GetOrAdd(id, _ => new WorkerStateMachine(id, manifest.Supervision));
        sm.Reset();
        return StartProcessInternal(manifest, dir, sm);
    }

    public ValueTask<bool> StopAsync(string id, CancellationToken ct = default)
    {
        if (_activeProcesses.TryRemove(id, out var ctx))
        {
            try
            {
                KillProcessTree(ctx.Process);
            }
            catch { }
        }

        _portManager.ReleasePort(id);
        if (_stateMachines.TryGetValue(id, out var sm))
        {
            sm.NotifyStopped();
        }

        var logBuffer = GetLogBuffer(id);
        logBuffer.AppendLine("进程已被用户手动停止。");
        return ValueTask.FromResult(true);
    }

    public async ValueTask<bool> RestartAsync(string id, CancellationToken ct = default)
    {
        await StopAsync(id, ct);
        await Task.Delay(500, ct);
        return await StartAsync(id, ct);
    }

    public async ValueTask<bool> DeleteAsync(string id, CancellationToken ct = default)
    {
        await StopAsync(id, ct);
        _stateMachines.TryRemove(id, out _);
        _logBuffers.TryRemove(id, out _);

        var dir = Path.Combine(_workersRoot, id);
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
            return true;
        }
        return false;
    }

    public async ValueTask<WorkerInstanceDto> CreateOrUpdateAsync(WorkerCreateOrUpdateRequest request, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, request.Id);
        Directory.CreateDirectory(dir);

        var manifest = new WorkerManifest
        {
            Id = request.Id,
            Name = request.Name,
            Description = request.Description,
            Mode = request.Mode,
            Runtime = request.Runtime,
            Entrypoint = request.Entrypoint,
            Supervision = new WorkerSupervisionConfig { AutoRestart = request.AutoRestart },
            Storage = new WorkerStorageConfig { EnableSqlite = request.EnableSqlite, EnableKv = request.EnableKv },
            EnvironmentVariables = request.EnvironmentVariables ?? new Dictionary<string, string>()
        };

        var manifestFile = Path.Combine(dir, "worker.json");
        await File.WriteAllTextAsync(manifestFile, JsonSerializer.Serialize(manifest, WorkerJsonContext.Default.WorkerManifest), ct);

        if (!string.IsNullOrWhiteSpace(request.ScriptContent))
        {
            var entryFile = Path.Combine(dir, request.Entrypoint);
            await File.WriteAllTextAsync(entryFile, request.ScriptContent, ct);
        }

        return BuildDto(manifest, dir);
    }

    public ValueTask<IReadOnlyList<string>> GetLogsAsync(string id, int limit = 200, CancellationToken ct = default)
    {
        var buffer = GetLogBuffer(id);
        return ValueTask.FromResult(buffer.GetRecentLines(limit));
    }

    private bool StartProcessInternal(WorkerManifest manifest, string dir, WorkerStateMachine sm)
    {
        if (_activeProcesses.TryGetValue(manifest.Id, out var existing))
        {
            if (!existing.Process.HasExited) return true;
            _activeProcesses.TryRemove(manifest.Id, out _);
        }

        if (!_adapters.TryGetValue(manifest.Runtime, out var adapter))
        {
            sm.RecordExit(-1, $"未支持的运行时: {manifest.Runtime}");
            return false;
        }

        if (!adapter.CheckAvailable(out var missingReason))
        {
            sm.RecordExit(-1, missingReason);
            GetLogBuffer(manifest.Id).AppendLine($"[ERROR] 环境检查失败: {missingReason}");
            return false;
        }

        int? port = null;
        if (manifest.Mode == WorkerServiceMode.Service)
        {
            port = _portManager.AllocatePort(manifest.Id, manifest.Network.InternalPort);
        }

        var spec = adapter.BuildStartSpec(manifest, dir, port);
        var logBuffer = GetLogBuffer(manifest.Id);

        try
        {
            sm.NotifyStarting();
            var startInfo = new ProcessStartInfo
            {
                FileName = spec.FileName,
                Arguments = spec.Arguments,
                WorkingDirectory = spec.WorkingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var (k, v) in spec.EnvironmentVariables)
            {
                startInfo.EnvironmentVariables[k] = v;
            }

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
                HandleProcessExit(manifest, dir, sm, proc.ExitCode);
            };

            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _activeProcesses[manifest.Id] = new WorkerProcessContext(proc, port, DateTime.UtcNow);
            logBuffer.AppendLine($"Worker 进程启动成功 (PID: {proc.Id}{(port.HasValue ? $", PORT: {port}" : "")})");

            // 10秒后若依然存活，判定为健康运行 Running
            _ = Task.Run(async () =>
            {
                await Task.Delay(10000);
                if (_activeProcesses.TryGetValue(manifest.Id, out var cur) && !cur.Process.HasExited)
                {
                    sm.NotifyRunning();
                }
            });

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 Worker 失败: {Id}", manifest.Id);
            sm.RecordExit(-1, ex.Message);
            logBuffer.AppendLine($"[FATAL] 启动异常: {ex.Message}");
            return false;
        }
    }

    private void HandleProcessExit(WorkerManifest manifest, string dir, WorkerStateMachine sm, int exitCode)
    {
        _activeProcesses.TryRemove(manifest.Id, out _);
        _portManager.ReleasePort(manifest.Id);

        var logBuffer = GetLogBuffer(manifest.Id);
        logBuffer.AppendLine($"Worker 进程已退出，ExitCode: {exitCode}");

        if (sm.State == WorkerState.Stopped) return; // 用户主动停止

        var (shouldRestart, delay, nextState) = sm.RecordExit(exitCode);
        if (!manifest.Supervision.AutoRestart || !shouldRestart)
        {
            logBuffer.AppendLine(sm.LastError ?? "进程退出且不再自动重启。");
            return;
        }

        logBuffer.AppendLine($"触发自动自愈，将在 {delay.TotalSeconds} 秒后重新拉起进程 (重试计数: {sm.FastCrashCount})...");
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, _stoppingCts?.Token ?? CancellationToken.None);
                StartProcessInternal(manifest, dir, sm);
            }
            catch { }
        });
    }

    private WorkerInstanceDto BuildDto(WorkerManifest manifest, string dir)
    {
        _activeProcesses.TryGetValue(manifest.Id, out var ctx);
        var isAlive = ctx != null && !ctx.Process.HasExited;

        var sm = _stateMachines.GetOrAdd(manifest.Id, _ => new WorkerStateMachine(manifest.Id, manifest.Supervision));
        var state = isAlive ? (sm.State == WorkerState.Starting ? WorkerState.Starting : WorkerState.Running) : sm.State;

        int? boundPort = ctx?.BoundPort ?? _portManager.GetPort(manifest.Id);
        string? accessUrl = manifest.Mode == WorkerServiceMode.Service ? $"/w/{manifest.Id}/" : null;

        var sqlitePath = Path.Combine(dir, "data.db");
        var kvPath = Path.Combine(dir, "kv.db");

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
            State = state,
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
            DataDirectory = dir
        };
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

    private sealed record WorkerProcessContext(Process Process, int? BoundPort, DateTime StartedAt);
}
