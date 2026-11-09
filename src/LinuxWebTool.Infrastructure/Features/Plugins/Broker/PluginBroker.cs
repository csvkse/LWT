using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Supervisor;

namespace LinuxWebTool.Infrastructure.Features.Plugins.Broker;

/// <summary>
/// 插件动态服务发现与通用互调透明网关 (Plugin Broker & Invocation Gateway)
/// </summary>
public sealed class PluginBroker
{
    private static readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>
    /// 扫描并发现所有已安装插件暴露的标准服务元数据清单。
    /// </summary>
    public IReadOnlyList<ServiceDescriptorDto> DiscoverServices(
        IEnumerable<PluginManifest> manifests,
        IReadOnlyDictionary<string, PluginStateMachine> stateMachines,
        PluginPortManager portManager)
    {
        var descriptors = new List<ServiceDescriptorDto>();

        foreach (var manifest in manifests)
        {
            var isRunning = stateMachines.TryGetValue(manifest.Id, out var sm) && sm.State == PluginState.Running;
            var currentState = sm?.State ?? PluginState.Stopped;
            var assignedPort = portManager.GetPort(manifest.Id);
            var protocol = manifest.GetEffectiveProtocol();
            var lifecycle = manifest.GetEffectiveLifecycle();

            // 若显式声明了 Interface.Services
            if (manifest.Interface?.Services != null && manifest.Interface.Services.Count > 0)
            {
                foreach (var (svcName, svcDef) in manifest.Interface.Services)
                {
                    var endpoint = manifest.Interface.Endpoint;
                    if (string.IsNullOrWhiteSpace(endpoint))
                    {
                        endpoint = protocol switch
                        {
                            InterfaceProtocol.Http => assignedPort.HasValue ? $"http://127.0.0.1:{assignedPort.Value}" : null,
                            InterfaceProtocol.UnixSocket => manifest.Interface.SocketPath,
                            InterfaceProtocol.WasmAbi => manifest.Entrypoint,
                            _ => null
                        };
                    }

                    descriptors.Add(new ServiceDescriptorDto
                    {
                        Id = svcName,
                        PluginId = manifest.Id,
                        Name = svcDef.Description != null ? $"{manifest.Name} ({svcDef.Description})" : manifest.Name,
                        Profile = manifest.Profile,
                        Lifecycle = lifecycle,
                        Protocol = protocol,
                        State = currentState,
                        Port = assignedPort,
                        SocketPath = manifest.Interface.SocketPath,
                        HealthEndpoint = manifest.Interface.HealthEndpoint,
                        Endpoint = endpoint,
                        Actions = manifest.CustomActions?.Select(a => a.Action).ToList() ?? [],
                        Operations = svcDef.Operations != null && svcDef.Operations.Count > 0
                            ? svcDef.Operations
                            : (manifest.CustomActions?.Select(a => a.Action).ToList() ?? ["*"]),
                        PublishedEvents = manifest.Events?.Publishes ?? [],
                        Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["Description"] = svcDef.Description ?? manifest.Description,
                            ["Running"] = isRunning ? "true" : "false"
                        }
                    });
                }
            }
            else
            {
                // 隐式兼容推断导出
                var endpoint = protocol switch
                {
                    InterfaceProtocol.Http => assignedPort.HasValue ? $"http://127.0.0.1:{assignedPort.Value}" : null,
                    InterfaceProtocol.UnixSocket => manifest.Interface?.SocketPath,
                    InterfaceProtocol.WasmAbi => manifest.Entrypoint,
                    _ => null
                };

                var operations = new List<string>();
                if (manifest.CustomActions != null && manifest.CustomActions.Count > 0)
                {
                    operations.AddRange(manifest.CustomActions.Select(a => a.Action));
                }
                else
                {
                    operations.Add("run");
                }

                descriptors.Add(new ServiceDescriptorDto
                {
                    Id = manifest.Id,
                    PluginId = manifest.Id,
                    Name = manifest.Name,
                    Profile = manifest.Profile,
                    Lifecycle = lifecycle,
                    Protocol = protocol,
                    State = currentState,
                    Port = assignedPort,
                    SocketPath = manifest.Interface?.SocketPath,
                    HealthEndpoint = manifest.Interface?.HealthEndpoint,
                    Endpoint = endpoint,
                    Actions = manifest.CustomActions?.Select(a => a.Action).ToList() ?? [],
                    Operations = operations,
                    PublishedEvents = manifest.Events?.Publishes ?? [],
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Description"] = manifest.Description,
                        ["Running"] = isRunning ? "true" : "false"
                    }
                });
            }
        }

        return descriptors;
    }

    /// <summary>
    /// 通用透明互调网关，按协议分发并执行请求。
    /// </summary>
    public async ValueTask<PluginInvokeResponse> InvokeServiceAsync(
        PluginInvokeRequest request,
        PluginManifest manifest,
        string pluginDir,
        PluginStateMachine? stateMachine,
        PluginPortManager portManager,
        Func<PluginActionRequest, CancellationToken, ValueTask<PluginActionResult>> executeAction,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var protocol = manifest.GetEffectiveProtocol();

        try
        {
            switch (protocol)
            {
                case InterfaceProtocol.WasmAbi:
                {
                    var action = string.IsNullOrWhiteSpace(request.Operation) ? "run" : request.Operation;
                    var actionReq = new PluginActionRequest
                    {
                        PluginId = manifest.Id,
                        Action = action,
                        Param = request.Payload
                    };
                    var res = await executeAction(actionReq, ct);
                    sw.Stop();

                    return new PluginInvokeResponse
                    {
                        Success = res.Status == "succeeded",
                        StatusCode = res.Success && (res.ExitCode == null || res.ExitCode == 0) ? 200 : 500,
                        ContentType = "application/json",
                        Output = res.Message,
                        DurationMs = sw.ElapsedMilliseconds,
                        Logs = res.Logs != null ? new List<string>(res.Logs) : null
                    };
                }

                case InterfaceProtocol.Http:
                {
                    var port = portManager.GetPort(manifest.Id) ?? 0;
                    if (port <= 0)
                    {
                        sw.Stop();
                        return new PluginInvokeResponse
                        {
                            Success = false,
                            StatusCode = 503,
                            Output = $"插件 [{manifest.Id}] 未分配有效监听端口或尚未启动",
                            DurationMs = sw.ElapsedMilliseconds
                        };
                    }

                    var opPath = (request.Operation ?? "").TrimStart('/');
                    var targetUrl = $"http://127.0.0.1:{port}/{opPath}";

                    using var httpReq = new HttpRequestMessage(
                        string.IsNullOrWhiteSpace(request.Payload) ? HttpMethod.Get : HttpMethod.Post,
                        targetUrl);

                    if (!string.IsNullOrWhiteSpace(request.Payload))
                    {
                        httpReq.Content = new StringContent(
                            request.Payload,
                            Encoding.UTF8,
                            string.IsNullOrWhiteSpace(request.ContentType) ? "application/json" : request.ContentType);
                    }

                    if (request.Headers != null)
                    {
                        foreach (var (k, v) in request.Headers)
                        {
                            httpReq.Headers.TryAddWithoutValidation(k, v);
                        }
                    }

                    using var httpRes = await _httpClient.SendAsync(httpReq, ct);
                    var body = await httpRes.Content.ReadAsStringAsync(ct);
                    sw.Stop();

                    return new PluginInvokeResponse
                    {
                        Success = httpRes.IsSuccessStatusCode,
                        StatusCode = (int)httpRes.StatusCode,
                        ContentType = httpRes.Content.Headers.ContentType?.ToString() ?? "text/plain",
                        Output = body,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }

                case InterfaceProtocol.UnixSocket:
                {
                    var sockPath = manifest.Interface?.SocketPath;
                    if (string.IsNullOrWhiteSpace(sockPath))
                    {
                        sw.Stop();
                        return new PluginInvokeResponse
                        {
                            Success = false,
                            StatusCode = 400,
                            Output = "UnixSocket 协议插件未声明 interface.socketPath 路径",
                            DurationMs = sw.ElapsedMilliseconds
                        };
                    }

                    var fullSock = Path.GetFullPath(Path.Combine(pluginDir, sockPath));
                    if (!File.Exists(fullSock))
                    {
                        sw.Stop();
                        return new PluginInvokeResponse
                        {
                            Success = false,
                            StatusCode = 503,
                            Output = $"Unix 域套接字文件不存在: {sockPath}",
                            DurationMs = sw.ElapsedMilliseconds
                        };
                    }

                    using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    var endpoint = new UnixDomainSocketEndPoint(fullSock);
                    await socket.ConnectAsync(endpoint, ct);

                    var payloadBytes = Encoding.UTF8.GetBytes(request.Payload ?? "");
                    await socket.SendAsync(payloadBytes, SocketFlags.None, ct);

                    var buffer = new byte[8192];
                    var received = await socket.ReceiveAsync(buffer, SocketFlags.None, ct);
                    var responseStr = Encoding.UTF8.GetString(buffer, 0, received);
                    sw.Stop();

                    return new PluginInvokeResponse
                    {
                        Success = true,
                        StatusCode = 200,
                        ContentType = "text/plain",
                        Output = responseStr,
                        DurationMs = sw.ElapsedMilliseconds
                    };
                }

                case InterfaceProtocol.CliArgs:
                default:
                {
                    var action = string.IsNullOrWhiteSpace(request.Operation) ? "run" : request.Operation;
                    var actionReq = new PluginActionRequest
                    {
                        PluginId = manifest.Id,
                        Action = action,
                        Param = request.Payload
                    };
                    var res = await executeAction(actionReq, ct);
                    sw.Stop();

                    return new PluginInvokeResponse
                    {
                        Success = res.Status == "succeeded",
                        StatusCode = res.Success && (res.ExitCode == null || res.ExitCode == 0) ? 200 : 500,
                        Output = res.Message,
                        DurationMs = sw.ElapsedMilliseconds,
                        Logs = res.Logs != null ? new List<string>(res.Logs) : null
                    };
                }
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new PluginInvokeResponse
            {
                Success = false,
                StatusCode = 500,
                Output = $"调用插件服务异常: {ex.Message}",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }
}
