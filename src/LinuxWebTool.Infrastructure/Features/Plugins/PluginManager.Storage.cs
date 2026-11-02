using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager
{
    private string GetPluginVolumeDir(string id)
    {
        var pluginsParent = Path.GetDirectoryName(_pluginsRoot) ?? _pluginsRoot;
        var volumeRoot = Path.Combine(pluginsParent, "plugins_data", "volumes");
        var volumeDir = Path.Combine(volumeRoot, id);
        if (!Directory.Exists(volumeDir))
        {
            Directory.CreateDirectory(volumeDir);
        }
        return volumeDir;
    }

    private string ResolvePluginDbPath(string id, string dbName)
    {
        var volumeDir = GetPluginVolumeDir(id);
        var targetPath = Path.Combine(volumeDir, dbName);

        // 历史平滑自愈迁移：若持久卷中尚未创建，但代码根目录或旧 volumes 目录下有旧文件，自动拷贝
        if (!File.Exists(targetPath))
        {
            var pluginsParent = Path.GetDirectoryName(_pluginsRoot) ?? _pluginsRoot;
            var oldVolPath = Path.Combine(pluginsParent, "volumes", id, dbName);
            var legacyPath = Path.Combine(_pluginsRoot, id, dbName);
            var src = File.Exists(oldVolPath) ? oldVolPath : (File.Exists(legacyPath) ? legacyPath : null);
            if (src != null)
            {
                try
                {
                    File.Copy(src, targetPath, overwrite: false);
                    _logger?.LogInformation("成功将插件 '{Id}' 历史数据库 {DbName} 迁移至独立物理卷: {TargetPath}", id, dbName, targetPath);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "迁移历史数据库 {DbName} 失败，将使用现有持久卷路径", dbName);
                }
            }
        }

        return targetPath;
    }

    public ValueTask<PluginSqliteInfo> GetSqliteInfoAsync(string id, CancellationToken ct = default)
    {
        var dbPath = ResolvePluginDbPath(id, "data.db");
        if (!File.Exists(dbPath))
        {
            return ValueTask.FromResult(new PluginSqliteInfo
            {
                Exists = false,
                SizeBytes = 0,
                TableNames = []
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
            _logger?.LogWarning(ex, "查询插件 SQLite 元数据失败: {DbPath}", dbPath);
        }

        var info = new FileInfo(dbPath);
        return ValueTask.FromResult(new PluginSqliteInfo
        {
            Exists = true,
            SizeBytes = info.Length,
            TableNames = tables
        });
    }

    public ValueTask<IReadOnlyList<PluginKvItem>> GetKvListAsync(string id, CancellationToken ct = default)
    {
        var dbPath = ResolvePluginDbPath(id, "kv.db");
        EnsureKvTableCreated(dbPath);

        var list = new List<PluginKvItem>();
        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT k, v, updated_at FROM kv_store ORDER BY k LIMIT 500;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new PluginKvItem
                {
                    Key = reader.GetString(0),
                    Value = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取插件 KV 失败: {DbPath}", dbPath);
        }

        return ValueTask.FromResult<IReadOnlyList<PluginKvItem>>(list);
    }

    public ValueTask<PluginKvItem?> GetKvAsync(string id, string key, CancellationToken ct = default)
    {
        var dbPath = ResolvePluginDbPath(id, "kv.db");
        EnsureKvTableCreated(dbPath);

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT k, v, updated_at FROM kv_store WHERE k = @k LIMIT 1;";
            cmd.Parameters.AddWithValue("@k", key);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ValueTask.FromResult<PluginKvItem?>(new PluginKvItem
                {
                    Key = reader.GetString(0),
                    Value = reader.IsDBNull(1) ? "" : reader.GetString(1),
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "读取插件 KV 单项失败: {Key}", key);
        }

        return ValueTask.FromResult<PluginKvItem?>(null);
    }

    public ValueTask<bool> SetKvAsync(string id, PluginKvItem item, CancellationToken ct = default)
    {
        var dbPath = ResolvePluginDbPath(id, "kv.db");
        EnsureKvTableCreated(dbPath);

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO kv_store (k, v, updated_at)
VALUES (@k, @v, @updated_at)
ON CONFLICT(k) DO UPDATE SET
    v = excluded.v,
    updated_at = excluded.updated_at;";

            cmd.Parameters.AddWithValue("@k", item.Key);
            cmd.Parameters.AddWithValue("@v", item.Value);
            cmd.Parameters.AddWithValue("@updated_at", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
            cmd.ExecuteNonQuery();
            return ValueTask.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "写入插件 KV 异常: {Key}", item.Key);
            return ValueTask.FromResult(false);
        }
    }

    public ValueTask<bool> DeleteKvAsync(string id, string key, CancellationToken ct = default)
    {
        var dbPath = ResolvePluginDbPath(id, "kv.db");
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
            _logger?.LogError(ex, "删除插件 KV 异常: {Key}", key);
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
    updated_at TEXT
);";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }
}
