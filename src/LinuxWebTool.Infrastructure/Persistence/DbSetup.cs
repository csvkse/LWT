using SqlSugar;

namespace LinuxWebTool.Infrastructure.Persistence;

/// <summary>SqlSugar 客户端构建与 CodeFirst 建表。</summary>
public static class DbSetup
{
    public static ISqlSugarClient Create(string connectionString)
    {
        var directory = GetSqliteDirectory(connectionString);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // SQLite 并发：多后台服务（采样/调度/监听/转码）共用单例 db。
        // 关键：IsAutoCloseConnection 必须为 false。为 true 时，SqlSugar 在多个 async 操作
        // 挂起期间共享同一个 SqliteConnection，其中一个 Close() 会让其他挂起操作持有的
        // sqlite3_stmt 被释放，触发 ObjectDisposedException / NullReferenceException（容器已复现）。
        // 关闭自动关闭后，SqlSugar 每次操作独立打开/关闭连接（SqlSugarScope 内部 ManageContext
        // 会自动 Dispose 每次操作的连接上下文，不会泄漏），天然隔离并发。
        // Default Timeout=30 兜底多连接并发的写锁忙等，避免 database is locked。
        var concurrencySafe = EnsureSqliteOptions(connectionString);

        return new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = concurrencySafe,
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = false,
            InitKeyType = InitKeyType.Attribute,
        });
    }

    public static void Initialize(ISqlSugarClient db)
    {
        // InitTables 泛型重载最多 5 个类型参数，分多次注册。
        db.CodeFirst.InitTables<LinuxCommand, CommandGroup, ScheduleTask, ExecutionRecord, OperationLog>();
        db.CodeFirst.InitTables<SystemStatusSnapshot>();
        db.CodeFirst.InitTables<SystemStatusProcessSnapshot, SystemStatusDiskSnapshot, SystemStatusNetSnapshot>();
        db.CodeFirst.InitTables<SmbMount, TranscodePreset, TranscodeJob, WatchRule>();
    }

    /// <summary>为 SQLite 连接串追加 busy 等待参数：Default Timeout=30（写锁时忙等重试而非立即失败）。</summary>
    private static string EnsureSqliteOptions(string connectionString)
    {
        if (connectionString.Contains("Data Source", StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Default Timeout", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString.TrimEnd(';') + ";Default Timeout=30";
        }
        return connectionString;
    }

    /// <summary>把连接串里的相对 SQLite 文件路径锚定到应用根目录，避免受进程工作目录影响。</summary>
    public static string ResolveConnectionString(string connectionString, string basePath)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=', StringComparison.OrdinalIgnoreCase);
            if (index <= 0) continue;
            var key = part[..index].Trim();
            if (!key.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("DataSource", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = part[(index + 1)..].Trim();
            if (!Path.IsPathRooted(value))
            {
                var absolute = Path.GetFullPath(Path.Combine(basePath, value));
                return connectionString.Replace(part, $"{key}={absolute}", StringComparison.OrdinalIgnoreCase);
            }
        }

        return connectionString;
    }

    /// <summary>从 SQLite 连接串中解析出数据文件所在目录，便于启动时自动创建。</summary>
    private static string? GetSqliteDirectory(string connectionString)
    {
        foreach (var part in connectionString.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=', StringComparison.OrdinalIgnoreCase);
            if (index <= 0) continue;
            var key = part[..index].Trim();
            if (!key.Equals("Data Source", StringComparison.OrdinalIgnoreCase)
                && !key.Equals("DataSource", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = part[(index + 1)..].Trim();
            var separator = value.LastIndexOfAny(['/', '\\']);
            return separator > 0 ? value[..separator] : null;
        }

        return null;
    }
}
