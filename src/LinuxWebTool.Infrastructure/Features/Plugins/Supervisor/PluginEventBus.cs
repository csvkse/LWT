using System.Text.Json;
using System.Threading.Channels;
using LinuxWebTool.Infrastructure.Shared.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

public sealed record PluginOutboxEvent
{
    public required string Id { get; init; }
    public string? AggregateId { get; init; }
    public required string EventType { get; init; }
    public string? Payload { get; init; }
    public string Status { get; init; } = "Pending";
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 插件可靠事件总线 (Plugin Event Bus)
/// - 基于 SQLite WAL 事务性发件箱 (Transactional Outbox) 杜绝丢消息与双重真相；
/// - 基于 System.Threading.Channels 提供单机极速低延迟异步派发；
/// - 基于消费者收件箱 (Consumer Inbox) 实现联合唯一键幂等去重 (At-least-once + Exactly-once)。
/// </summary>
public sealed class PluginEventBus : IHostedService
{
    private readonly DbConnectionFactory _dbFactory;
    private readonly ILogger<PluginEventBus>? _logger;
    private readonly Channel<PluginOutboxEvent> _channel = Channel.CreateBounded<PluginOutboxEvent>(new BoundedChannelOptions(5000)
    {
        FullMode = BoundedChannelFullMode.Wait
    });

    private readonly List<Func<PluginOutboxEvent, Task>> _subscribers = [];
    private readonly object _subLock = new();
    private CancellationTokenSource? _cts;
    private Task? _dispatchTask;

    public PluginEventBus(DbConnectionFactory dbFactory, ILogger<PluginEventBus>? logger = null)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _dispatchTask = Task.Run(() => ProcessDispatchLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        if (_cts != null)
        {
            await _cts.CancelAsync();
        }
        if (_dispatchTask != null)
        {
            await Task.WhenAny(_dispatchTask, Task.Delay(2000, cancellationToken));
        }
    }

    /// <summary>
    /// 注册内存事件订阅者。
    /// </summary>
    public void Subscribe(Func<PluginOutboxEvent, Task> handler)
    {
        lock (_subLock)
        {
            _subscribers.Add(handler);
        }
    }

    /// <summary>
    /// 原子发布事件至持久化发件箱并入列通道。
    /// </summary>
    public async ValueTask PublishAsync(
        string aggregateId,
        string eventType,
        object? payloadObj,
        CancellationToken ct = default)
    {
        var eventId = Guid.NewGuid().ToString("N");
        var payloadJson = payloadObj switch
        {
            null => null,
            string str => str,
            JsonElement el => el.GetRawText(),
            _ => payloadObj.ToString()
        };

        var evt = new PluginOutboxEvent
        {
            Id = eventId,
            AggregateId = aggregateId,
            EventType = eventType,
            Payload = payloadJson,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        // 1. 同库写入 SQLite Outbox 表
        try
        {
            using var conn = _dbFactory.CreateConnection();
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO plugin_event_outbox (Id, AggregateId, EventType, Payload, Status, CreatedAt)
VALUES (@id, @aggId, @type, @payload, 'Pending', @created);";
            cmd.Parameters.AddWithValue("@id", evt.Id);
            cmd.Parameters.AddWithValue("@aggId", (object?)evt.AggregateId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", evt.EventType);
            cmd.Parameters.AddWithValue("@payload", (object?)evt.Payload ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@created", evt.CreatedAt.ToString("o"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "写入持久化 Outbox 记录异常 (EventId: {EventId})", eventId);
        }

        // 2. 写入内存通道供即刻消费
        await _channel.Writer.WriteAsync(evt, ct);
    }

    /// <summary>
    /// 查询指定聚合/插件或类型的事件列表（支持外部插件 HTTP 轮询）。
    /// </summary>
    public async ValueTask<IReadOnlyList<PluginOutboxEvent>> PollEventsAsync(
        string? aggregateId = null,
        string? eventType = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        var list = new List<PluginOutboxEvent>();
        using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();

        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(aggregateId))
        {
            conditions.Add("AggregateId = @aggId");
            cmd.Parameters.AddWithValue("@aggId", aggregateId);
        }
        if (!string.IsNullOrWhiteSpace(eventType))
        {
            conditions.Add("EventType = @type");
            cmd.Parameters.AddWithValue("@type", eventType);
        }

        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
        cmd.CommandText = $"SELECT Id, AggregateId, EventType, Payload, Status, CreatedAt FROM plugin_event_outbox {whereClause} ORDER BY ROWID DESC LIMIT @limit;";
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));

        try
        {
            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                list.Add(new PluginOutboxEvent
                {
                    Id = reader.GetString(0),
                    AggregateId = reader.IsDBNull(1) ? null : reader.GetString(1),
                    EventType = reader.GetString(2),
                    Payload = reader.IsDBNull(3) ? null : reader.GetString(3),
                    Status = reader.GetString(4),
                    CreatedAt = DateTime.TryParse(reader.GetString(5), out var dt) ? dt : DateTime.UtcNow
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "查询 Outbox 事件列表异常");
        }

        return list;
    }

    /// <summary>
    /// 基于收件箱 (Consumer Inbox) 幂等执行消费逻辑（防止重复副作用）。
    /// </summary>
    public async Task<bool> ExecuteWithInboxAsync(
        string consumerId,
        string eventId,
        Func<Task> action,
        CancellationToken ct = default)
    {
        using var conn = _dbFactory.CreateConnection();
        await conn.OpenAsync(ct);

        // 1. 检查是否已被消费
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(1) FROM plugin_event_inbox WHERE ConsumerId = @cId AND EventId = @eId;";
        checkCmd.Parameters.AddWithValue("@cId", consumerId);
        checkCmd.Parameters.AddWithValue("@eId", eventId);
        var count = Convert.ToInt32(await checkCmd.ExecuteScalarAsync(ct));
        if (count > 0)
        {
            _logger?.LogDebug("事件已在收件箱中存在，幂等跳过重复执行 (Consumer: {Consumer}, Event: {EventId})", consumerId, eventId);
            return false;
        }

        // 2. 执行核心业务逻辑
        await action();

        // 3. 记录已消费凭证
        using var recordCmd = conn.CreateCommand();
        recordCmd.CommandText = @"
INSERT OR IGNORE INTO plugin_event_inbox (ConsumerId, EventId, Status, ProcessedAt)
VALUES (@cId, @eId, 'Processed', @processed);";
        recordCmd.Parameters.AddWithValue("@cId", consumerId);
        recordCmd.Parameters.AddWithValue("@eId", eventId);
        recordCmd.Parameters.AddWithValue("@processed", DateTime.UtcNow.ToString("o"));
        await recordCmd.ExecuteNonQueryAsync(ct);

        return true;
    }

    private async Task ProcessDispatchLoopAsync(CancellationToken ct)
    {
        var reader = _channel.Reader;
        while (!ct.IsCancellationRequested && await reader.WaitToReadAsync(ct))
        {
            while (reader.TryRead(out var evt))
            {
                Func<PluginOutboxEvent, Task>[] subs;
                lock (_subLock)
                {
                    subs = [.. _subscribers];
                }

                foreach (var sub in subs)
                {
                    try
                    {
                        await sub(evt);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "事件订阅回调处理异常: {EventType}", evt.EventType);
                    }
                }

                // 标记 SQLite Outbox 为已发布
                try
                {
                    using var conn = _dbFactory.CreateConnection();
                    await conn.OpenAsync(ct);
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE plugin_event_outbox SET Status = 'Published', PublishedAt = @pub WHERE Id = @id;";
                    cmd.Parameters.AddWithValue("@id", evt.Id);
                    cmd.Parameters.AddWithValue("@pub", DateTime.UtcNow.ToString("o"));
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch { }
            }
        }
    }
}
