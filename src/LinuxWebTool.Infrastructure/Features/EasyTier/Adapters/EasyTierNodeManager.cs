using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace LinuxWebTool.Infrastructure.Features.EasyTier.Adapters;

/// <summary>
/// EasyTier 节点生命周期、实时拓扑与双层热更新管理器（支持 Native FFI 与 Core Binary 双引擎）
/// </summary>
public class EasyTierNodeManager(
    EasyTierNodeStore store,
    EasyTierHostSupervisor supervisor,
    ILogger<EasyTierNodeManager> logger) : IEasyTierManager, IHostedService
{
    private readonly ConcurrentDictionary<string, NodeRuntimeState> _runtimeStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Process> _runningProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private static readonly EasyTierNativeCallBoundary NativeCalls = new();
    private Dictionary<string, string> lastNativeSnapshot = new(StringComparer.OrdinalIgnoreCase);
    private DateTime nextNativeSnapshot;

    private async Task<T> MutateAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        if (!await lifecycle.WaitAsync(TimeSpan.FromSeconds(5), ct))
            throw new InvalidOperationException("EasyTier 节点操作正在执行，请稍后重试");
        try { return await operation(); }
        finally { lifecycle.Release(); }
    }

    private sealed class NodeRuntimeState
    {
        public string InstanceName { get; init; } = string.Empty;
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public string? LastError { get; set; }
        public string? DeviceName { get; set; }
        public string? VirtualIpv4 { get; set; }
        public int RpcPort { get; set; }
        public EasyTierEngineMode Mode { get; set; }
        public string? Network { get; set; }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Initializing EasyTier Node Manager...");

        var engineStatus = await supervisor.GetEngineStatusAsync(0);
        if (!engineStatus.IsInstalled)
        {
            logger.LogInformation("EasyTier Native Engine is not present on startup. Auto-start postponed.");
            return;
        }

        var allNodes = await store.GetAllNodesAsync();
        foreach (var node in allNodes.Where(n => n.AutoStart))
        {
            try
            {
                await StartNodeAsync(node.Id, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to auto-start EasyTier node {Name}", node.InstanceName);
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (!await lifecycle.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken)) return;
        try
        {
            logger.LogInformation("Stopping all active EasyTier nodes...");
            foreach (var (id, state) in _runtimeStates)
            {
                try
                {
                    await StopNodeInternalAsync(id, state.InstanceName, cancellationToken);
                    _runtimeStates.TryRemove(id, out _);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error stopping EasyTier node {Name}", state.InstanceName);
                }
            }
        }
        finally { lifecycle.Release(); }
    }

    public async Task<IReadOnlyList<EasyTierNodeStatusDto>> GetAllNodeStatusesAsync(CancellationToken ct = default)
    {
        var entities = await store.GetAllNodesAsync();
        var snapshotMap = await CollectNetworkInfosSafeAsync(ct);

        var results = new List<EasyTierNodeStatusDto>();
        foreach (var entity in entities)
        {
            var isRunning = _runtimeStates.TryGetValue(entity.Id, out var rt);
            snapshotMap.TryGetValue(entity.InstanceName, out var infoJson);

            var peerCount = 0;
            var directPeerCount = 0;
            var devName = entity.InstanceName;
            var virtualIp = entity.VirtualIpv4;
            long rx = 0;
            long tx = 0;

            // 1. 若为 CoreBinary 模式，尝试通过 CLI JSON 解析
            if (isRunning && rt != null && rt.Mode == EasyTierEngineMode.CoreBinary && supervisor.GetCliPath(out var cliPath))
            {
                var nodeJson = await RunCliJsonSafeAsync(cliPath!, rt.RpcPort, "node", ct);
                if (!string.IsNullOrWhiteSpace(nodeJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(nodeJson);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("ipv4_addr", out var ipP) && !string.IsNullOrWhiteSpace(ipP.GetString()))
                        {
                            virtualIp = ipP.GetString();
                        }
                    }
                    catch { }
                }

                var peerJson = await RunCliJsonSafeAsync(cliPath!, rt.RpcPort, "peer", ct);
                if (!string.IsNullOrWhiteSpace(peerJson))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(peerJson);
                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            peerCount = doc.RootElement.GetArrayLength();
                            foreach (var p in doc.RootElement.EnumerateArray())
                            {
                                if (p.TryGetProperty("cost", out var cP) && !string.Equals(cP.GetString(), "Local", StringComparison.OrdinalIgnoreCase))
                                {
                                    directPeerCount++;
                                }
                                else if (string.IsNullOrWhiteSpace(virtualIp))
                                {
                                    if (p.TryGetProperty("cidr", out var localCidr) && !string.IsNullOrWhiteSpace(localCidr.GetString()))
                                    {
                                        virtualIp = localCidr.GetString();
                                    }
                                    else if (p.TryGetProperty("ipv4", out var localIp) && !string.IsNullOrWhiteSpace(localIp.GetString()))
                                    {
                                        virtualIp = localIp.GetString();
                                    }
                                }

                                if (p.TryGetProperty("rx_bytes", out var rxP)) rx += ParseByteString(rxP.GetString());
                                if (p.TryGetProperty("tx_bytes", out var txP)) tx += ParseByteString(txP.GetString());
                            }
                        }
                    }
                    catch { }
                }
            }
            // 2. 若为 FFI 模式，从内存快照解析
            else if (!string.IsNullOrWhiteSpace(infoJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(infoJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("running", out var rProp) && rProp.GetBoolean())
                    {
                        isRunning = true;
                    }
                    if (root.TryGetProperty("dev_name", out var dnProp))
                    {
                        devName = dnProp.GetString() ?? devName;
                    }
                    if (root.TryGetProperty("my_node_info", out var nodeInfo) &&
                        nodeInfo.TryGetProperty("virtual_ipv4", out var vipProp))
                    {
                        virtualIp = vipProp.GetString() ?? virtualIp;
                    }
                    if (root.TryGetProperty("peers", out var peersProp) && peersProp.ValueKind == JsonValueKind.Array)
                    {
                        peerCount = peersProp.GetArrayLength();
                        foreach (var peer in peersProp.EnumerateArray())
                        {
                            if (peer.TryGetProperty("conns", out var connsProp) && connsProp.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var conn in connsProp.EnumerateArray())
                                {
                                    if (conn.TryGetProperty("stats", out var stats))
                                    {
                                        if (stats.TryGetProperty("rx_bytes", out var rxP)) rx += rxP.GetInt64();
                                        if (stats.TryGetProperty("tx_bytes", out var txP)) tx += txP.GetInt64();
                                    }
                                }
                            }
                            if (peer.TryGetProperty("directly_connected_conns", out var directProp) &&
                                directProp.ValueKind == JsonValueKind.Array && directProp.GetArrayLength() > 0)
                            {
                                directPeerCount++;
                            }
                        }
                    }
                }
                catch { }
            }

            results.Add(new EasyTierNodeStatusDto(
                Id: entity.Id,
                InstanceName: entity.InstanceName,
                NetworkName: entity.NetworkName,
                IsRunning: isRunning,
                Status: isRunning ? 1 : entity.Status,
                VirtualIpv4: virtualIp,
                DeviceName: devName,
                PeerCount: peerCount,
                DirectPeerCount: directPeerCount,
                TotalRxBytes: rx,
                TotalTxBytes: tx,
                LastError: rt?.LastError ?? entity.LastError,
                StartedAt: rt?.StartedAt,
                UpdateTime: entity.UpdateTime,
                Listeners: DeserializeList(entity.ListenersJson)));
        }

        return results;
    }

    public async Task<EasyTierNodeDetailDto?> GetNodeDetailAsync(string nodeId, CancellationToken ct = default)
    {
        var entity = await store.GetNodeByIdAsync(nodeId);
        if (entity == null) return null;

        var allStatuses = await GetAllNodeStatusesAsync(ct);
        var status = allStatuses.FirstOrDefault(s => s.Id == entity.Id)
            ?? new EasyTierNodeStatusDto(entity.Id, entity.InstanceName, entity.NetworkName, false, 0, null, null, 0, 0, 0, 0, null, null, entity.UpdateTime, DeserializeList(entity.ListenersJson));

        var configDto = new EasyTierNodeConfigDto(
            Id: entity.Id,
            InstanceName: entity.InstanceName,
            NetworkName: entity.NetworkName,
            NetworkSecret: entity.NetworkSecret,
            VirtualIpv4: entity.VirtualIpv4,
            EnableDhcp: entity.EnableDhcp,
            Listeners: DeserializeList(entity.ListenersJson),
            Peers: DeserializeList(entity.PeersJson),
            ProxyNetworks: DeserializeList(entity.ProxyNetworksJson),
            Routes: DeserializeList(entity.RoutesJson),
            RawTomlOverride: entity.RawTomlOverride,
            AutoStart: entity.AutoStart,
            Status: entity.Status,
            LastError: entity.LastError,
            UpdateTime: entity.UpdateTime);

        var peers = new List<EasyTierPeerDetailDto>();
        var routes = new List<EasyTierRouteDetailDto>();
        string? stunNatType = null;
        var localIps = new List<string>();
        var activeListeners = new List<string>();

        var isRunning = _runtimeStates.TryGetValue(entity.Id, out var rt);

        // CoreBinary CLI 诊断
        if (isRunning && rt != null && rt.Mode == EasyTierEngineMode.CoreBinary && supervisor.GetCliPath(out var cliPath))
        {
            var nodeJson = await RunCliJsonSafeAsync(cliPath!, rt.RpcPort, "node", ct);
            if (!string.IsNullOrWhiteSpace(nodeJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(nodeJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("stun_info", out var stun))
                    {
                        stunNatType = stun.ToString();
                    }
                    if (root.TryGetProperty("listeners", out var lis) && lis.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var l in lis.EnumerateArray()) activeListeners.Add(l.GetString() ?? "");
                    }
                }
                catch { }
            }

            var peerJson = await RunCliJsonSafeAsync(cliPath!, rt.RpcPort, "peer", ct);
            if (!string.IsNullOrWhiteSpace(peerJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(peerJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in doc.RootElement.EnumerateArray())
                        {
                            var pid = p.TryGetProperty("id", out var pidP) ? pidP.ToString() : "Unknown";
                            var host = p.TryGetProperty("hostname", out var hP) ? hP.GetString() ?? pid : pid;
                            var cost = p.TryGetProperty("cost", out var cP) ? cP.GetString() ?? "" : "";
                            var isDirect = !string.Equals(cost, "Relay", StringComparison.OrdinalIgnoreCase) && !string.Equals(cost, "Local", StringComparison.OrdinalIgnoreCase);

                            var vip = "";
                            if (p.TryGetProperty("cidr", out var cidrP) && !string.IsNullOrWhiteSpace(cidrP.GetString()))
                            {
                                vip = cidrP.GetString()!;
                            }
                            else if (p.TryGetProperty("ipv4", out var ipP) && !string.IsNullOrWhiteSpace(ipP.GetString()))
                            {
                                vip = ipP.GetString()!;
                            }

                            if (cost == "Local" && string.IsNullOrWhiteSpace(vip) && !string.IsNullOrWhiteSpace(rt.VirtualIpv4))
                            {
                                vip = rt.VirtualIpv4;
                            }

                            double lat = 0;
                            if (p.TryGetProperty("lat_ms", out var latP))
                            {
                                var lStr = latP.GetString();
                                if (double.TryParse(lStr, out var parsedLat)) lat = Math.Round(parsedLat, 2);
                            }

                            double loss = 0;
                            if (p.TryGetProperty("loss_rate", out var lossP))
                            {
                                var str = lossP.GetString()?.TrimEnd('%');
                                if (double.TryParse(str, out var parsedLoss)) loss = parsedLoss;
                            }

                            var rxBytes = ParseByteString(p.TryGetProperty("rx_bytes", out var rxP) ? rxP.GetString() : null);
                            var txBytes = ParseByteString(p.TryGetProperty("tx_bytes", out var txP) ? txP.GetString() : null);
                            var proto = p.TryGetProperty("tunnel_proto", out var tp) ? tp.GetString() ?? "-" : "-";
                            var nat = p.TryGetProperty("nat_type", out var natP) ? natP.GetString() : null;
                            var ver = p.TryGetProperty("version", out var vP) ? vP.GetString() : null;

                            peers.Add(new EasyTierPeerDetailDto(
                                PeerId: pid,
                                Hostname: host,
                                VirtualIpv4: vip,
                                ConnectionType: isDirect ? "Direct (P2P)" : (cost == "Local" ? "Local (本机)" : "Relay (中继)"),
                                Protocol: proto,
                                TunnelAddress: nat,
                                LatencyMs: lat,
                                LossRate: loss,
                                RxBytes: rxBytes,
                                TxBytes: txBytes,
                                NatType: nat,
                                Version: ver));
                        }
                    }
                }
                catch { }
            }

            var routeJson = await RunCliJsonSafeAsync(cliPath!, rt.RpcPort, "route", ct);
            if (!string.IsNullOrWhiteSpace(routeJson))
            {
                try
                {
                    using var rdoc = JsonDocument.Parse(routeJson);
                    if (rdoc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in rdoc.RootElement.EnumerateArray())
                        {
                            var dest = r.TryGetProperty("ipv4", out var rIp) ? rIp.GetString() ?? "" : "";
                            var nextHop = r.TryGetProperty("next_hop_hostname", out var nhP) ? nhP.GetString() ?? "" : "";
                            var cost = r.TryGetProperty("path_len", out var plP) ? plP.GetInt32() : 0;
                            var lat = r.TryGetProperty("path_latency", out var pLat) ? pLat.ToString() : "-";
                            if (!string.IsNullOrWhiteSpace(dest))
                            {
                                routes.Add(new EasyTierRouteDetailDto(dest, nextHop, cost, lat));
                            }
                        }
                    }
                }
                catch { }
            }
        }
        else
        {
            var snapshotMap = await CollectNetworkInfosSafeAsync(ct);
            if (snapshotMap.TryGetValue(entity.InstanceName, out var infoJson) && !string.IsNullOrWhiteSpace(infoJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(infoJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("my_node_info", out var myNode))
                    {
                        if (myNode.TryGetProperty("stun_info", out var stun)) stunNatType = stun.ToString();
                        if (myNode.TryGetProperty("ips", out var ips) && ips.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var ip in ips.EnumerateArray()) localIps.Add(ip.GetString() ?? "");
                        }
                        if (myNode.TryGetProperty("listeners", out var lis) && lis.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var l in lis.EnumerateArray()) activeListeners.Add(l.GetString() ?? "");
                        }
                    }

                    if (root.TryGetProperty("peers", out var peersArr) && peersArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in peersArr.EnumerateArray())
                        {
                            var peerId = p.TryGetProperty("peer_id", out var pidP) ? pidP.ToString() : "Unknown";
                            var isDirect = p.TryGetProperty("directly_connected_conns", out var dProp) &&
                                           dProp.ValueKind == JsonValueKind.Array && dProp.GetArrayLength() > 0;
                            long rx = 0, tx = 0;
                            double latency = 0;
                            var proto = "UDP";
                            string? tunnelAddr = null;

                            if (p.TryGetProperty("conns", out var cArr) && cArr.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var c in cArr.EnumerateArray())
                                {
                                    if (c.TryGetProperty("stats", out var st))
                                    {
                                        if (st.TryGetProperty("rx_bytes", out var rxP)) rx += rxP.GetInt64();
                                        if (st.TryGetProperty("tx_bytes", out var txP)) tx += txP.GetInt64();
                                        if (st.TryGetProperty("latency_us", out var latP)) latency = latP.GetDouble() / 1000.0;
                                    }
                                    if (c.TryGetProperty("tunnel", out var tP)) tunnelAddr = tP.GetString();
                                }
                            }

                            peers.Add(new EasyTierPeerDetailDto(
                                PeerId: peerId,
                                Hostname: peerId.Length > 8 ? peerId[..8] : peerId,
                                VirtualIpv4: "",
                                ConnectionType: isDirect ? "Direct (P2P)" : "Relay (中继)",
                                Protocol: proto,
                                TunnelAddress: tunnelAddr,
                                LatencyMs: Math.Round(latency, 2),
                                LossRate: 0,
                                RxBytes: rx,
                                TxBytes: tx));
                        }
                    }
                }
                catch { }
            }
        }

        var toml = EasyTierConfigGenerator.GenerateToml(entity);
        return new EasyTierNodeDetailDto(
            Config: configDto,
            Status: status,
            Peers: peers,
            Routes: routes,
            StunNatType: stunNatType,
            LocalPhysicalIps: localIps,
            ActiveListeners: activeListeners,
            GeneratedToml: toml);
    }

    public async Task<EasyTierNodeStatusDto> CreateNodeAsync(CreateEasyTierNodeRequest request, CancellationToken ct = default)
        => await MutateAsync(() => CreateNodeCoreAsync(request, ct), ct);

    private async Task<EasyTierNodeStatusDto> CreateNodeCoreAsync(CreateEasyTierNodeRequest request, CancellationToken ct)
    {
        var existing = await store.GetNodeByNameAsync(request.InstanceName);
        if (existing != null)
        {
            throw new InvalidOperationException($"节点实例名称 '{request.InstanceName}' 已存在");
        }

        var toml = EasyTierConfigGenerator.GenerateToml(request);
        if (!EasyTierConfigGenerator.ValidateConfig(toml, out var error))
        {
            throw new ArgumentException($"EasyTier 配置格式校验失败: {error}");
        }

        var entity = new EasyTierNodeEntity
        {
            Id = Guid.NewGuid().ToString("N"),
            InstanceName = request.InstanceName.Trim(),
            NetworkName = request.NetworkName.Trim(),
            NetworkSecret = request.NetworkSecret?.Trim() ?? string.Empty,
            VirtualIpv4 = string.IsNullOrWhiteSpace(request.VirtualIpv4) ? null : request.VirtualIpv4.Trim(),
            EnableDhcp = request.EnableDhcp,
            ListenersJson = JsonSerializer.Serialize(request.Listeners ?? [], EasyTierJsonContext.Default.ListString),
            PeersJson = JsonSerializer.Serialize(request.Peers ?? [], EasyTierJsonContext.Default.ListString),
            ProxyNetworksJson = JsonSerializer.Serialize(request.ProxyNetworks ?? [], EasyTierJsonContext.Default.ListString),
            RoutesJson = JsonSerializer.Serialize(request.Routes ?? [], EasyTierJsonContext.Default.ListString),
            RawTomlOverride = string.IsNullOrWhiteSpace(request.RawTomlOverride) ? null : request.RawTomlOverride.Trim(),
            AutoStart = request.AutoStart,
            Status = 0,
            UpdateTime = DateTime.UtcNow
        };

        await store.UpsertNodeAsync(entity);

        if (entity.AutoStart && supervisor.GetEngineMode(out _) != EasyTierEngineMode.None)
        {
            try
            {
                await StartNodeInternalAsync(entity, ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to automatically start newly created node {Name}", entity.InstanceName);
            }
        }

        var statuses = await GetAllNodeStatusesAsync(ct);
        return statuses.First(s => s.Id == entity.Id);
    }

    public async Task<EasyTierNodeStatusDto> UpdateNodeAsync(string nodeId, UpdateEasyTierNodeRequest request, CancellationToken ct = default)
        => await MutateAsync(() => UpdateNodeCoreAsync(nodeId, request, ct), ct);

    private async Task<EasyTierNodeStatusDto> UpdateNodeCoreAsync(string nodeId, UpdateEasyTierNodeRequest request, CancellationToken ct)
    {
        var entity = await store.GetNodeByIdAsync(nodeId)
            ?? throw new KeyNotFoundException($"未找到 ID 为 '{nodeId}' 的节点");

        var isRunning = _runtimeStates.ContainsKey(entity.Id);

        var requiresRestart = !string.Equals(entity.InstanceName, request.InstanceName, StringComparison.Ordinal)
            || !string.Equals(entity.NetworkName, request.NetworkName, StringComparison.Ordinal)
            || !string.Equals(entity.NetworkSecret, request.NetworkSecret ?? "", StringComparison.Ordinal)
            || entity.EnableDhcp != request.EnableDhcp
            || !string.Equals(entity.VirtualIpv4, request.VirtualIpv4, StringComparison.Ordinal)
            || !string.Equals(entity.RawTomlOverride, request.RawTomlOverride, StringComparison.Ordinal)
            || !ListsEqual(DeserializeList(entity.ListenersJson), request.Listeners ?? []);

        var duplicate = await store.GetNodeByNameAsync(request.InstanceName.Trim());
        if (duplicate is not null && duplicate.Id != entity.Id)
            throw new InvalidOperationException("节点实例名称已存在");
        entity.InstanceName = request.InstanceName.Trim();
        entity.NetworkName = request.NetworkName.Trim();
        entity.NetworkSecret = request.NetworkSecret?.Trim() ?? string.Empty;
        entity.VirtualIpv4 = string.IsNullOrWhiteSpace(request.VirtualIpv4) ? null : request.VirtualIpv4.Trim();
        entity.EnableDhcp = request.EnableDhcp;
        entity.ListenersJson = JsonSerializer.Serialize(request.Listeners ?? [], EasyTierJsonContext.Default.ListString);
        entity.PeersJson = JsonSerializer.Serialize(request.Peers ?? [], EasyTierJsonContext.Default.ListString);
        entity.ProxyNetworksJson = JsonSerializer.Serialize(request.ProxyNetworks ?? [], EasyTierJsonContext.Default.ListString);
        entity.RoutesJson = JsonSerializer.Serialize(request.Routes ?? [], EasyTierJsonContext.Default.ListString);
        entity.RawTomlOverride = string.IsNullOrWhiteSpace(request.RawTomlOverride) ? null : request.RawTomlOverride.Trim();
        entity.AutoStart = request.AutoStart;
        entity.UpdateTime = DateTime.UtcNow;

        var updatedToml = EasyTierConfigGenerator.GenerateToml(entity);
        if (!EasyTierConfigGenerator.ValidateConfig(updatedToml, out var configError))
            throw new ArgumentException($"EasyTier 配置格式校验失败: {configError}");
        if (isRunning) ValidateNetwork(updatedToml, entity.Id);
        await store.UpsertNodeAsync(entity);

        if (isRunning)
        {
            if (requiresRestart)
            {
                logger.LogInformation("Node {Name} critical configuration changed. Performing graceful reload...", entity.InstanceName);
                await StopNodeInternalAsync(entity.Id, _runtimeStates[entity.Id].InstanceName, ct);
                _runtimeStates.TryRemove(entity.Id, out _);
                await StartNodeInternalAsync(entity, ct);
            }
            else
            {
                logger.LogInformation("Node {Name} dynamic configuration changed. Applying in-place hot patch...", entity.InstanceName);
                var patch = new EasyTierPatchRequestDto(
                    ConnectorsToAdd: request.Peers,
                    ConnectorsToRemove: null,
                    ProxyNetworks: request.ProxyNetworks,
                    Routes: request.Routes,
                    Hostname: entity.InstanceName,
                    DisableRelayData: null,
                    PreferPeerRelay: null);
                var applied = await PatchNodeConfigCoreAsync(entity.Id, patch, ct);
                if (!applied.Success) throw new InvalidOperationException(applied.Message);
            }
        }

        var statuses = await GetAllNodeStatusesAsync(ct);
        return statuses.First(s => s.Id == entity.Id);
    }

    public async Task<bool> DeleteNodeAsync(string nodeId, CancellationToken ct = default)
        => await MutateAsync(() => DeleteNodeCoreAsync(nodeId, ct), ct);

    private async Task<bool> DeleteNodeCoreAsync(string nodeId, CancellationToken ct)
    {
        var entity = await store.GetNodeByIdAsync(nodeId);
        if (entity == null) return false;

        if (_runtimeStates.TryGetValue(entity.Id, out var state))
        {
            await StopNodeInternalAsync(entity.Id, state.InstanceName, ct);
            _runtimeStates.TryRemove(entity.Id, out _);
        }

        await store.DeleteNodeAsync(nodeId);
        return true;
    }

    public async Task<bool> StartNodeAsync(string nodeId, CancellationToken ct = default)
        => await MutateAsync(() => StartNodeCoreAsync(nodeId, ct), ct);

    private async Task<bool> StartNodeCoreAsync(string nodeId, CancellationToken ct)
    {
        var entity = await store.GetNodeByIdAsync(nodeId)
            ?? throw new KeyNotFoundException($"未找到 ID 为 '{nodeId}' 的节点");

        return await StartNodeInternalAsync(entity, ct);
    }

    public async Task<bool> StopNodeAsync(string nodeId, CancellationToken ct = default)
        => await MutateAsync(() => StopNodeCoreAsync(nodeId, ct), ct);

    private async Task<bool> StopNodeCoreAsync(string nodeId, CancellationToken ct)
    {
        var entity = await store.GetNodeByIdAsync(nodeId)
            ?? throw new KeyNotFoundException($"未找到 ID 为 '{nodeId}' 的节点");

        await StopNodeInternalAsync(entity.Id, _runtimeStates.TryGetValue(entity.Id, out var running) ? running.InstanceName : entity.InstanceName, ct);
        _runtimeStates.TryRemove(entity.Id, out _);
        await store.UpdateStatusAsync(entity.Id, 0, null);
        return true;
    }

    public async Task<EasyTierPatchResultDto> PatchNodeConfigAsync(string nodeId, EasyTierPatchRequestDto patch, CancellationToken ct = default)
        => await MutateAsync(() => PatchNodeConfigCoreAsync(nodeId, patch, ct), ct);

    private async Task<EasyTierPatchResultDto> PatchNodeConfigCoreAsync(string nodeId, EasyTierPatchRequestDto patch, CancellationToken ct)
    {
        var entity = await store.GetNodeByIdAsync(nodeId)
            ?? throw new KeyNotFoundException($"未找到 ID 为 '{nodeId}' 的节点");

        if (!_runtimeStates.TryGetValue(entity.Id, out var rt))
        {
            return new EasyTierPatchResultDto(false, false, "节点未运行，无法执行原地热打补丁", DateTime.UtcNow);
        }

        if (rt.Mode == EasyTierEngineMode.CoreBinary && supervisor.GetCliPath(out var cliPath))
        {
            // 通过 CLI 动态更新 connectors
            if (patch.ConnectorsToAdd != null && patch.ConnectorsToAdd.Count > 0)
            {
                foreach (var c in patch.ConnectorsToAdd)
                {
                    await RunCliJsonSafeAsync(cliPath!, rt.RpcPort, $"connector add {c}", ct);
                }
            }
            return new EasyTierPatchResultDto(true, false, "已通过 CLI 成功注入动态对端", DateTime.UtcNow);
        }
        else if (rt.Mode == EasyTierEngineMode.NativeFfi)
        {
            try
            {
                var patchNode = new JsonObject();
                if (patch.ConnectorsToAdd != null)
                {
                    var connArr = new JsonArray();
                    foreach (var c in patch.ConnectorsToAdd) connArr.Add(c);
                    patchNode["connectors"] = connArr;
                }
                if (patch.ProxyNetworks != null)
                {
                    var pArr = new JsonArray();
                    foreach (var p in patch.ProxyNetworks) pArr.Add(p);
                    patchNode["proxy_networks"] = pArr;
                }
                if (patch.Routes != null)
                {
                    var rArr = new JsonArray();
                    foreach (var r in patch.Routes) rArr.Add(r);
                    patchNode["routes"] = rArr;
                }
                if (!string.IsNullOrWhiteSpace(patch.Hostname))
                {
                    patchNode["hostname"] = patch.Hostname;
                }

                var payload = new JsonObject
                {
                    ["instance"] = entity.InstanceName,
                    ["patch"] = patchNode
                };

                var payloadJson = payload.ToJsonString();
                var response = await NativeCalls.RunAsync(() =>
                {
                var ret = EasyTierNativeMethods.call_json_rpc(
                    "api.config.ConfigRpcService",
                    "PatchConfig",
                    null,
                    payloadJson,
                    out var respPtr);

                if (ret != 0)
                {
                    var err = EasyTierNativeMethods.GetLastErrorMessage();
                    return new EasyTierPatchResultDto(false, false, $"热打补丁失败: {err}", DateTime.UtcNow);
                }

                if (respPtr != IntPtr.Zero) EasyTierNativeMethods.free_string(respPtr);
                return new EasyTierPatchResultDto(true, false, "原地热打补丁成功应用，网络零中断", DateTime.UtcNow);
                }, TimeSpan.FromSeconds(3), ct);
                return response;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to apply runtime patch to node {Name}", entity.InstanceName);
                return new EasyTierPatchResultDto(false, false, $"应用异常: {ex.Message}", DateTime.UtcNow);
            }
        }

        return new EasyTierPatchResultDto(false, false, "未就绪的引擎模式", DateTime.UtcNow);
    }

    public Task<EasyTierEngineStatusDto> GetEngineStatusAsync(CancellationToken ct = default)
    {
        return supervisor.GetEngineStatusAsync(_runtimeStates.Count);
    }

    public Task<EasyTierGitHubReleaseInfoDto> CheckGitHubReleaseAsync(string? proxyPrefix = null, CancellationToken ct = default)
    {
        return supervisor.CheckGitHubReleaseAsync(proxyPrefix, ct);
    }

    public async Task<EasyTierUpgradeResultDto> InstallGitHubReleaseAsync(InstallGitHubReleaseRequest request, CancellationToken ct = default)
        => await MutateAsync(() => InstallGitHubReleaseCoreAsync(request, ct), ct);

    private async Task<EasyTierUpgradeResultDto> InstallGitHubReleaseCoreAsync(InstallGitHubReleaseRequest request, CancellationToken ct)
    {
        var activeIds = _runtimeStates.Keys.ToList();

        var result = await supervisor.InstallGitHubReleaseAsync(
            request,
            onBeforeSwapCallback: async () =>
            {
                logger.LogInformation("Draining {Count} active nodes before GitHub core install...", activeIds.Count);
                foreach (var id in activeIds)
                {
                    if (_runtimeStates.TryGetValue(id, out var state))
                    {
                        await StopNodeInternalAsync(id, state.InstanceName, ct);
                        _runtimeStates.TryRemove(id, out _);
                    }
                }
                await Task.Delay(200, ct);
                return activeIds.Count;
            },
            onAfterSwapCallback: async () =>
            {
                logger.LogInformation("Restoring {Count} active nodes after GitHub core install...", activeIds.Count);
                var restored = 0;
                foreach (var id in activeIds)
                {
                    var node = await store.GetNodeByIdAsync(id);
                    if (node != null)
                    {
                        try
                        {
                            await StartNodeInternalAsync(node, ct);
                            restored++;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to restore node {Name} after update", node.InstanceName);
                        }
                    }
                }
                return restored;
            },
            ct);

        return result;
    }

    public async Task<EasyTierUpgradeResultDto> UpgradeEngineAsync(Stream binaryStream, string fileName, CancellationToken ct = default)
        => await MutateAsync(() => UpgradeEngineCoreAsync(binaryStream, fileName, ct), ct);

    private async Task<EasyTierUpgradeResultDto> UpgradeEngineCoreAsync(Stream binaryStream, string fileName, CancellationToken ct)
    {
        var activeIds = _runtimeStates.Keys.ToList();

        var result = await supervisor.ExecuteHotUpgradeAsync(
            binaryStream,
            fileName,
            onBeforeSwapCallback: async () =>
            {
                logger.LogInformation("Draining {Count} active nodes before binary hot swap...", activeIds.Count);
                foreach (var id in activeIds)
                {
                    if (_runtimeStates.TryGetValue(id, out var state))
                    {
                        await StopNodeInternalAsync(id, state.InstanceName, ct);
                        _runtimeStates.TryRemove(id, out _);
                    }
                }
                await Task.Delay(200, ct);
                return activeIds.Count;
            },
            onAfterSwapCallback: async () =>
            {
                logger.LogInformation("Restoring {Count} previously active nodes with new core...", activeIds.Count);
                var restored = 0;
                foreach (var id in activeIds)
                {
                    var node = await store.GetNodeByIdAsync(id);
                    if (node != null)
                    {
                        try
                        {
                            await StartNodeInternalAsync(node, ct);
                            restored++;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to restore node {Name} after upgrade", node.InstanceName);
                        }
                    }
                }
                return restored;
            },
            ct);

        return result;
    }

    protected virtual async Task<bool> StartNodeInternalAsync(EasyTierNodeEntity node, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_runtimeStates.TryGetValue(node.Id, out var existing))
        {
            if (existing.LastError is not null) throw new InvalidOperationException(existing.LastError);
            if (!_runningProcesses.TryGetValue(node.Id, out var current) || !current.HasExited) return true;
            current.Dispose();
            _runningProcesses.TryRemove(node.Id, out _);
            _runtimeStates.TryRemove(node.Id, out _);
        }
        if (NativeCalls.IsBusy) throw new InvalidOperationException("上次 EasyTier 原生调用尚未完成，暂停启动");
        var toml = EasyTierConfigGenerator.GenerateToml(node);
        ValidateNetwork(toml, node.Id);
        var mode = supervisor.GetEngineMode(out var resolvedPath);
        if (mode == EasyTierEngineMode.None || string.IsNullOrWhiteSpace(resolvedPath))
        {
            var err = "EasyTier 内核引擎未就绪，请在“内核管理”中从 GitHub 一键安装或手动上传内核。";
            await store.UpdateStatusAsync(node.Id, 2, err);
            throw new InvalidOperationException(err);
        }

        logger.LogInformation("Starting EasyTier instance '{Name}' via mode {Mode}...", node.InstanceName, mode);

        var usedPorts = _runtimeStates.Values.Select(r => r.RpcPort).ToHashSet();
        var rpcPort = Enumerable.Range(15888, 1000).FirstOrDefault(p => !usedPorts.Contains(p));
        if (rpcPort == 0) throw new InvalidOperationException("没有可用的 EasyTier RPC 端口");

        if (mode == EasyTierEngineMode.NativeFfi)
        {
            var reservation = new NodeRuntimeState { InstanceName = node.InstanceName, Mode = mode,
                VirtualIpv4 = node.VirtualIpv4, Network = EasyTierNetworkGuard.GetVirtualNetwork(toml), RpcPort = rpcPort };
            _runtimeStates[node.Id] = reservation;
            var knownFailure = false;
            try
            {
                var error = await NativeCalls.RunAsync(() =>
                    EasyTierNativeMethods.run_network_instance(toml) == 0 ? null : EasyTierNativeMethods.GetLastErrorMessage(),
                    TimeSpan.FromSeconds(5), ct);
                knownFailure = error is not null;
                if (error is not null) throw new InvalidOperationException($"启动 EasyTier 实例失败: {error}");
            }
            catch (Exception ex)
            {
                if (knownFailure) _runtimeStates.TryRemove(node.Id, out _);
                else reservation.LastError = "原生启动结果未确认，请先停止此节点再重试：" + ex.Message;
                await store.UpdateStatusAsync(node.Id, 2, ex.Message);
                throw;
            }
        }
        else if (mode == EasyTierEngineMode.CoreBinary)
        {
            var nodeTomlPath = Path.Combine(supervisor.StorageDirectory, "nodes", $"{node.InstanceName}.toml");
            await File.WriteAllTextAsync(nodeTomlPath, toml, ct);

            var psi = new ProcessStartInfo
            {
                FileName = resolvedPath,
                Arguments = $"-c \"{nodeTomlPath}\" --rpc-portal 127.0.0.1:{rpcPort}",
                WorkingDirectory = supervisor.BinDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            var proc = Process.Start(psi);
            if (proc == null)
            {
                throw new InvalidOperationException($"无法拉起 easytier-core 进程: {resolvedPath}");
            }

            var runtimeState = new NodeRuntimeState
            {
                InstanceName = node.InstanceName,
                StartedAt = DateTime.UtcNow,
                VirtualIpv4 = node.VirtualIpv4,
                RpcPort = rpcPort,
                Mode = mode,
                Network = EasyTierNetworkGuard.GetVirtualNetwork(toml)
            };

            proc.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    logger.LogDebug("[EasyTier:{Name}] {Msg}", node.InstanceName, e.Data);
                }
            };
            proc.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data))
                {
                    if (e.Data.Contains("Failed to create adapter", StringComparison.OrdinalIgnoreCase))
                    {
                        runtimeState.LastError = OperatingSystem.IsWindows()
                            ? "创建 TUN 虚拟网卡失败 (Windows 下创建虚拟网卡需要以管理员身份运行 WebHost)"
                            : "创建 TUN 虚拟网卡失败 (Linux 下需要 root 或 CAP_NET_ADMIN 权限)";
                        logger.LogWarning("[EasyTier:{Name}] {Msg}", node.InstanceName, runtimeState.LastError);
                    }
                    else if (e.Data.Contains("ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning("[EasyTier:{Name}] {Msg}", node.InstanceName, e.Data);
                    }
                    else
                    {
                        logger.LogDebug("[EasyTier:{Name}] {Msg}", node.InstanceName, e.Data);
                    }
                }
            };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _runningProcesses[node.Id] = proc;
            _runtimeStates[node.Id] = runtimeState;
        }
        else
        {
            _runtimeStates[node.Id] = new NodeRuntimeState
            {
                InstanceName = node.InstanceName,
                StartedAt = DateTime.UtcNow,
                VirtualIpv4 = node.VirtualIpv4,
                RpcPort = rpcPort,
                Mode = mode,
                Network = EasyTierNetworkGuard.GetVirtualNetwork(toml)
            };
        }

        await store.UpdateStatusAsync(node.Id, 1, null);
        logger.LogInformation("EasyTier network instance '{Name}' started successfully.", node.InstanceName);
        return true;
    }

    protected virtual async Task StopNodeInternalAsync(string nodeId, string instanceName, CancellationToken ct)
    {
        if (_runningProcesses.TryGetValue(nodeId, out var proc))
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
                await proc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(2), ct);
            }
            _runningProcesses.TryRemove(nodeId, out _);
            proc.Dispose();
            return;
        }

        if (_runtimeStates.TryGetValue(nodeId, out var state) && state.Mode == EasyTierEngineMode.NativeFfi)
        {
            await NativeCalls.RunAsync(() =>
            {
                var namePtr = Marshal.StringToCoTaskMemUTF8(instanceName);
                var arrPtr = Marshal.AllocCoTaskMem(IntPtr.Size);
                Marshal.WriteIntPtr(arrPtr, namePtr);
                try
                {
                    if (EasyTierNativeMethods.delete_network_instance(arrPtr, 1) != 0)
                        throw new InvalidOperationException(EasyTierNativeMethods.GetLastErrorMessage());
                }
                finally
                {
                    Marshal.FreeCoTaskMem(namePtr);
                    Marshal.FreeCoTaskMem(arrPtr);
                }
                return true;
            }, TimeSpan.FromSeconds(3), ct);
        }
    }

    private static async Task<string?> RunCliJsonSafeAsync(string cliPath, int rpcPort, string command, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cliPath,
                Arguments = $"-p 127.0.0.1:{rpcPort} --output json {command}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(2));

            var output = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    private void ValidateNetwork(string toml, string nodeId)
    {
        var running = _runtimeStates.Where(p => p.Key != nodeId).Select(p => p.Value).ToArray();
        EasyTierNetworkGuard.Validate(toml, running.Where(r => r.Network is not null).Select(r => r.Network!).ToArray(),
            EasyTierNetworkGuard.GetLocalNetworks(_runtimeStates.Values.Select(r => r.DeviceName ?? ""),
                _runtimeStates.Values.Select(r => r.Network)));
    }

    private async Task<Dictionary<string, string>> CollectNetworkInfosSafeAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < nextNativeSnapshot || NativeCalls.IsBusy) return lastNativeSnapshot;
        try
        {
            lastNativeSnapshot = await NativeCalls.RunAsync(CollectNetworkInfos, TimeSpan.FromSeconds(2), ct);
            nextNativeSnapshot = DateTime.UtcNow.AddSeconds(3);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning("EasyTier 状态采集未完成，返回上次快照：{Reason}", ex.Message); }
        return lastNativeSnapshot;
    }

    protected virtual Dictionary<string, string> CollectNetworkInfos()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!EasyTierNativeMethods.ProbeLibrary(out _)) return result;

        const int maxInstances = 32;
        var structSize = Marshal.SizeOf<KeyValuePairNative>();
        var buffer = Marshal.AllocCoTaskMem(structSize * maxInstances);

        try
        {
            var count = EasyTierNativeMethods.collect_network_infos(buffer, maxInstances);
            if (count <= 0) return result;

            for (var i = 0; i < count; i++)
            {
                var kv = Marshal.PtrToStructure<KeyValuePairNative>(buffer + (i * structSize));
                var key = Marshal.PtrToStringUTF8(kv.Key) ?? "";
                var val = Marshal.PtrToStringUTF8(kv.Value) ?? "";

                EasyTierNativeMethods.free_string(kv.Key);
                EasyTierNativeMethods.free_string(kv.Value);

                if (!string.IsNullOrEmpty(key)) result[key] = val;
            }
        }
        catch (Exception ex)
        {
            logger.LogTrace(ex, "Error collecting network infos from EasyTier FFI");
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }

        return result;
    }

    private static long ParseByteString(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "-") return 0;
        text = text.Trim();
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !double.TryParse(parts[0], out var val)) return 0;
        var unit = parts.Length > 1 ? parts[1].ToUpperInvariant() : "B";
        return unit switch
        {
            "KB" or "KIB" => (long)(val * 1024),
            "MB" or "MIB" => (long)(val * 1024 * 1024),
            "GB" or "GIB" => (long)(val * 1024 * 1024 * 1024),
            _ => (long)val
        };
    }

    private static List<string> DeserializeList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize(json, EasyTierJsonContext.Default.ListString) ?? []; } catch { return []; }
    }

    private static bool ListsEqual(List<string> a, List<string> b)
    {
        if (a.Count != b.Count) return false;
        return a.OrderBy(x => x).SequenceEqual(b.OrderBy(x => x));
    }

    public async Task<EasyTierAvailablePortDto> GetNextAvailablePortAsync(int? preferredStartPort = null, CancellationToken ct = default)
    {
        var allNodes = await store.GetAllNodesAsync();
        var reservedPorts = new HashSet<int>();

        foreach (var node in allNodes)
        {
            var listeners = DeserializeList(node.ListenersJson);
            foreach (var l in listeners)
            {
                foreach (var p in ExtractPortsFromText(l))
                {
                    reservedPorts.Add(p);
                }
            }
            if (!string.IsNullOrWhiteSpace(node.RawTomlOverride))
            {
                foreach (var p in ExtractPortsFromText(node.RawTomlOverride))
                {
                    reservedPorts.Add(p);
                }
            }
        }

        int candidate;
        if (preferredStartPort.HasValue && preferredStartPort.Value >= 1024 && preferredStartPort.Value <= 65530)
        {
            candidate = preferredStartPort.Value;
        }
        else if (reservedPorts.Count > 0)
        {
            candidate = reservedPorts.Max() + 1;
        }
        else
        {
            candidate = 11010;
        }

        var skippedPorts = new List<int>();
        var maxIterations = 200;

        while (candidate <= 65534 && maxIterations-- > 0)
        {
            var basePort = candidate;
            var wgPort = candidate + 1;

            // 1. 检查 basePort 是否已被现有节点预留
            if (reservedPorts.Contains(basePort))
            {
                skippedPorts.Add(basePort);
                candidate++;
                continue;
            }

            // 2. 检查 wgPort 是否已被现有节点预留
            if (reservedPorts.Contains(wgPort))
            {
                skippedPorts.Add(wgPort);
                candidate = wgPort + 1;
                continue;
            }

            // 3. 探测操作系统端口：basePort (TCP & UDP)
            if (!IsPortAvailable(basePort, checkTcp: true, checkUdp: true))
            {
                skippedPorts.Add(basePort);
                candidate++;
                continue;
            }

            // 4. 探测操作系统端口：wgPort (UDP)
            if (!IsPortAvailable(wgPort, checkTcp: false, checkUdp: true))
            {
                skippedPorts.Add(wgPort);
                candidate = wgPort + 1;
                continue;
            }

            // 端口对均可用且无任何冲突
            var suggested = new List<string>
            {
                $"tcp://0.0.0.0:{basePort}",
                $"udp://0.0.0.0:{basePort}",
                $"wg://0.0.0.0:{wgPort}",
                $"tcp://[::]:{basePort}",
                $"udp://[::]:{basePort}",
                $"wg://[::]:{wgPort}"
            };

            return new EasyTierAvailablePortDto(basePort, wgPort, skippedPorts, suggested);
        }

        return new EasyTierAvailablePortDto(11010, 11011, skippedPorts, [
            "tcp://0.0.0.0:11010", "udp://0.0.0.0:11010", "wg://0.0.0.0:11011",
            "tcp://[::]:11010", "udp://[::]:11010", "wg://[::]:11011"
        ]);
    }

    private static bool IsPortAvailable(int port, bool checkTcp, bool checkUdp)
    {
        if (port < 1 || port > 65535) return false;

        try
        {
            var ipProps = IPGlobalProperties.GetIPGlobalProperties();
            if (checkTcp)
            {
                var tcpListeners = ipProps.GetActiveTcpListeners();
                if (tcpListeners.Any(ep => ep.Port == port)) return false;
            }

            if (checkUdp)
            {
                var udpListeners = ipProps.GetActiveUdpListeners();
                if (udpListeners.Any(ep => ep.Port == port)) return false;
            }
        }
        catch
        {
            // 环境受限时忽略，直接进行 Socket 绑定探测
        }

        if (checkTcp)
        {
            try
            {
                using var tcp = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                if (OperatingSystem.IsWindows())
                {
                    try { tcp.ExclusiveAddressUse = true; } catch { }
                }
                tcp.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch
            {
                return false;
            }

            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    using var tcp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
                    if (OperatingSystem.IsWindows())
                    {
                        try { tcp6.ExclusiveAddressUse = true; } catch { }
                    }
                    tcp6.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                }
                catch
                {
                    return false;
                }
            }
        }

        if (checkUdp)
        {
            try
            {
                using var udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows())
                {
                    try { udp.ExclusiveAddressUse = true; } catch { }
                }
                udp.Bind(new IPEndPoint(IPAddress.Any, port));
            }
            catch
            {
                return false;
            }

            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    using var udp6 = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp);
                    if (OperatingSystem.IsWindows())
                    {
                        try { udp6.ExclusiveAddressUse = true; } catch { }
                    }
                    udp6.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
                }
                catch
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static List<int> ExtractPortsFromText(string text)
    {
        var ports = new List<int>();
        if (string.IsNullOrWhiteSpace(text)) return ports;

        foreach (Match m in Regex.Matches(text, @":(\d{2,5})\b"))
        {
            if (int.TryParse(m.Groups[1].Value, out var p) && p >= 1 && p <= 65535)
            {
                ports.Add(p);
            }
        }

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (int.TryParse(trimmed, out var p) && p >= 1 && p <= 65535)
            {
                ports.Add(p);
            }
        }

        if (text.Contains("wg://0.0.0.0") || text.Contains("wg://[::]")) ports.Add(11011);
        if (text.Contains("tcp://0.0.0.0") || text.Contains("udp://0.0.0.0")) ports.Add(11010);

        return ports;
    }
}
