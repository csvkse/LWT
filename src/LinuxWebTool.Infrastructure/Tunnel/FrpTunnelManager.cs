using System.Collections.Concurrent;
using LinuxWebTool.Contracts.Models;
using LinuxWebTool.Infrastructure.Persistence;
using LinuxWebTool.Infrastructure.Persistence.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Tunnel;

/// <summary>
/// FRP 穿透多线路管理器
/// 统一调度管理全部穿透实例、提供热插拔与状态监控
/// </summary>
public sealed class FrpTunnelManager(
    FrpTunnelLineStore store,
    ILogger<FrpTunnelManager> logger,
    ILoggerFactory loggerFactory) : IHostedService
{
    private readonly ConcurrentDictionary<string, FrpTunnelInstance> _instances = new();
    private readonly ILogger _instanceLogger = loggerFactory.CreateLogger("FrpTunnelInstance");
    private CancellationTokenSource? _managerCts;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _managerCts = new CancellationTokenSource();
        var lines = await store.GetAllLinesAsync();
        logger.LogInformation("FRP 穿透多线路管理器启动，已加载 {Count} 条线路", lines.Count);

        foreach (var line in lines)
        {
            var instance = new FrpTunnelInstance(line, _instanceLogger);
            _instances[line.Id] = instance;

            if (line.AutoStart)
            {
                logger.LogInformation("自动拉起线路 [{Name}] (Host: {Host})", line.Name, line.TunnelHost);
                _ = instance.StartAsync(_managerCts.Token);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("正在优雅关闭全部 FRP 穿透线路...");
        _managerCts?.Cancel();
        foreach (var (_, instance) in _instances)
        {
            await instance.StopAsync();
        }
        _instances.Clear();
    }

    public async Task<IReadOnlyList<FrpTunnelLineDto>> GetAllLinesWithStatusAsync()
    {
        var entities = await store.GetAllLinesAsync();
        var dtos = new List<FrpTunnelLineDto>();

        foreach (var entity in entities)
        {
            if (!_instances.TryGetValue(entity.Id, out var instance))
            {
                instance = new FrpTunnelInstance(entity, _instanceLogger);
                _instances[entity.Id] = instance;
            }

            dtos.Add(MapToDto(entity, instance));
        }

        return dtos;
    }

    public async Task<FrpTunnelLineDto?> GetLineWithStatusAsync(string id)
    {
        var entity = await store.GetLineByIdAsync(id);
        if (entity == null) return null;

        if (!_instances.TryGetValue(entity.Id, out var instance))
        {
            instance = new FrpTunnelInstance(entity, _instanceLogger);
            _instances[entity.Id] = instance;
        }

        return MapToDto(entity, instance);
    }

    public async Task<FrpTunnelLineDto> CreateLineAsync(CreateFrpTunnelLineRequest req)
    {
        var entity = new FrpTunnelLineEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = req.Name.Trim(),
            ServerUrl = req.ServerUrl.Trim(),
            BackupServerUrls = req.BackupServerUrls?.Trim(),
            TunnelHost = req.TunnelHost.Trim(),
            ApiKey = req.ApiKey?.Trim() ?? string.Empty,
            LocalTargetUrl = string.IsNullOrWhiteSpace(req.LocalTargetUrl) ? "http://127.0.0.1:8080" : req.LocalTargetUrl.Trim(),
            AutoStart = req.AutoStart,
            HeartbeatIntervalSeconds = req.HeartbeatIntervalSeconds > 0 ? req.HeartbeatIntervalSeconds : 15,
            EnableLan302Proxy = req.EnableLan302Proxy,
            ProxyType = string.IsNullOrWhiteSpace(req.ProxyType) ? "Direct" : req.ProxyType.Trim(),
            ProxyUrl = req.ProxyUrl?.Trim(),
            ProxyBypass = req.ProxyBypass?.Trim(),
            SortOrder = req.SortOrder,
            Status = "Disconnected",
            CreateTime = DateTime.UtcNow,
            UpdateTime = DateTime.UtcNow
        };

        await store.InsertLineAsync(entity);
        var instance = new FrpTunnelInstance(entity, _instanceLogger);
        _instances[entity.Id] = instance;

        if (entity.AutoStart && _managerCts != null)
        {
            _ = instance.StartAsync(_managerCts.Token);
        }

        return MapToDto(entity, instance);
    }

    public async Task<FrpTunnelLineDto?> UpdateLineAsync(string id, UpdateFrpTunnelLineRequest req)
    {
        var entity = await store.GetLineByIdAsync(id);
        if (entity == null) return null;

        entity.Name = req.Name.Trim();
        entity.ServerUrl = req.ServerUrl.Trim();
        entity.BackupServerUrls = req.BackupServerUrls?.Trim();
        entity.TunnelHost = req.TunnelHost.Trim();
        entity.ApiKey = req.ApiKey?.Trim() ?? string.Empty;
        entity.LocalTargetUrl = string.IsNullOrWhiteSpace(req.LocalTargetUrl) ? "http://127.0.0.1:8080" : req.LocalTargetUrl.Trim();
        entity.AutoStart = req.AutoStart;
        entity.HeartbeatIntervalSeconds = req.HeartbeatIntervalSeconds > 0 ? req.HeartbeatIntervalSeconds : 15;
        entity.EnableLan302Proxy = req.EnableLan302Proxy;
        entity.ProxyType = string.IsNullOrWhiteSpace(req.ProxyType) ? "Direct" : req.ProxyType.Trim();
        entity.ProxyUrl = req.ProxyUrl?.Trim();
        entity.ProxyBypass = req.ProxyBypass?.Trim();
        entity.SortOrder = req.SortOrder;
        entity.UpdateTime = DateTime.UtcNow;

        await store.UpdateLineAsync(entity);

        if (_instances.TryGetValue(id, out var instance))
        {
            var isRunning = instance.State == "Connected" || instance.State == "Connecting" || instance.State == "Reconnecting";
            if (isRunning)
            {
                await instance.StopAsync();
            }
            instance.UpdateConfig(entity);
            if (isRunning && _managerCts != null)
            {
                _ = instance.StartAsync(_managerCts.Token);
            }
        }
        else
        {
            instance = new FrpTunnelInstance(entity, _instanceLogger);
            _instances[id] = instance;
        }

        return MapToDto(entity, instance);
    }

    public async Task DeleteLineAsync(string id)
    {
        if (_instances.TryRemove(id, out var instance))
        {
            await instance.StopAsync();
        }
        await store.DeleteLineAsync(id);
    }

    public async Task StartLineAsync(string id)
    {
        if (!_instances.TryGetValue(id, out var instance))
        {
            var entity = await store.GetLineByIdAsync(id);
            if (entity == null) return;
            instance = new FrpTunnelInstance(entity, _instanceLogger);
            _instances[id] = instance;
        }

        if (_managerCts == null || _managerCts.IsCancellationRequested)
        {
            _managerCts = new CancellationTokenSource();
        }

        await instance.StartAsync(_managerCts.Token);
        await store.UpdateStatusAsync(id, "Connecting");
    }

    public async Task StopLineAsync(string id)
    {
        if (_instances.TryGetValue(id, out var instance))
        {
            await instance.StopAsync();
            await store.UpdateStatusAsync(id, "Stopped");
        }
    }

    public IReadOnlyList<FrpTunnelLogItem> GetLineLogs(string id)
    {
        return _instances.TryGetValue(id, out var instance) ? instance.GetRecentLogs() : Array.Empty<FrpTunnelLogItem>();
    }

    public IReadOnlyList<FrpTunnelLogItem> GetAllLogs()
    {
        return _instances.Values
            .SelectMany(i => i.GetRecentLogs())
            .OrderByDescending(l => l.Timestamp)
            .Take(150)
            .ToList();
    }

    public FrpTunnelInstance? GetInstance(string id) => _instances.TryGetValue(id, out var i) ? i : null;

    private static FrpTunnelLineDto MapToDto(FrpTunnelLineEntity entity, FrpTunnelInstance instance) => new(
        entity.Id,
        entity.Name,
        entity.ServerUrl,
        entity.BackupServerUrls,
        entity.TunnelHost,
        entity.ApiKey,
        entity.LocalTargetUrl,
        entity.AutoStart,
        entity.HeartbeatIntervalSeconds,
        entity.EnableLan302Proxy,
        entity.ProxyType,
        entity.ProxyUrl,
        entity.ProxyBypass,
        instance.State,
        instance.PublicUrl,
        instance.UptimeSeconds,
        instance.SentBytes,
        instance.ReceivedBytes,
        instance.LastError,
        entity.SortOrder,
        entity.CreateTime,
        entity.UpdateTime);
}
