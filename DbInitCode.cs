    public static void Initialize(Microsoft.Data.Sqlite.SqliteConnection db)
    {
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