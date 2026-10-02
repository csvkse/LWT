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
  Id TEXT PRIMARY KEY,
  Name TEXT,
  BizType INTEGER,
  SortOrder INTEGER,
  CreateTime TEXT
);
CREATE TABLE IF NOT EXISTS execution_record (
  Id TEXT PRIMARY KEY,
  Source INTEGER,
  CommandId TEXT,
  ScheduleTaskId TEXT,
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
  Id TEXT PRIMARY KEY,
  Name TEXT,
  CommandText TEXT,
  ScriptType INTEGER,
  Description TEXT,
  GroupId TEXT,
  IsPinned INTEGER,
  SortOrder INTEGER,
  TimeoutSeconds INTEGER,
  LastExecTime TEXT,
  CreateTime TEXT,
  UpdateTime TEXT
);
CREATE TABLE IF NOT EXISTS operation_log (
  Id TEXT PRIMARY KEY,
  Time TEXT,
  Action TEXT,
  TargetType TEXT,
  TargetName TEXT,
  Detail TEXT,
  ClientIp TEXT,
  Success INTEGER
);
CREATE TABLE IF NOT EXISTS schedule_task (
  Id TEXT PRIMARY KEY,
  Name TEXT,
  CommandId TEXT,
  CronExpression TEXT,
  Enabled INTEGER,
  GroupId TEXT,
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
  Id TEXT PRIMARY KEY,
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
  Id TEXT PRIMARY KEY,
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
  Id TEXT PRIMARY KEY,
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
  Id TEXT PRIMARY KEY,
  Time TEXT,
  Mount TEXT,
  FileSystem TEXT,
  UsagePercent REAL,
  TotalBytes INTEGER,
  UsedBytes INTEGER,
  FreeBytes INTEGER
);
CREATE TABLE IF NOT EXISTS system_status_net_snapshot (
  Id TEXT PRIMARY KEY,
  Time TEXT,
  Name TEXT,
  SentBytesPerSec INTEGER,
  RecvBytesPerSec INTEGER
);
CREATE TABLE IF NOT EXISTS system_status_process_snapshot (
  Id TEXT PRIMARY KEY,
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
  Id TEXT PRIMARY KEY,
  Time TEXT,
  CpuUsage REAL,
  Load1 REAL,
  MemUsage REAL,
  DiskRootUsage REAL,
  NetSentBps INTEGER,
  NetRecvBps INTEGER
);
CREATE TABLE IF NOT EXISTS transcode_job (
  Id TEXT PRIMARY KEY,
  SourcePath TEXT,
  OutputPath TEXT,
  PresetId TEXT,
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
  WatchRuleId TEXT,
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
  Id TEXT PRIMARY KEY,
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
  Id TEXT PRIMARY KEY,
  Name TEXT,
  WatchPath TEXT,
  FilePatterns TEXT,
  PresetId TEXT,
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
