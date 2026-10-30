using LinuxWebTool.Contracts.Features.BackupSync.Contracts;

namespace LinuxWebTool.Infrastructure.Features.BackupSync.Progress;

/// <summary>
/// 备份与同步执行进度上报观察者接口（适配器模式）
/// 提供极端 I/O 循环下的无锁、纳秒级原子化计数与阶段流转支持
/// </summary>
public interface IBackupProgressReporter
{
    /// <summary>当前被追踪的任务 ID</summary>
    Guid TaskId { get; }

    /// <summary>更新当前执行阶段与状态描述</summary>
    void UpdatePhase(BackupExecutionPhase phase, string? detail = null);

    /// <summary>设定扫描发现的目标总量基准（可随扫描过程动态扩展）</summary>
    void SetTotalScanTarget(long totalFiles, long totalBytes);

    /// <summary>上报单个文件的处理进度（热路径，必须零锁非阻塞）</summary>
    void ReportFileProcessed(long bytesDelta, string currentPath, bool isSkipped = false);

    /// <summary>上报目标端差异文件的清理或移入回收站数量</summary>
    void ReportItemsDeleted(long deletedFiles, long deletedBytes);

    /// <summary>生成当前进度的不可变快照（计算平滑速率与 ETA）</summary>
    BackupProgressSnapshot GetSnapshot();
}
