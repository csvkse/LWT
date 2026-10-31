using LinuxWebTool.Contracts.Features.Workers.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Workers;

public sealed partial class WorkersManager
{
    public ValueTask<WorkerSqliteInfo> GetSqliteInfoAsync(string id, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        var dbPath = Path.Combine(dir, "data.db");
        if (!File.Exists(dbPath))
        {
            return ValueTask.FromResult(new WorkerSqliteInfo
            {
                Exists = false,
                SizeBytes = 0,
                AbsolutePath = dbPath,
                Tables = []
            });
        }

        var tables = new List<string>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询 Worker SQLite 元数据失败: {DbPath}", dbPath);
        }

        var info = new FileInfo(dbPath);
        return ValueTask.FromResult(new WorkerSqliteInfo
        {
            Exists = true,
            SizeBytes = info.Length,
            AbsolutePath = dbPath,
            Tables = tables
        });
    }

    public ValueTask<IReadOnlyList<WorkerKvItem>> GetKvListAsync(string id, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        var dbPath = Path.Combine(dir, "kv.db");
        EnsureKvTableCreated(dbPath);

        var list = new List<WorkerKvItem>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT k, v, expire_at, updated_at FROM kv_store ORDER BY k LIMIT 500;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new WorkerKvItem
                {
                    Key = reader.GetString(0),
                    Value = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    ExpireAtTimestamp = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    UpdatedAt = reader.IsDBNull(3) ? null : reader.GetString(3)
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 Worker KV 失败: {DbPath}", dbPath);
        }

        return ValueTask.FromResult<IReadOnlyList<WorkerKvItem>>(list);
    }

    public ValueTask<bool> SetKvAsync(string id, WorkerKvItem item, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        var dbPath = Path.Combine(dir, "kv.db");
        EnsureKvTableCreated(dbPath);

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO kv_store (k, v, expire_at, updated_at)
VALUES (@k, @v, @expire_at, @updated_at)
ON CONFLICT(k) DO UPDATE SET
    v = excluded.v,
    expire_at = excluded.expire_at,
    updated_at = excluded.updated_at;";

            cmd.Parameters.AddWithValue("@k", item.Key);
            cmd.Parameters.AddWithValue("@v", item.Value);
            cmd.Parameters.AddWithValue("@expire_at", (object?)item.ExpireAtTimestamp ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
            return ValueTask.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "写入 Worker KV 异常: {Key}", item.Key);
            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<bool> DeleteKvAsync(string id, string key, CancellationToken ct = default)
    {
        var dir = Path.Combine(_workersRoot, id);
        var dbPath = Path.Combine(dir, "kv.db");
        if (!File.Exists(dbPath)) return ValueTask.FromResult(false);

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM kv_store WHERE k = @k;";
            cmd.Parameters.AddWithValue("@k", key);
            var affected = cmd.ExecuteNonQuery();
            return ValueTask.FromResult(affected > 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除 Worker KV 异常: {Key}", key);
            return ValueTask.FromResult(false);
        }
    }

    private static void EnsureKvTableCreated(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS kv_store (
    k TEXT PRIMARY KEY,
    v TEXT,
    expire_at INTEGER,
    updated_at TEXT
);";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}
