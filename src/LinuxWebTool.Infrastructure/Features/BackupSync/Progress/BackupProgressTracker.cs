using System.Collections.Concurrent;
using System.Diagnostics;
using LinuxWebTool.Contracts.Features.BackupSync.Contracts;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Progress;

/// <summary>
/// 备份与同步全局执行进度追踪协调器（单例）
/// 管理全部任务的实时内存状态机，提供高并发无锁热路径与平滑速率聚合
/// </summary>
public sealed class BackupProgressTracker
{
    private readonly ConcurrentDictionary<Guid, TaskProgressReporter> _activeReporters = new();
    private readonly ConcurrentDictionary<Guid, BackupProgressSnapshot> _latestSnapshots = new();

    /// <summary>
    /// 初始化并启动指定任务的进度追踪上下文
    /// </summary>
    public IBackupProgressReporter StartTracking(Guid taskId, string taskName, TaskMode mode)
    {
        var reporter = new TaskProgressReporter(taskId, taskName, (int)mode);
        _activeReporters[taskId] = reporter;
        _latestSnapshots[taskId] = reporter.GetSnapshot();
        return reporter;
    }

    /// <summary>
    /// 结束指定任务的进度追踪并归档最终快照
    /// </summary>
    public void CompleteTracking(Guid taskId, bool success, string? errorMessage = null)
    {
        if (_activeReporters.TryGetValue(taskId, out var reporter))
        {
            var finalPhase = success ? BackupExecutionPhase.Completed : BackupExecutionPhase.Faulted;
            var detail = success ? "任务圆满完成" : (errorMessage ?? "任务执行失败");
            reporter.UpdatePhase(finalPhase, detail);
            _latestSnapshots[taskId] = reporter.GetSnapshot();
        }
    }

    /// <summary>
    /// 获取指定任务的当前实时进度或最新完成快照
    /// </summary>
    public BackupProgressSnapshot? GetProgress(Guid taskId)
    {
        if (_activeReporters.TryGetValue(taskId, out var reporter))
        {
            var snap = reporter.GetSnapshot();
            _latestSnapshots[taskId] = snap;
            return snap;
        }

        return _latestSnapshots.TryGetValue(taskId, out var archived) ? archived : null;
    }

    /// <summary>
    /// 单个任务的进度上报适配器实现
    /// </summary>
    private sealed class TaskProgressReporter : IBackupProgressReporter
    {
        public Guid TaskId { get; }
        private readonly string _taskName;
        private readonly int _mode;
        private readonly DateTime _startTime;
        private readonly Stopwatch _stopwatch;

        private long _scannedFiles;
        private long _scannedBytes;
        private long _processedFiles;
        private long _processedBytes;
        private long _skippedFiles;
        private long _deletedFiles;
        private long _deletedBytes;

        private int _phase;
        private volatile string _phaseDescription = "准备中...";
        private volatile string? _currentItemPath;

        // 速率采样变量（仅在 GetSnapshot 低频调用时由锁保护）
        private readonly object _sampleSync = new();
        private long _lastSampleMs;
        private long _lastSampleBytes;
        private long _lastSampleFiles;
        private double _speedBytesPerSec;
        private double _speedFilesPerSec;

        public TaskProgressReporter(Guid taskId, string taskName, int mode)
        {
            TaskId = taskId;
            _taskName = taskName;
            _mode = mode;
            _startTime = DateTime.Now;
            _stopwatch = Stopwatch.StartNew();
            _phase = (int)BackupExecutionPhase.Idle;
        }

        public void UpdatePhase(BackupExecutionPhase phase, string? detail = null)
        {
            Interlocked.Exchange(ref _phase, (int)phase);
            _phaseDescription = detail ?? phase switch
            {
                BackupExecutionPhase.Idle => "就绪",
                BackupExecutionPhase.PreflightChecking => "正在执行安全预检与连通性验证...",
                BackupExecutionPhase.Scanning => "正在扫描源目录并计算比对差异...",
                BackupExecutionPhase.Transferring => "正在传输文件与建立快照...",
                BackupExecutionPhase.CleaningTarget => "正在比对并清理目标端差异文件...",
                BackupExecutionPhase.RetentionPruning => "正在执行快照保留策略修剪...",
                BackupExecutionPhase.Completed => "执行完成",
                BackupExecutionPhase.Faulted => "执行失败",
                BackupExecutionPhase.AutoPaused => "自动熔断暂停",
                _ => "执行中..."
            };
        }

        public void SetTotalScanTarget(long totalFiles, long totalBytes)
        {
            if (totalFiles > 0)
            {
                Interlocked.Exchange(ref _scannedFiles, totalFiles);
            }
            if (totalBytes > 0)
            {
                Interlocked.Exchange(ref _scannedBytes, totalBytes);
            }
        }

        public void ReportFileProcessed(long bytesDelta, string currentPath, bool isSkipped = false)
        {
            var newProcessed = Interlocked.Increment(ref _processedFiles);
            if (bytesDelta > 0)
            {
                Interlocked.Add(ref _processedBytes, bytesDelta);
            }
            if (isSkipped)
            {
                Interlocked.Increment(ref _skippedFiles);
            }

            // 自适应扩容已扫描总量：如果处理量超过原预估扫描量，动态跟进
            var currentScanned = Volatile.Read(ref _scannedFiles);
            if (newProcessed > currentScanned)
            {
                Interlocked.Exchange(ref _scannedFiles, newProcessed);
            }

            _currentItemPath = currentPath;
        }

        public void ReportItemsDeleted(long deletedFiles, long deletedBytes)
        {
            if (deletedFiles > 0)
            {
                Interlocked.Add(ref _deletedFiles, deletedFiles);
            }
            if (deletedBytes > 0)
            {
                Interlocked.Add(ref _deletedBytes, deletedBytes);
            }
        }

        public BackupProgressSnapshot GetSnapshot()
        {
            var elapsedMs = _stopwatch.ElapsedMilliseconds;
            var processedBytes = Volatile.Read(ref _processedBytes);
            var processedFiles = Volatile.Read(ref _processedFiles);
            var scannedBytes = Volatile.Read(ref _scannedBytes);
            var scannedFiles = Volatile.Read(ref _scannedFiles);
            var skippedFiles = Volatile.Read(ref _skippedFiles);
            var currentPhase = (BackupExecutionPhase)Volatile.Read(ref _phase);
            var isRunning = currentPhase is not (BackupExecutionPhase.Completed or BackupExecutionPhase.Faulted or BackupExecutionPhase.AutoPaused or BackupExecutionPhase.Idle);

            lock (_sampleSync)
            {
                var deltaMs = elapsedMs - _lastSampleMs;
                if (deltaMs >= 400)
                {
                    var deltaBytes = processedBytes - _lastSampleBytes;
                    var deltaFiles = processedFiles - _lastSampleFiles;
                    var deltaSec = Math.Max(0.1, deltaMs / 1000.0);

                    var instantSpeedBytes = Math.Max(0.0, deltaBytes / deltaSec);
                    var instantSpeedFiles = Math.Max(0.0, deltaFiles / deltaSec);

                    // 指数加权移动平均 (EMA 平滑处理)
                    _speedBytesPerSec = _speedBytesPerSec <= 0 ? instantSpeedBytes : (0.45 * instantSpeedBytes + 0.55 * _speedBytesPerSec);
                    _speedFilesPerSec = _speedFilesFilesPerSec(_speedFilesPerSec, instantSpeedFiles);

                    _lastSampleMs = elapsedMs;
                    _lastSampleBytes = processedBytes;
                    _lastSampleFiles = processedFiles;
                }
            }

            // 预估剩余时间 (ETA)
            long? etaMs = null;
            if (isRunning)
            {
                if (scannedBytes > processedBytes && _speedBytesPerSec > 1024)
                {
                    var remainingBytes = scannedBytes - processedBytes;
                    etaMs = (long)Math.Max(0, remainingBytes / _speedBytesPerSec * 1000.0);
                }
                else if (scannedFiles > processedFiles && _speedFilesPerSec > 0.5)
                {
                    var remainingFiles = scannedFiles - processedFiles;
                    etaMs = (long)Math.Max(0, remainingFiles / _speedFilesPerSec * 1000.0);
                }
            }

            var filePercent = scannedFiles > 0
                ? Math.Clamp(Math.Round((double)processedFiles / scannedFiles * 100.0, 1), 0.0, 100.0)
                : (currentPhase == BackupExecutionPhase.Completed ? 100.0 : 0.0);

            var bytePercent = scannedBytes > 0
                ? Math.Clamp(Math.Round((double)processedBytes / scannedBytes * 100.0, 1), 0.0, 100.0)
                : (currentPhase == BackupExecutionPhase.Completed ? 100.0 : 0.0);

            return new BackupProgressSnapshot
            {
                TaskId = TaskId,
                TaskName = _taskName,
                Mode = _mode,
                Phase = currentPhase,
                PhaseDescription = _phaseDescription,
                IsRunning = isRunning,
                StartTime = _startTime,
                ElapsedMs = elapsedMs,
                ScannedFiles = scannedFiles,
                ProcessedFiles = processedFiles,
                SkippedFiles = skippedFiles,
                FilePercent = filePercent,
                ScannedBytes = scannedBytes,
                ProcessedBytes = processedBytes,
                BytePercent = bytePercent,
                SpeedBytesPerSec = Math.Round(_speedBytesPerSec, 1),
                SpeedFilesPerSec = Math.Round(_speedFilesPerSec, 1),
                EstimatedRemainingMs = etaMs,
                CurrentItemPath = _currentItemPath
            };
        }

        private static double _speedFilesFilesPerSec(double current, double instant)
        {
            return current <= 0 ? instant : (0.45 * instant + 0.55 * current);
        }
    }
}
