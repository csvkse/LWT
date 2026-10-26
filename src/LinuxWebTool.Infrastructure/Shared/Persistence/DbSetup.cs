using System.IO;
using Microsoft.Data.Sqlite;
using Dapper;
namespace LinuxWebTool.Infrastructure.Shared.Persistence;

public class DbConnectionFactory(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public SqliteConnection CreateConnection()
    {
        return new SqliteConnection(ConnectionString);
    }
}

public static class DbSetup
{
    public static DbConnectionFactory CreateFactory(string connectionString)
    {
        var directory = GetSqliteDirectory(connectionString);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var concurrencySafe = EnsureSqliteOptions(connectionString);
        return new DbConnectionFactory(concurrencySafe);
    }

    public static void Initialize(DbConnectionFactory factory)
    {
        using var db = factory.CreateConnection();
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS command_group (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT,
  BizType INTEGER,
  SortOrder INTEGER,
  CreateTime TEXT
);
CREATE TABLE IF NOT EXISTS execution_record (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Source INTEGER,
  CommandId TEXT COLLATE NOCASE,
  ScheduleTaskId TEXT COLLATE NOCASE,
  CommandName TEXT,
  CommandText TEXT,
  Status INTEGER,
  ExitCode INTEGER,
  Output TEXT,
  ErrorOutput TEXT,
  DurationMs INTEGER,
  TimedOut INTEGER,
  Truncated INTEGER,
  TriggerBy TEXT,
  StartTime TEXT,
  EndTime TEXT
);
CREATE TABLE IF NOT EXISTS linux_command (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT,
  CommandText TEXT,
  ScriptType INTEGER,
  Description TEXT,
  GroupId TEXT COLLATE NOCASE,
  IsPinned INTEGER,
  SortOrder INTEGER,
  TimeoutSeconds INTEGER,
  LastExecTime TEXT,
  CreateTime TEXT,
  UpdateTime TEXT
);
CREATE TABLE IF NOT EXISTS operation_log (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Time TEXT,
  Action TEXT,
  TargetType TEXT,
  TargetName TEXT,
  Detail TEXT,
  ClientIp TEXT,
  Success INTEGER
);
CREATE TABLE IF NOT EXISTS schedule_task (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT,
  CommandId TEXT COLLATE NOCASE,
  CronExpression TEXT,
  Enabled INTEGER,
  GroupId TEXT COLLATE NOCASE,
  IsPinned INTEGER,
  SortOrder INTEGER,
  TimeoutSeconds INTEGER,
  Arguments TEXT,
  LastRunTime TEXT,
  NextRunTime TEXT,
  CreateTime TEXT,
  UpdateTime TEXT
);
CREATE TABLE IF NOT EXISTS smb_mount (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT,
  Server TEXT,
  LocalPath TEXT,
  Username TEXT,
  Password TEXT,
  Domain TEXT,
  Options TEXT,
  AutoMount INTEGER,
  Enabled INTEGER,
  Description TEXT,
  CreateTime TEXT,
  UpdateTime TEXT
);
CREATE TABLE IF NOT EXISTS webdav_mount (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT,
  Url TEXT,
  LocalPath TEXT,
  Username TEXT,
  Password TEXT,
  AutoMount INTEGER,
  Enabled INTEGER,
  Description TEXT,
  CreateTime TEXT,
  UpdateTime TEXT
);
CREATE TABLE IF NOT EXISTS rclone_mount (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT,
  Kind TEXT,
  LocalPath TEXT,
  RemotePath TEXT,
  Host TEXT,
  Port INTEGER,
  Username TEXT,
  Password TEXT,
  KeyFile TEXT,
  HostKey TEXT,
  Endpoint TEXT,
  Bucket TEXT,
  Region TEXT,
  AccessKeyId TEXT,
  SecretAccessKey TEXT,
  AutoMount INTEGER,
  Enabled INTEGER,
  Description TEXT,
  CreateTime TEXT,
  UpdateTime TEXT
);
CREATE TABLE IF NOT EXISTS system_status_disk_snapshot (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Time TEXT,
  Mount TEXT,
  FileSystem TEXT,
  UsagePercent REAL,
  TotalBytes INTEGER,
  UsedBytes INTEGER,
  FreeBytes INTEGER
);
CREATE TABLE IF NOT EXISTS system_status_net_snapshot (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Time TEXT,
  Name TEXT,
  SentBytesPerSec INTEGER,
  RecvBytesPerSec INTEGER
);
CREATE TABLE IF NOT EXISTS system_status_process_snapshot (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Time TEXT,
  Pid INTEGER,
  Name TEXT,
  CpuPercent REAL,
  MemPercent REAL,
  MemBytes INTEGER,
  DiskReadBps INTEGER,
  DiskWriteBps INTEGER,
  NetSentBps INTEGER,
  NetRecvBps INTEGER
);
CREATE TABLE IF NOT EXISTS system_status_snapshot (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Time TEXT,
  CpuUsage REAL,
  Load1 REAL,
  MemUsage REAL,
  DiskRootUsage REAL,
  NetSentBps INTEGER,
  NetRecvBps INTEGER
);
CREATE TABLE IF NOT EXISTS transcode_job (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  SourcePath TEXT,
  OutputPath TEXT,
  PresetId TEXT COLLATE NOCASE,
  PresetName TEXT,
  CustomArgs TEXT,
  IsFullCommand INTEGER,
  UseHardwareAccel INTEGER,
  HardwareBackend TEXT,
  UsedHardwareAccel INTEGER,
  CommandLine TEXT,
  FallbackReason TEXT,
  FallbackFromCommand TEXT,
  OutputDir TEXT,
  OutputContainer TEXT,
  OutputMode INTEGER,
  Trigger INTEGER,
  WatchRuleId TEXT COLLATE NOCASE,
  Status INTEGER,
  Progress REAL,
  SpeedText TEXT,
  DurationMs INTEGER,
  ErrorOutput TEXT,
  LogFile TEXT,
  SourceSizeBytes INTEGER,
  OutputSizeBytes INTEGER,
  QueueTime TEXT,
  StartTime TEXT,
  EndTime TEXT,
  CreateTime TEXT,
  UpdateTime TEXT
);
CREATE TABLE IF NOT EXISTS transcode_preset (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT,
  Container TEXT,
  VideoCodec TEXT,
  VideoQuality INTEGER,
  AudioCodec TEXT,
  AudioBitrate TEXT,
  ExtraArgs TEXT,
  Description TEXT,
  IsBuiltin INTEGER,
  CreateTime TEXT,
  UpdateTime TEXT
);
CREATE TABLE IF NOT EXISTS watch_rule (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT,
  WatchPath TEXT,
  FilePatterns TEXT,
  PresetId TEXT COLLATE NOCASE,
  OutputMode INTEGER,
  Recursive INTEGER,
  Mode INTEGER,
  PollSeconds INTEGER,
  Enabled INTEGER,
  UseHardwareAccel INTEGER,
  HardwareBackend TEXT,
  LastScanTime TEXT,
  CreateTime TEXT,
  UpdateTime TEXT
);
CREATE TABLE IF NOT EXISTS api_key (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT NOT NULL,
  KeyPrefix TEXT NOT NULL,
  KeyHash TEXT NOT NULL UNIQUE,
  IsEnabled INTEGER NOT NULL DEFAULT 1,
  AllowApi INTEGER NOT NULL DEFAULT 1,
  AllowMcp INTEGER NOT NULL DEFAULT 1,
  AllowTerminal INTEGER NOT NULL DEFAULT 0,
  AllowSchedules INTEGER NOT NULL DEFAULT 0,
  AllowFiles INTEGER NOT NULL DEFAULT 0,
  AllowTranscode INTEGER NOT NULL DEFAULT 0,
  AllowGateway INTEGER NOT NULL DEFAULT 0,
  AllowedPaths TEXT,
  AllowRemoteHosts INTEGER NOT NULL DEFAULT 0,
  AllowedHostIds TEXT,
  CreatedAt TEXT NOT NULL,
  LastUsedAt TEXT,
  ExpiresAt TEXT
);
CREATE INDEX IF NOT EXISTS idx_api_key_hash ON api_key(KeyHash);
CREATE TABLE IF NOT EXISTS remote_host (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT NOT NULL,
  Host TEXT NOT NULL,
  Port INTEGER NOT NULL DEFAULT 22,
  Protocol TEXT NOT NULL DEFAULT 'ssh',
  AuthType TEXT NOT NULL DEFAULT 'Password',
  Username TEXT NOT NULL,
  EncryptedCredential TEXT,
  CredentialNonce TEXT,
  CredentialTag TEXT,
  HostKeyFingerprint TEXT,
  Tags TEXT,
  Remark TEXT,
  SortOrder INTEGER NOT NULL DEFAULT 0,
  CreatedAt TEXT NOT NULL,
  UpdatedAt TEXT NOT NULL,
  LastConnectedAt TEXT
);
CREATE INDEX IF NOT EXISTS idx_remote_host_sort ON remote_host(SortOrder, CreatedAt);
CREATE TABLE IF NOT EXISTS frp_tunnel_config (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  ServerUrl TEXT NOT NULL,
  TunnelHost TEXT NOT NULL,
  ApiKey TEXT NOT NULL,
  LocalTargetUrl TEXT NOT NULL DEFAULT 'http://127.0.0.1:8080',
  AutoStart INTEGER NOT NULL DEFAULT 0,
  HeartbeatIntervalSeconds INTEGER NOT NULL DEFAULT 15,
  Status INTEGER NOT NULL DEFAULT 0,
  LastConnectedAt TEXT,
  LastDisconnectReason TEXT,
  UpdateTime TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS frp_tunnel_lines (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT NOT NULL,
  ServerUrl TEXT NOT NULL,
  BackupServerUrls TEXT,
  TunnelHost TEXT NOT NULL UNIQUE,
  ApiKey TEXT,
  LocalTargetUrl TEXT NOT NULL DEFAULT 'http://127.0.0.1:8080',
  AutoStart INTEGER NOT NULL DEFAULT 1,
  HeartbeatIntervalSeconds INTEGER NOT NULL DEFAULT 15,
  EnableLan302Proxy INTEGER NOT NULL DEFAULT 1,
  ProxyType TEXT NOT NULL DEFAULT 'Direct',
  ProxyUrl TEXT,
  ProxyBypass TEXT,
  Status TEXT NOT NULL DEFAULT 'Disconnected',
  SortOrder INTEGER NOT NULL DEFAULT 0,
  CreateTime TEXT NOT NULL,
  UpdateTime TEXT NOT NULL
);
INSERT OR IGNORE INTO frp_tunnel_lines (Id, Name, ServerUrl, BackupServerUrls, TunnelHost, ApiKey, LocalTargetUrl, AutoStart, HeartbeatIntervalSeconds, EnableLan302Proxy, ProxyType, ProxyUrl, ProxyBypass, Status, SortOrder, CreateTime, UpdateTime)
SELECT Id, '默认穿透线路', ServerUrl, '', TunnelHost, ApiKey, LocalTargetUrl, AutoStart, HeartbeatIntervalSeconds, 1, 'Direct', '', '', 'Disconnected', 0, UpdateTime, UpdateTime
FROM frp_tunnel_config
WHERE (SELECT COUNT(*) FROM frp_tunnel_lines) = 0;
CREATE INDEX IF NOT EXISTS idx_frp_tunnel_host ON frp_tunnel_lines(TunnelHost);
CREATE TABLE IF NOT EXISTS gateway_route (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  RouteId TEXT NOT NULL UNIQUE,
  ClusterId TEXT NOT NULL,
  MatchPath TEXT NOT NULL,
  MatchHosts TEXT,
  Transforms TEXT,
  Metadata TEXT,
  OrderNum INTEGER NOT NULL DEFAULT 0,
  IsEnabled INTEGER NOT NULL DEFAULT 1,
  UpdateTime TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS gateway_cluster (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  ClusterId TEXT NOT NULL UNIQUE,
  LoadBalancingPolicy TEXT NOT NULL DEFAULT 'RoundRobin',
  Destinations TEXT NOT NULL,
  HealthCheckConfig TEXT,
  UpdateTime TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS gateway_website (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT NOT NULL,
  TargetUrl TEXT NOT NULL UNIQUE,
  RewriteBody INTEGER NOT NULL DEFAULT 1,
  RewriteCookie INTEGER NOT NULL DEFAULT 1,
  IsEnabled INTEGER NOT NULL DEFAULT 1,
  UpdateTime TEXT NOT NULL,
  Alias TEXT,
  AuthType TEXT NOT NULL DEFAULT 'None',
  AuthSecret TEXT
);
CREATE TABLE IF NOT EXISTS gateway_tcp_route (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT NOT NULL,
  Protocol TEXT NOT NULL DEFAULT 'TCP',
  ListenPort INTEGER NOT NULL UNIQUE,
  ForwardHost TEXT NOT NULL,
  ForwardPort INTEGER NOT NULL,
  IsEnabled INTEGER NOT NULL DEFAULT 1,
  UpdateTime TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS easytier_nodes (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  InstanceName TEXT NOT NULL UNIQUE,
  NetworkName TEXT NOT NULL,
  NetworkSecret TEXT NOT NULL,
  VirtualIpv4 TEXT,
  EnableDhcp INTEGER NOT NULL DEFAULT 1,
  ListenersJson TEXT NOT NULL DEFAULT '[]',
  PeersJson TEXT NOT NULL DEFAULT '[]',
  ProxyNetworksJson TEXT NOT NULL DEFAULT '[]',
  RoutesJson TEXT NOT NULL DEFAULT '[]',
  RawTomlOverride TEXT,
  AutoStart INTEGER NOT NULL DEFAULT 1,
  Status INTEGER NOT NULL DEFAULT 0,
  LastError TEXT,
  UpdateTime TEXT NOT NULL,
  RoutingMode TEXT NOT NULL DEFAULT 'auto',
  Hostname TEXT,
  VirtualIpv6 TEXT,
  MappedListenersJson TEXT NOT NULL DEFAULT '[]',
  ProxyNetworkRulesJson TEXT NOT NULL DEFAULT '[]',
  AdvancedConfigJson TEXT NOT NULL DEFAULT '{}'
);
CREATE INDEX IF NOT EXISTS idx_easytier_instance_name ON easytier_nodes(InstanceName);
CREATE TABLE IF NOT EXISTS backup_sync_task (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT NOT NULL,
  Mode INTEGER NOT NULL,
  SourcePath TEXT NOT NULL,
  TargetPath TEXT NOT NULL,
  Enabled INTEGER NOT NULL DEFAULT 1,
  CronExpression TEXT NOT NULL,
  CronDescription TEXT,
  SyncDirection INTEGER NOT NULL DEFAULT 1,
  DeleteHandling INTEGER NOT NULL DEFAULT 2,
  TrashRetentionDays INTEGER NOT NULL DEFAULT 30,
  KeepLastCount INTEGER NOT NULL DEFAULT 5,
  KeepDays INTEGER NOT NULL DEFAULT 7,
  KeepWeeks INTEGER NOT NULL DEFAULT 4,
  KeepMonths INTEGER NOT NULL DEFAULT 12,
  ExcludePatterns TEXT,
  LastRunTime TEXT,
  LastRunStatus TEXT,
  LastRunMessage TEXT,
  NextRunTime TEXT,
  ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
  AutoPausedReason TEXT,
  CreateTime TEXT NOT NULL,
  UpdateTime TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS backup_snapshot (
  SnapshotId TEXT NOT NULL COLLATE NOCASE,
  TaskId TEXT NOT NULL COLLATE NOCASE,
  CreatedAt TEXT NOT NULL,
  TotalFiles INTEGER NOT NULL DEFAULT 0,
  TotalSizeBytes INTEGER NOT NULL DEFAULT 0,
  SnapshotPath TEXT NOT NULL,
  Tag TEXT NOT NULL DEFAULT '',
  IsPinned INTEGER NOT NULL DEFAULT 0,
  Note TEXT,
  PRIMARY KEY (TaskId, SnapshotId)
);
CREATE INDEX IF NOT EXISTS idx_backup_snapshot_task ON backup_snapshot(TaskId);
";
        cmd.ExecuteNonQuery();

        EnsureColumnExists(db, "easytier_nodes", "RoutingMode", "TEXT NOT NULL DEFAULT 'auto'");
        EnsureColumnExists(db, "easytier_nodes", "Hostname", "TEXT");
        EnsureColumnExists(db, "easytier_nodes", "VirtualIpv6", "TEXT");
        EnsureColumnExists(db, "easytier_nodes", "MappedListenersJson", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumnExists(db, "easytier_nodes", "ProxyNetworkRulesJson", "TEXT NOT NULL DEFAULT '[]'");
        EnsureColumnExists(db, "easytier_nodes", "AdvancedConfigJson", "TEXT NOT NULL DEFAULT '{}'");
        EnsureColumnExists(db, "gateway_website", "Alias", "TEXT");
        EnsureColumnExists(db, "gateway_website", "AuthType", "TEXT NOT NULL DEFAULT 'None'");
        EnsureColumnExists(db, "gateway_website", "AuthSecret", "TEXT");
        EnsureColumnExists(db, "api_key", "AllowedPaths", "TEXT");
        EnsureColumnExists(db, "api_key", "AllowRemoteHosts", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "api_key", "AllowedHostIds", "TEXT");
        EnsureColumnExists(db, "backup_sync_task", "ConsecutiveFailures", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "backup_sync_task", "AutoPausedReason", "TEXT");
    }

    private static void EnsureColumnExists(SqliteConnection db, string table, string column, string definition)
    {
        using var checkCmd = db.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = checkCmd.ExecuteReader();
        while (reader.Read())
        {
            var colName = reader.GetString(1);
            if (colName.Equals(column, StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();
        using var alterCmd = db.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
        alterCmd.ExecuteNonQuery();
    }

    private static string EnsureSqliteOptions(string connectionString)
    {
        if (connectionString.Contains("Data Source", System.StringComparison.OrdinalIgnoreCase)
            && !connectionString.Contains("Default Timeout", System.StringComparison.OrdinalIgnoreCase))
        {
            return connectionString.TrimEnd(';') + ";Default Timeout=30";
        }
        return connectionString;
    }

    public static string ResolveConnectionString(string connectionString, string basePath)
    {
        foreach (var part in connectionString.Split(';', System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=', System.StringComparison.OrdinalIgnoreCase);
            if (index <= 0) continue;
            var key = part.Substring(0, index).Trim();
            if (!key.Equals("Data Source", System.StringComparison.OrdinalIgnoreCase)
                && !key.Equals("DataSource", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = part.Substring(index + 1).Trim();
            if (!System.IO.Path.IsPathRooted(value))
            {
                var absolute = System.IO.Path.GetFullPath(System.IO.Path.Combine(basePath, value));
                return connectionString.Replace(part, $"'{key}={absolute}'", System.StringComparison.OrdinalIgnoreCase);
            }
        }
        return connectionString;
    }

    private static string? GetSqliteDirectory(string connectionString)
    {
        foreach (var part in connectionString.Split(';', System.StringSplitOptions.TrimEntries | System.StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=', System.StringComparison.OrdinalIgnoreCase);
            if (index <= 0) continue;
            var key = part.Substring(0, index).Trim();
            if (!key.Equals("Data Source", System.StringComparison.OrdinalIgnoreCase)
                && !key.Equals("DataSource", System.StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var value = part.Substring(index + 1).Trim();
            var separator = value.LastIndexOfAny(new[] { '/', '\\' });
            return separator > 0 ? value.Substring(0, separator) : null;
        }
        return null;
    }
}
