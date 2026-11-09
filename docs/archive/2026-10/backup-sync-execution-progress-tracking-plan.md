# 备份与同步任务实时执行进度（Execution Progress）调研与技术方案

## 1. 背景与用户需求深度分析

### 1.1 现状与痛点剖析
- **黑盒执行体验**：目前的备份与同步任务在点击“立即执行”或定时调度触发后，系统状态直接进入 `Running`（执行中）。当备份规模达数十万个文件、数十 GB 时，任务可能持续数分钟乃至数十分钟。在此期间，界面仅呈现单一转圈的“执行中...”状态，用户无法得知：
  - 当前处于哪个处理阶段（安全预检？文件扫描？数据传输？硬链比对？快照修剪？）
  - 已经处理了多少文件 / 总共有多少文件？当前百分比进度是多少？
  - 实时传输速度是多少（MB/s、文件/秒）？预估还需要多长时间完成（ETA）？
  - 当前卡在哪个具体的文件路径上？（便于排查大文件阻塞或异常文件）
- **长连接 HTTP 阻塞与超时断联隐患**：
  - 目前手动触发采用的是同步等待 HTTP 响应模式。若任务耗时超过 60 秒，反向代理（如 Nginx、FRP、网关）或浏览器极易触发 `504 Gateway Timeout`，虽然任务在后端仍可继续跑，但前端无法得知真实状态。
- **核心诉求**：提供**实时、细粒度、低开销的执行进度可视化**，并在任务运行期间动态呈现阶段、进度条、速率、ETA 与当前处理路径。

---

## 2. 头脑风暴：可行性、逻辑与性能隐患排查

### 2.1 性能与系统开销防御（最大挑战：高频小文件下的 I/O 放大）
- **痛点**：若目录包含 20 万个文件（例如 Node.js `node_modules` 或 Git 仓库），若每次处理一个文件都写一次数据库、触发一次锁或发起一次网络广播，进度的系统开销甚至会远超文件备份本身的开销！
- **防御机制**：
  1. **纯内存无锁原子化更新**：进度状态必须常驻内存（`ConcurrentDictionary`），文件数与字节数的累加必须采用原生 CPU 级别的 `Interlocked.Add` / `Volatile.Read`，避免任何互斥锁开销。
  2. **双频节流机制 (Two-Tier Throttling)**：
     - **极速热路径 (Hot Path)**：循环内部仅递增原子计数器与记录最后一条路径（极简操作，单次小于 10 纳秒）；
     - **低频采样计算 (Low-Frequency Sampling)**：仅在客户端拉取或每隔 250ms 计算一次瞬时速率（平滑移动平均值 EMA）与 ETA，杜绝无意义的高频运算。
  3. **零数据库持久化冲击**：中间流式进度绝不写入 SQLite 数据库，仅在任务开始和最终完成时写一次数据库执行记录。

### 2.2 逻辑挑战：总进度预估的“先有鸡还是先有蛋”
- **业界难题**：在未扫描完整个源目录前，无法得知 `TotalFiles` 和 `TotalBytes`；而若先全量扫描一遍，海量文件下用户需要在“扫描中”干等数十秒才开始复制。
- **推荐工业级混合预估逻辑 (Hybrid Estimation)**：
  - **第一阶段（源端扫描）**：进度显示为渐进式“已扫描发现 N 个文件，M 字节...”，若上一历史快照存在元数据，可作为基准参考线提供近似百分比；
  - **第二阶段（同步传输）**：基于实际扫描到的总数，计算精准百分比 `ProcessedFiles / TotalFiles * 100%`；
  - **第三阶段（修剪与完成）**：进度锁定在 99%~100%，显示“正在修剪过期快照与提交元数据”。

---

## 3. 业界主流方案调研与对比 (Industry Benchmarks)

| 系统 / 方案 | 进度数据流方案 | 阶段划分 | 速率与 ETA 计算方式 | 借鉴点 |
| :--- | :--- | :--- | :--- | :--- |
| **Rsync (`--info=progress2`)** | 管道输出流式传输 | 单阶段边扫边推 | 基于时间滑动窗口（Moving Window）计算即时字节与文件传输速率 | 速率平滑算法（避免小文件波动）；单行当前文件截断展示 |
| **Duplicati / Kopia** | 后端内存状态机 + REST 轮询 / WebSocket | `Scanning` -> `Transferring` -> `Verifying` -> `Cleaning` | 区分文件数进度与字节数进度双进度条；动态计算剩余时间 | 内存状态机管理；多阶段阶段指示器徽章 |
| **FreeFileSync** | GUI 观察者模式 (`IProgress`) | `Comparison` -> `Synchronization` -> `Verification` | 基于总文件数和已完成文件数提供线性百分比 | 友好的三阶段状态展示；可取消任务操作 |
| **LinuxWebTool 转码模块** | 内存任务对象 + 前端 2~5s 智能轮询 | `Queued` -> `Running` -> `Completed` | 基于 `ffmpeg -progress` 时间戳差值与视频时长 | 系统已有成熟的智能轮询架构，可无缝复用 |

---

## 4. 当前系统已有基础设施与复用评估

1. **统一协调器互斥量 (`BackupSyncCoordinator.TaskLocks`)**：
   - 现有的 `TaskLocks`（`ConcurrentDictionary<Guid, SemaphoreSlim>`）已经天然感知任意任务的运行状态。
2. **转码模块成熟的前端智能轮询机制 (`TranscodeView.js`)**：
   - 包含 `scheduleJobRefresh`、在途防抖（防止并发请求堆叠闪烁）与自适应停启轮询机制，前端可直接套用该成熟模式。
3. **Native AOT 零反射编译保障**：
   - 新增的进度 DTO 显式注入 `AppJsonSerializerContext.cs`，保持极致启动速度与内存开销。
4. **第三方库评估**：
   - 市场上的 `ShellProgressBar` 或 `Spectre.Console` 是面向终端控制台的，在 Web API 与 AOT 容器下不适用；
   - .NET 原生 `System.Threading.Interlocked` + `Stopwatch` + 观察者适配器能够以不到 200 行代码实现最高性能与最轻依赖。

---

## 5. 设计模式重构与架构落地

### 5.1 架构设计全景图
```
┌────────────────────────────────────────────────────────────────────────┐
│                      Web 前端 (BackupSyncView.js)                      │
│   - 检测到任务 Running 时启动 1.5s 智能防抖轮询                          │
│   - 展现：阶段徽章、双维度进度条、实时速率 (MB/s)、ETA 倒计时、当前文件路径 │
└───────────────────────────────────▲────────────────────────────────────┘
                                    │ GET /api/BackupSync/Tasks/{id}/Progress
┌───────────────────────────────────┴────────────────────────────────────┐
│              BackupSyncController (Minimal API 控制器)                 │
└───────────────────────────────────▲────────────────────────────────────┘
                                    │ 获取内存聚合快照
┌───────────────────────────────────┴────────────────────────────────────┐
│           BackupProgressTracker (单例 / 进度适配器与状态管理器)         │
│   - ConcurrentDictionary<Guid, TaskProgressContext> 内存常驻           │
│   - 原子无锁递增 (Interlocked)、平滑移动平均速率与 ETA 计算             │
└───────────────────────────────────▲────────────────────────────────────┘
                                    │ 驱动进度更新
┌───────────────────────────────────┴────────────────────────────────────┐
│            BackupSyncStateMachine (阶段状态机模式)                      │
│  - PreflightChecking ──> Scanning ──> Transferring ──> Cleaning/Prune  │
└───────────────────────────────────▲────────────────────────────────────┘
                                    │ 循环内部上报
        ┌───────────────────────────┴───────────────────────────┐
        ▼                                                       ▼
HardlinkSnapshotEngine                                  MirrorSyncEngine
(增量快照引擎)                                          (直接镜像同步引擎)
```

### 5.2 状态机模式 (State Machine Pattern) - 细粒度阶段流转
定义 `BackupExecutionPhase`：
1. `Idle` (0): 空闲就绪
2. `PreflightChecking` (1): 正在执行挂载状态、目录可达性与磁盘空间预检
3. `Scanning` (2): 正在扫描源端文件系统与匹配过滤规则
4. `Transferring` (3): 正在复制变动文件、创建符号链接与硬链接引用
5. `CleaningTarget` (4): 正在比对目标端多余文件并清理/移入回收站（仅同步模式）
6. `RetentionPruning` (5): 正在执行 GFS 快照轮转修剪与历史记录清理
7. `Completed` (6): 任务圆满完成
8. `Faulted` (7): 任务异常失败
9. `AutoPaused` (8): 连续失败触发断路器自动熔断

### 5.3 适配器模式 (Adapter Pattern) - 进度聚合适配器
- 定义接口：`IBackupProgressReporter`
  - `UpdatePhase(BackupExecutionPhase phase, string? detail = null)`
  - `SetTotalScanTarget(long totalFiles, long totalBytes)`
  - `ReportFileProgress(long bytesDelta, string currentFilePath, bool isSkipped = false)`
- 实现类：`ThrottledProgressReporter`
  - 内部记录热点计数字段：`long _processedFiles`、`long _processedBytes`、`long _skippedFiles`；
  - 维护一个微小环形缓冲队列计算最近 3 秒内的瞬时速率；
  - 采用无锁读取为 API 生成不可变不可修改的 `BackupProgressSnapshot`。

---

## 6. 数据契约与接口设计

### 6.1 进度数据模型 (`BackupProgressSnapshot`)
```csharp
public sealed record BackupProgressSnapshot
{
    public Guid TaskId { get; init; }
    public string TaskName { get; init; } = string.Empty;
    public int Mode { get; init; } // 1 = Sync, 2 = Backup
    public BackupExecutionPhase Phase { get; init; }
    public string PhaseDescription { get; init; } = string.Empty;
    public bool IsRunning { get; init; }
    public DateTime StartTime { get; init; }
    public long ElapsedMs { get; init; }

    // 文件维度统计
    public long ScannedFiles { get; init; }
    public long ProcessedFiles { get; init; }
    public long SkippedFiles { get; init; }
    public double FilePercent { get; init; } // 0.0 ~ 100.0%

    // 字节容量维度统计
    public long ScannedBytes { get; init; }
    public long ProcessedBytes { get; init; }
    public double BytePercent { get; init; } // 0.0 ~ 100.0%

    // 速率与预估
    public double SpeedBytesPerSec { get; init; } // 字节速率（格式化如 24.5 MB/s）
    public double SpeedFilesPerSec { get; init; } // 文件速率（格式化如 120 files/s）
    public long? EstimatedRemainingMs { get; init; } // 预估剩余毫秒数 (ETA)
    
    // 当前项追踪
    public string? CurrentItemPath { get; init; } // 当前正在传输处理的相对路径
}
```

### 6.2 REST 接口规范
- `GET /api/BackupSync/Tasks/{id:guid}/Progress`
  - 返回当前任务的实时进度快照；若任务未在运行，返回其最新一次执行的最终静态摘要。

### 6.3 前端呈现规范
- 在任务列表中，当任务处于 `Running` 状态时：
  1. 状态列显示动态脉冲徽章（如 `正在传输数据...`）；
  2. 展开实时进度条：展示双重信息（百分比、已处理文件数/总扫描数、传输容量）；
  3. 仪表指示器：显示即时速度 `⚡ 25.8 MB/s · 135 项/s` 与剩余预估时间 `⏳ 约 2分15秒`；
  4. 底部截断展示当前处理路径：`正在处理: /node_modules/.../index.js`。

---

## 7. 实施计划与步骤

1. **第一阶段：契约与内存进度适配器层**
   - 在 `BackupSyncContracts.cs` 中增加 `BackupExecutionPhase` 与 `BackupProgressSnapshot`；
   - 在 `Infrastructure/Features/BackupSync/Progress/` 中实现 `BackupProgressTracker` 与 `IBackupProgressReporter`；
   - 在 `AppJsonSerializerContext.cs` 中注册进度模型确保 Native AOT 兼容。
2. **第二阶段：状态机与核心引擎改造**
   - `BackupSyncStateMachine` 与 `BackupSyncCoordinator` 注入进度追踪器，全流程触发阶段转换；
   - `HardlinkSnapshotEngine` 与 `MirrorSyncEngine` 在扫描与复制文件循环中接入进度上报。
3. **第三阶段：控制器与前端界面集成**
   - `BackupSyncController.cs` 增加 `[HttpGet("Tasks/{id:guid}/Progress")]` 端点与 `EndpointsMapper.g.cs` 映射；
   - `BackupSyncView.js` 实现防抖智能轮询组件，呈现现代化进度条与速率面板。
4. **第四阶段：质量与架构门禁验证**
   - 编写高频并发压力测试验证热路径无锁性能；
   - 扩展架构门禁，确保进度接口零反射、零线程死锁。
