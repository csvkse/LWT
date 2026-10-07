using System.IO;
using Microsoft.Data.Sqlite;
using Dapper;

namespace LinuxWebTool.Infrastructure.Persistence;

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
  CreatedAt TEXT NOT NULL,
  LastUsedAt TEXT,
  ExpiresAt TEXT
);
CREATE INDEX IF NOT EXISTS idx_api_key_hash ON api_key(KeyHash);
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
  UpdateTime TEXT NOT NULL
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
  UpdateTime TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_easytier_instance_name ON easytier_nodes(InstanceName);
";
        cmd.ExecuteNonQuery();
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
