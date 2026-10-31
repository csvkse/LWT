namespace LinuxWebTool.Contracts.Features.Workers.Contracts;

/// <summary>
/// Worker 语言运行时类型。
/// </summary>
public enum WorkerRuntimeType
{
    Node,
    Python,
    DotNet,
    Bash,
    PowerShell,
    Batch
}

/// <summary>
/// Worker 运行形态。
/// </summary>
public enum WorkerServiceMode
{
    /// <summary>
    /// 有请求接口服务（对外监听端口或由 YARP 路径反向代理）。
    /// </summary>
    Service,

    /// <summary>
    /// 无请求后台长时常驻守护进程（后台轮询、任务消费、队列处理）。
    /// </summary>
    Daemon
}

/// <summary>
/// Worker 进程生命周期状态。
/// </summary>
public enum WorkerState
{
    Stopped,
    Starting,
    Running,
    Crashed,
    Backoff,
    Degraded
}

/// <summary>
/// Worker 网络与路由配置。
/// </summary>
public sealed record WorkerNetworkConfig
{
    /// <summary>
    /// 路由暴露模式："path" (推荐，通过 /w/{id}/* 路径代理) | "port" (直接监听外部端口)。
    /// </summary>
    public string RoutingMode { get; init; } = "path";

    /// <summary>
    /// 分配或指定的内部监听端口。若为空则启动时由系统端口池动态分配。
    /// </summary>
    public int? InternalPort { get; init; }

    /// <summary>
    /// 注入给子进程的端口环境变量名（默认 PORT）。
    /// </summary>
    public string PortEnv { get; init; } = "PORT";

    /// <summary>
    /// 路径转发前缀（如 /w/{id}）。
    /// </summary>
    public string? PathPrefix { get; init; }
}

/// <summary>
/// Worker 专属存储与沙盒配置。
/// </summary>
public sealed record WorkerStorageConfig
{
    /// <summary>
    /// 是否挂载专属独立 SQLite 数据库 (data.db)。
    /// </summary>
    public bool EnableSqlite { get; init; } = true;

    /// <summary>
    /// 是否启用专属独立 Key-Value 数据库 (kv.db)。
    /// </summary>
    public bool EnableKv { get; init; } = true;

    /// <summary>
    /// 独立 SQLite 数据库文件路径。
    /// </summary>
    public string? SqlitePath { get; init; }

    /// <summary>
    /// 独立 KV 数据库文件路径。
    /// </summary>
    public string? KvPath { get; init; }
}

/// <summary>
/// Worker 进程自愈与崩溃雪崩防护配置。
/// </summary>
public sealed record WorkerSupervisionConfig
{
    /// <summary>
    /// 异常崩溃时是否自动重启自愈。
    /// </summary>
    public bool AutoRestart { get; init; } = true;

    /// <summary>
    /// 瞬崩判定时间窗口（秒，默认 10 秒）。
    /// </summary>
    public int FlappingThresholdSec { get; init; } = 10;

    /// <summary>
    /// 触发 CrashLoop 熔断保护的最大连续崩溃重试次数（默认 5 次）。
    /// </summary>
    public int MaxCrashRetries { get; init; } = 5;
}

/// <summary>
/// Worker 服务定义清单 (worker.json)。
/// </summary>
public sealed record WorkerManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string? Description { get; init; }
    public string? Author { get; init; }
    public string Category { get; init; } = "Workers";
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// 运行形态：Service (有请求微服务) | Daemon (无请求后台守护)。
    /// </summary>
    public WorkerServiceMode Mode { get; init; } = WorkerServiceMode.Service;

    /// <summary>
    /// 语言运行时类型：Node | Python | DotNet | Bash | PowerShell | Batch。
    /// </summary>
    public WorkerRuntimeType Runtime { get; init; } = WorkerRuntimeType.Python;

    /// <summary>
    /// 入口文件名（如 worker.py, worker.js, Program.cs, worker.sh, worker.ps1）。
    /// </summary>
    public required string Entrypoint { get; init; }

    /// <summary>
    /// 网络路由配置。
    /// </summary>
    public WorkerNetworkConfig Network { get; init; } = new();

    /// <summary>
    /// 专属数据存储配置。
    /// </summary>
    public WorkerStorageConfig Storage { get; init; } = new();

    /// <summary>
    /// 进程自愈与守护配置。
    /// </summary>
    public WorkerSupervisionConfig Supervision { get; init; } = new();

    /// <summary>
    /// 环境变量自定义键值对。
    /// </summary>
    public IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// Worker 实例传输对象（用于前端页面渲染）。
/// </summary>
public sealed record WorkerInstanceDto
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string? Description { get; init; }
    public string? Author { get; init; }
    public WorkerServiceMode Mode { get; init; }
    public WorkerRuntimeType Runtime { get; init; }
    public string Entrypoint { get; init; } = "";
    public WorkerState State { get; init; }
    public int? ProcessId { get; init; }
    public int? BoundPort { get; init; }
    public string? AccessUrl { get; init; }
    public DateTime? StartedAt { get; init; }
    public long UptimeSeconds { get; init; }
    public int FastCrashCount { get; init; }
    public string? LastError { get; init; }
    public bool AutoRestart { get; init; }
    public bool EnableSqlite { get; init; }
    public bool EnableKv { get; init; }
    public long SqliteSizeBytes { get; init; }
    public long KvSizeBytes { get; init; }
    public string DataDirectory { get; init; } = "";
    public bool IsEnabled { get; init; } = true;
    public WorkerSourceMetadata? Source { get; init; }
    public IReadOnlyList<string> MissingDependencies { get; init; } = [];
}

