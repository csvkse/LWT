using System.IO;
using Microsoft.Data.Sqlite;
using Dapper;
using LinuxWebTool.Contracts.Features.Security.Contracts;
using LinuxWebTool.Infrastructure.Features.Security.Policies;
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
  EndTime TEXT,
  TargetType INTEGER NOT NULL DEFAULT 0,
  TargetHostId TEXT COLLATE NOCASE,
  TargetHostName TEXT,
  BatchId TEXT COLLATE NOCASE,
  StepOrder INTEGER NOT NULL DEFAULT 0,
  WorkflowId TEXT COLLATE NOCASE
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
  UpdateTime TEXT,
  DefaultTargetHostId TEXT COLLATE NOCASE,
  ExecutionTarget TEXT NOT NULL DEFAULT 'container'
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
  UpdateTime TEXT,
  TargetHostMode INTEGER NOT NULL DEFAULT 0,
  TargetHostId TEXT COLLATE NOCASE,
  TargetHostIds TEXT,
  FailurePolicy INTEGER NOT NULL DEFAULT 0,
  TargetBizType INTEGER NOT NULL DEFAULT 0,
  WorkflowId TEXT COLLATE NOCASE,
  ExecutionTarget TEXT NOT NULL DEFAULT 'container'
);
CREATE TABLE IF NOT EXISTS workflows (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT NOT NULL,
  Description TEXT,
  ExecutionMode INTEGER NOT NULL DEFAULT 0,
  FailurePolicy INTEGER NOT NULL DEFAULT 0,
  DefaultTargetHostId TEXT COLLATE NOCASE,
  DefinitionJson TEXT NOT NULL,
  Enabled INTEGER NOT NULL DEFAULT 1,
  GroupId TEXT COLLATE NOCASE,
  IsPinned INTEGER NOT NULL DEFAULT 0,
  SortOrder INTEGER NOT NULL DEFAULT 0,
  LastRunTime TEXT,
  CreateTime TEXT NOT NULL,
  UpdateTime TEXT NOT NULL
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
  AllowServices INTEGER NOT NULL DEFAULT 0,
  AllowWorkers INTEGER NOT NULL DEFAULT 0,
  AllowedHostIds TEXT,
  AllowedTools TEXT,
  AllowedCommandIds TEXT,
  PolicyTemplateIds TEXT,
  CreatedAt TEXT NOT NULL,
  LastUsedAt TEXT,
  ExpiresAt TEXT
);
CREATE INDEX IF NOT EXISTS idx_api_key_hash ON api_key(KeyHash);
CREATE TABLE IF NOT EXISTS security_policy_template (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  Name TEXT NOT NULL,
  Description TEXT,
  IsBuiltin INTEGER NOT NULL DEFAULT 0,
  RulesJson TEXT NOT NULL,
  CreatedAt TEXT NOT NULL,
  UpdatedAt TEXT NOT NULL
);
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
CREATE TABLE IF NOT EXISTS backup_sync_record (
  Id TEXT PRIMARY KEY COLLATE NOCASE,
  TaskId TEXT NOT NULL COLLATE NOCASE,
  TaskName TEXT NOT NULL,
  Mode INTEGER NOT NULL,
  TriggerType INTEGER NOT NULL DEFAULT 1,
  Status TEXT NOT NULL,
  StartTime TEXT NOT NULL,
  EndTime TEXT,
  DurationMs INTEGER NOT NULL DEFAULT 0,
  ProcessedFiles INTEGER NOT NULL DEFAULT 0,
  ProcessedBytes INTEGER NOT NULL DEFAULT 0,
  AddedFiles INTEGER NOT NULL DEFAULT 0,
  UpdatedFiles INTEGER NOT NULL DEFAULT 0,
  DeletedFiles INTEGER NOT NULL DEFAULT 0,
  SkippedFiles INTEGER NOT NULL DEFAULT 0,
  Summary TEXT,
  ErrorMessage TEXT,
  ClientIp TEXT
);
CREATE INDEX IF NOT EXISTS idx_backup_sync_record_task_time ON backup_sync_record(TaskId, StartTime DESC);
CREATE INDEX IF NOT EXISTS idx_backup_sync_record_time ON backup_sync_record(StartTime DESC);
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
        EnsureColumnExists(db, "api_key", "AllowServices", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "api_key", "AllowWorkers", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "api_key", "AllowedHostIds", "TEXT");
        EnsureColumnExists(db, "api_key", "AllowedTools", "TEXT");
        EnsureColumnExists(db, "api_key", "AllowedCommandIds", "TEXT");
        EnsureColumnExists(db, "api_key", "PolicyTemplateIds", "TEXT");
        EnsureColumnExists(db, "backup_sync_task", "ConsecutiveFailures", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "backup_sync_task", "AutoPausedReason", "TEXT");
        EnsureColumnExists(db, "linux_command", "DefaultTargetHostId", "TEXT COLLATE NOCASE");
        EnsureColumnExists(db, "linux_command", "ExecutionTarget", "TEXT NOT NULL DEFAULT 'container'");
        EnsureColumnExists(db, "schedule_task", "TargetHostMode", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "schedule_task", "TargetHostId", "TEXT COLLATE NOCASE");
        EnsureColumnExists(db, "schedule_task", "TargetHostIds", "TEXT");
        EnsureColumnExists(db, "schedule_task", "FailurePolicy", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "schedule_task", "TargetBizType", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "schedule_task", "WorkflowId", "TEXT COLLATE NOCASE");
        EnsureColumnExists(db, "schedule_task", "ExecutionTarget", "TEXT NOT NULL DEFAULT 'container'");
        EnsureColumnExists(db, "execution_record", "TargetType", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "execution_record", "TargetHostId", "TEXT COLLATE NOCASE");
        EnsureColumnExists(db, "execution_record", "TargetHostName", "TEXT");
        EnsureColumnExists(db, "execution_record", "BatchId", "TEXT COLLATE NOCASE");
        EnsureColumnExists(db, "execution_record", "StepOrder", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "execution_record", "WorkflowId", "TEXT COLLATE NOCASE");
        EnsureColumnExists(db, "backup_sync_record", "SkippedFiles", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumnExists(db, "backup_sync_record", "ClientIp", "TEXT");

        MigrateAndSeedSecurityPolicies(db);
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

    private static void MigrateAndSeedSecurityPolicies(SqliteConnection db)
    {
        var now = System.DateTime.UtcNow.ToString("O");
        const string dockerRules = """[{"RuleId":"r_docker_allow","Name":"Docker 常用运维动词白名单","Domain":3,"Effect":1,"CommandPatterns":"docker ps, docker logs, docker stats, docker restart, docker top, docker compose ps, docker compose logs","PathPatterns":null,"DisallowSubshells":true,"DisallowCommandChaining":true,"BlockHighRiskFlags":true,"ReadOnlyOnly":false}]""";
        const string workspaceRules = """[{"RuleId":"r_workspace_allow","Name":"专属工作区目录限定","Domain":12,"Effect":1,"CommandPatterns":null,"PathPatterns":"/home;/data/projects","DisallowSubshells":false,"DisallowCommandChaining":false,"BlockHighRiskFlags":false,"ReadOnlyOnly":false}]""";
        const string readonlyRules = """[{"RuleId":"r_readonly_fs","Name":"文件系统强制只读","Domain":12,"Effect":1,"CommandPatterns":null,"PathPatterns":"*","DisallowSubshells":false,"DisallowCommandChaining":false,"BlockHighRiskFlags":false,"ReadOnlyOnly":true},{"RuleId":"r_readonly_cmd","Name":"只读状态探针指令","Domain":3,"Effect":1,"CommandPatterns":"uptime, df, free, uname, top -b -n 1, ps aux, systemctl status *","PathPatterns":null,"DisallowSubshells":true,"DisallowCommandChaining":true,"BlockHighRiskFlags":true,"ReadOnlyOnly":false}]""";

        UpsertBuiltinTemplate(db, "tpl_docker_ops", "🐳 Docker 容器运维白名单 (内置)", "仅允许常用的容器查看、监控与安全重启操作，拦截特权模式与根目录挂载", dockerRules, now);
        UpsertBuiltinTemplate(db, "tpl_workspace_only", "📁 专属工作区目录限制 (内置)", "严格限定文件访问与工作目录在 /home 与 /data/projects，阻断全盘穿透", workspaceRules, now);
        UpsertBuiltinTemplate(db, "tpl_readonly_safe", "🔒 生产环境只读防护 (内置)", "严格禁止文件写入、删除与覆盖，终端仅放行无副作用的探针巡检指令", readonlyRules, now);

        try
        {
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT Id, Name, AllowedPaths, PolicyTemplateIds FROM api_key WHERE AllowedPaths IS NOT NULL AND trim(AllowedPaths) != ''";
            using var reader = cmd.ExecuteReader();
            var toMigrate = new System.Collections.Generic.List<(string Id, string Name, string Paths)>();
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var name = reader.GetString(1);
                var paths = reader.GetString(2);
                var pIds = reader.IsDBNull(3) ? null : reader.GetString(3);
                if (string.IsNullOrWhiteSpace(pIds))
                {
                    toMigrate.Add((id, name, paths));
                }
            }
            reader.Close();

            foreach (var item in toMigrate)
            {
                var tplId = $"tpl_migrated_{item.Id[..Math.Min(8, item.Id.Length)]}";
                var rulesJson = System.Text.Json.JsonSerializer.Serialize(new List<SecurityPolicyRule>
                {
                    new(
                        RuleId: "r_migrated_path",
                        Name: $"{item.Name} 原有路径防护",
                        Domain: SecurityTargetDomain.LocalFile | SecurityTargetDomain.RemoteFile,
                        Effect: PolicyEffect.Allow,
                        CommandPatterns: null,
                        PathPatterns: item.Paths,
                        DisallowSubshells: false,
                        DisallowCommandChaining: false,
                        BlockHighRiskFlags: false,
                        ReadOnlyOnly: false
                    )
                }, SecurityJsonContext.Default.ListSecurityPolicyRule);

                db.Execute(@"
                    INSERT OR IGNORE INTO security_policy_template (Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt)
                    VALUES (@Id, @Name, @Desc, 0, @Rules, @Now, @Now)",
                    new { Id = tplId, Name = $"{item.Name}-路径策略", Desc = "系统自动合并迁移生成的路径策略", Rules = rulesJson, Now = now });

                db.Execute("UPDATE api_key SET PolicyTemplateIds = @Pids, AllowedPaths = NULL WHERE Id = @Id",
                    new { Pids = $"[\"{tplId}\"]", Id = item.Id });
            }
        }
        catch { }
    }

    private static void UpsertBuiltinTemplate(SqliteConnection db, string id, string name, string desc, string rulesJson, string now)
    {
        db.Execute(@"
            INSERT INTO security_policy_template (Id, Name, Description, IsBuiltin, RulesJson, CreatedAt, UpdatedAt)
            VALUES (@Id, @Name, @Desc, 1, @RulesJson, @Now, @Now)
            ON CONFLICT(Id) DO UPDATE SET Name = @Name, Description = @Desc, RulesJson = @RulesJson, UpdatedAt = @Now",
            new { Id = id, Name = name, Desc = desc, RulesJson = rulesJson, Now = now });
    }
}
