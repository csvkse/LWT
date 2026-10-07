# 家庭网关与 FRP 反向穿透全流程代码审计与优化实施方案

## 1. 方案背景与审计目标

LinuxWebTool 系统内置了**家庭智能网关**（涵盖 L4 TCP/UDP 端口转发与 L7 YARP 动态反向代理/即席网站代理）与 **FRP 反向穿透**（基于 HTTP-over-WebSocket 的多路复用与流式透传，对接 Cloudflare DO 边缘网关）。

为了确保家庭环境高负载（高并发、高码率 4K/原盘流媒体串流、长时间稳定运行）下的绝对可靠性，针对网关和穿透全链路代码进行了深入审计，识别并消除所有潜在的**逻辑漏洞**、**生命周期竞态**、**异常崩溃隐患**与**内存/GC 性能瓶颈**。

---

## 2. 核心审计发现与问题清单

### 2.1 P0 级严重缺陷

1. **FRP 冗余后台引擎并发竞争（死循环互踢掉线）**
   * **位置**：`src/LinuxWebTool.WebHost/Composition/ServiceCollectionExtensions.cs`
   * **现象**：系统在演进为多线路 `FrpTunnelManager` 后，旧单线路引擎 `FrpTunnelEngine` 与 `FrpTunnelService` 仍作为 `HostedService` 注册并自启动。
   * **危害**：两套引擎同时以相同的 `TunnelHost` 连接边缘网关，触发 Cloudflare DO 网关的 `4002 Displaced` 互踢机制，产生永无休止的“上线 -> 被踢 -> 重连 -> 踢掉对方”恶性循环。
   * **解决**：彻底注销移除 `FrpTunnelService` 托管服务注册，保留统一的 `FrpTunnelManager`。

2. **L4 UDP 代理在 IPv6 目标下的 `ArgumentException` 致命崩溃**
   * **位置**：`src/LinuxWebTool.Infrastructure/Gateway/UdpProxyEngine.cs:279`
   * **现象**：在 `UdpSession.Start()` 中，向 `BackendSocket.ReceiveFromAsync` 传入写死的 IPv4 端点 `new IPEndPoint(IPAddress.Any, 0)`。
   * **危害**：当转发目标被解析为 IPv6 地址时，`BackendSocket` 为 `AddressFamily.InterNetworkV6`，.NET 立即抛出 `ArgumentException: The AddressFamily InterNetwork of the EndPoint is not valid for this Socket, use InterNetworkV6 instead`，直接终止 UDP 会话的响应接收循环，导致所有 IPv6 UDP 回包丢失。
   * **解决**：根据 `BackendSocket.AddressFamily` 自适应传入匹配的接收端点（IPv6Any / Any）。

3. **L7 根通配路由劫持后台管理界面与 SPA 页面**
   * **位置**：`DatabaseProxyConfigProvider.cs` & `GatewayController.cs` & `WebsiteProxyTransformProvider.cs`
   * **现象**：当用户添加 L7 路由规则为 `MatchPath = "/{**catchall}"`（或 `"/*"`）且未指定 `MatchHosts` 限制时，YARP 路由优先级抢占了所有请求（包括 SPA 前端静态文件回退 `MapFallbackToFile` 及后台 `/api/*` 接口）。
   * **危害**：配置后后台页面无法打开、接口返回 404 或被转发至后端，造成“配置网站代理后 后台管理无法再访问”。
   * **解决**：
     * `DatabaseProxyConfigProvider` 对未限定 Host 的规则自动排除系统保留路径（`/app/`, `/api/`, `/health`, `/scalar/`, `/openapi/` 等）。
     * `GatewayController` 保存路由时，强制校验全局根通配必须指定域名（`MatchHosts`）。
     * `WebsiteProxyTransformProvider` 确保对系统自身保留路径不接管。

---

### 2.2 P1 级重要性能与生命周期优化

1. **FRP 视频流式推流 `chunkMsg` 内存分配削减（零分配改造）**
   * **位置**：`src/LinuxWebTool.Infrastructure/Tunnel/FrpTunnelInstance.cs:883`
   * **现象**：每个 32KB 分片均重新 `new byte[2 + idLen + bytesRead]`。在 80Mbps 高码率 4K 流下，每秒产生约 320 次堆分配，托管堆垃圾超 10.5 MB/s，引发密集的 Gen 0 GC 停顿与卡顿抖动。
   * **解决**：改用 `ArrayPool<byte>.Shared.Rent` 租用缓冲区，发送完成后立即归还，将推流内存分配降至 **0 B**。

2. **L4 TCP 代理延迟优化（Nagle 算法与半关闭完善）**
   * **位置**：`src/LinuxWebTool.Infrastructure/Gateway/TcpProxyEngine.cs`
   * **现象**：
     * 未设置 `NoDelay = true`，Nagle 算法导致 SSH/API 等小报文产生高达 40ms 延迟。
     * `Task.WhenAny` 在客户端关闭写端（发送 FIN）时直接释放双向 Socket，掐断了上游正在传输的最后一部分响应。
     * 上游连接缺少超时熔断，目标宕机时阻塞 20~30 秒。
   * **解决**：
     * 开启 `client.NoDelay = true; upstream.NoDelay = true;`。
     * 当客户端单向读到 EOF 时，向上游发送 `Shutdown(SocketShutdown.Send)` 半关闭信号并等待上游响应传输完毕。
     * 引入 5 秒连接超时。

---

### 2.3 P2 级体验与鲁棒性增强

1. **L4 UDP 代理纯 IP 快路跳过与 DNS 缓存**
   * **位置**：`src/LinuxWebTool.Infrastructure/Gateway/UdpProxyEngine.cs:172`
   * **现象**：遇到新客户端每次均执行异步 DNS 解析。
   * **解决**：优先使用 `IPAddress.TryParse` 走纯 IP 快路；域名解析结果进行本地 TTL 缓存。

2. **FRP 滑动窗口流控超时自愈额度调优**
   * **位置**：`src/LinuxWebTool.Infrastructure/Tunnel/TunnelRequestSession.cs:95`
   * **现象**：在远端 ACK 网络抖动超时后，默认自愈补充额度仅 512KB，导致流速被硬限制在 16Mbps (2MB/s)。
   * **解决**：将超时自愈补充额度提升至 1MB ~ 2MB，保障 4K 原盘流在偶发丢 ACK 时依然平滑流畅播放。

3. **STRM 302 重定向直链缓存主动淘汰**
   * **位置**：`src/LinuxWebTool.Infrastructure/Tunnel/MediaStreamCoordinator.cs`
   * **现象**：缓存条目仅被动过期。
   * **解决**：引入最大容量（如 2000 条）防溢出淘汰机制。

---

## 3. 详细实施计划

1. **Step 1**: 更新 `ServiceCollectionExtensions.cs`，移除遗留单线路引擎 `FrpTunnelService`，解除互踢隐患。
2. **Step 2**: 改造 `UdpProxyEngine.cs`，修复 IPv6 接收端点崩溃，增加纯 IP 快路与域名缓存。
3. **Step 3**: 改造 `TcpProxyEngine.cs`，开启 `NoDelay`，增加 5 秒连接超时与优雅半关闭。
4. **Step 4**: 优化 `DatabaseProxyConfigProvider.cs` 和 `GatewayController.cs`，为全局根通配路由增加保留路径白名单保护与域名绑定强校验。
5. **Step 5**: 优化 `FrpTunnelInstance.cs`，引入 `ArrayPool<byte>.Shared` 消除 `chunkMsg` 堆分配，优化推流 GC。
6. **Step 6**: 调优 `TunnelRequestSession.cs` 与 `MediaStreamCoordinator.cs`，优化流控超时自愈与缓存容量。
7. **Step 7**: 执行完整自动化测试套件（Architecture & Integration Tests），验证全部功能正常。
