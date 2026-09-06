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

        return new SqlSugarScope(new ConnectionConfig
        {
            ConnectionString = connectionString,
            DbType = SqlSugar.DbType.Sqlite,
            IsAutoCloseConnection = true,
            InitKeyType = InitKeyType.Attribute,
        });
    }

    public static void Initialize(ISqlSugarClient db)
    {
        // InitTables 泛型重载最多 5 个类型参数，分两次注册。
        db.CodeFirst.InitTables<LinuxCommand, CommandGroup, ScheduleTask, ExecutionRecord, OperationLog>();
        db.CodeFirst.InitTables<SystemStatusSnapshot>();
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
