# 家庭网关 L4 端口转发失效根本原因深度调研与系统级重构方案

## 1. 现象与问题定义

用户在使用 `ghcr.io/csvkse/lwt:latest` 容器镜像部署 `LinuxWebTool` 时，通过 Web 控制台配置了 L4 端口转发规则：
- **规则名称**：16002
- **协议**：TCP
- **监听端口**：`:10000`
- **转发目标**：`10.126.1.77:5270`
- **页面状态**：显示绿色 `● 监听中`
- **容器启动指令**：
  ```bash
  sudo docker run -d \
    --name linuxwebtool \
    --restart unless-stopped \
    --privileged \
    --pid=host \
    --user root \
    --device /dev/fuse \
    --device=/dev/net/tun \
    -p 5270:5270 \
    --net=host \
    -v linuxwebtool-data:/app/data \
    -v /lwt:/lwt:rshared \
    -v /usr/local/bin:/usr/local/bin:ro \
    ghcr.io/csvkse/lwt:latest
  ```

**故障现象**：
客户端在浏览器中访问 `http://10.126.1.77:10000` 时，浏览器直接报错：
`无法访问此页面 10.126.1.77 拒绝连接。 ERR_CONNECTION_REFUSED`。

---

## 2. 深度调研：根本原因全景矩阵分析

通过全面排查源码、容器网络模型、套接字生命周期与 Linux 操作系统内核行为，确认该故障由 **五大维度技术隐患与设计缺陷** 叠加引起：

### 维度一：容器网络与宿主机防火墙边界（Docker Networking & Host Firewall）

1. **`-p 5270:5270` 与 `--net=host` 混用背后的网络拓扑陷阱**：
   - 用户在运行容器时同时指定了 `-p 5270:5270` 和 `--net=host`。
   - **Docker 原生 Linux 场景**：Docker CLI 会输出警告 `WARNING: Published ports are discarded when using host network mode`，`-p` 参数被完全忽略，容器直接绑定宿主网络栈。
   - **Docker Desktop (Windows/macOS) 或部分轻量虚拟化环境**：`--net=host` 实际上共享的是 WSL2/HyperKit 内部虚拟机的网络栈，**并不直接暴露到宿主机物理网卡 (`10.126.1.77`)**！此时只有通过 `-p 5270:5270` 显式由 `docker-proxy` 映射出来的端口能在 Windows 物理宿主访问，而后续在网关 UI 中动态新增的 `10000` 端口根本没有被 docker-proxy 映射，导致外部访问 `10000` 端口时物理机直接回送 TCP RST（`ERR_CONNECTION_REFUSED`）。
   - **Docker Bridge 模式误用**：若用户或管理平台（Portainer/1Panel/Synology）以默认 Bridge 模式启动容器，容器内部新开启的端口 `10000` 对宿主机及外网完全不可见。
2. **宿主机 UFW / firewalld 防火墙拦截（最常见的生产真实现象）**：
   - 当使用 Docker Bridge 模式（`-p`）时，Docker daemon 会自动在宿主机 iptables 的 `PREROUTING` 与 `DOCKER` 链中注入放行规则，直接绕过宿主机的 UFW（Ubuntu 默认防火墙）。
   - **但当启用 `--net=host` 时，Docker 不会接管 iptables！** 流量直接由宿主机内核网络栈处理。如果 Ubuntu 宿主机开启了 UFW（`ufw status` 为 active），且默认策略为 `DROP` 或 `REJECT`，宿主机将直接向访问 `10000` 端口的 SYN 包回送 TCP RST（`REJECT with tcp-reset`），从而在客户端表现为立竿见影的 `ERR_CONNECTION_REFUSED`！

---

### 维度二：套接字 IPv4/IPv6 协议栈绑定缺陷（Dual-Stack Binding Deficiency）

在原 `TcpProxyEngine.cs` 与 `UdpProxyEngine.cs` 实现中：
```csharp
// 原代码: 仅绑定纯 IPv4 Any (0.0.0.0)
var listener = new TcpListener(IPAddress.Any, port);
listener.Start();
```
1. **缺失 IPv6 双栈支持**：
   - `IPAddress.Any` 即 `0.0.0.0`，严格限制为 IPv4。
   - 现代 Linux 系统、现代浏览器（Chrome Happy Eyeballs / RFC 8305）以及各种 DNS 解析环境，优先发起 IPv6 连接或双栈回环。若系统通过 IPv6 解析目标或监听，IPv4 监听器完全无法接收 IPv6 数据报。
2. **端口占用与冲突静默失效**：
   - 若系统已存在以 IPv6 监听该端口的守护进程（或 `bindv6only=0` 共享模式），尝试绑定 `0.0.0.0` 将抛出 `SocketException: Address already in use`。
   - 微软 .NET 官方最佳实践明确要求使用 `TcpListener.Create(port)`：该 API 默认绑定 `IPAddress.IPv6Any` 并启用 `DualMode = true`，使单一 Socket 能同时处理 IPv4（以 IPv4-mapped IPv6 形式接收）与 IPv6 请求，并在 IPv6 不可用时安全降级。

---

### 维度三：UI 与后端状态“假阳性”欺骗（State Desynchronization & Silent Failure）

1. **前端状态假阳性**：
   在 `GatewayView.js` 中：
   ```html
   <span class="badge" :class="t.isEnabled ? '... text-emerald-300' : '... text-slate-400'">
     {{ t.isEnabled ? '● 监听中' : '● 已停止' }}
   </span>
   ```
   前端显示的 `● 监听中` **仅仅取决于数据库中 `IsEnabled` 字段是否为 true**！
   它完全没有向后端查询真实的套接字监听状态。即使 `listener.Start()` 抛出异常（如端口被占、权限不足、端口范围非法），数据库依然保持 `IsEnabled=1`，前端界面依然骄傲地亮起绿灯 `● 监听中`！用户误以为端口已就绪，排查完全无从下手。
2. **后端无状态查询与自愈**：
   `GET /api/Gateway/TcpRoutes` 仅简单查询 SQLite 表 `gateway_tcp_route`，没有任何字段反映当前 Socket 是否真正 Listen、是否存在错误信息、有多少活跃连接。

---

### 维度四：转发管道生命周期与异常 RST 丢弃（Relay Lifecycle & Unhandled Teardown）

在原 `ForwardClientAsync` 中：
```csharp
private async Task ForwardClientAsync(Socket clientSocket, string forwardHost, int forwardPort, CancellationToken token)
{
    using var client = clientSocket;
    using var upstream = new Socket(SocketType.Stream, ProtocolType.Tcp);
    try
    {
        // 1. 尝试连接上游目标 (10.126.1.77:5270)
        await upstream.ConnectAsync(forwardHost, forwardPort, connectCts.Token);
        ...
        await Task.WhenAll(clientToUpstream, upstreamToClient);
    }
    catch (Exception ex)
    {
        // 连接失败直接退出
    }
}
```
1. **上游连接失败引发客户端异常重置 (TCP RST)**：
   - 当客户端连接 `:10000` 时，系统内核 `AcceptSocketAsync` 先完成了 TCP 三次握手（SYN -> SYN-ACK -> ACK）。
   - 紧接着，客户端（浏览器）立刻发送了 HTTP 请求首部（`GET / HTTP/1.1...`），此时该数据已驻留在客户端 Socket 的接收缓冲区中。
   - 此时转发引擎尝试连接上游 `10.126.1.77:5270`。如果上游连接失败（超时、目标拒绝、DNS 无法解析），代码执行到 `catch` 块并退出方法。
   - `using var client = clientSocket;` 触发 `client.Dispose()`。
   - **在 BSD / Linux TCP 规范中：当关闭一个接收缓冲区中尚有未读取数据的 Socket 时，内核不会发送正常的 FIN 包，而是直接发送 TCP RST 报文！**
   - 浏览器收到 RST 报文，可能直接向用户报错 `ERR_CONNECTION_RESET` 乃至在某些握手窗口表现为 `ERR_CONNECTION_REFUSED`。
2. **未指定 AddressFamily 的 Socket 构造缺陷**：
   - `new Socket(SocketType.Stream, ProtocolType.Tcp)` 依赖运行时的默认推断；若连接 IPv4 字面量或 DNS 结果，缺乏多 IP 重试（Fallback）能力。
3. **`Task.WhenAll` 的半关闭悬挂隐患**：
   - TCP 是全双工的。当一端发送 FIN（例如客户端发送完毕），其对应的方向应该关闭写入（Half-Close），但仍应等待对端把剩余响应发回。`Task.WhenAll` 会在异常或非对称关闭时产生长时间卡死。业界均采用 `Task.WhenAny` 搭配主动取消传播。

---

### 维度五：回环与自身 IP 回环路由（Hairpin Routing & Target Selection）

用户配置的目标是 `10.126.1.77:5270`（本机物理 IP 上的本应用自身端口）：
- 在 Linux 中，当一个进程尝试连接本机物理网卡的外部 IP 时，内核通过 `lo` 接口路由。但在容器环境或开启了反向路径过滤（`rp_filter`）的机器上，发往自身物理 IP 的流量可能被防火墙标记为 Martian Packet（火星包）而被丢弃。
- 用户代理自身管理面板时，转发到 `127.0.0.1:5270` 是最稳健的路径，但系统缺乏目标连通性预检能力，无法在用户输入配置时就给出诊断提示。

---

## 3. 业界主流方案调研与对比

针对四层（L4 TCP/UDP）端口转发与负载均衡网关，深入对比业界主流方案的技术实现：

| 方案 | 技术原理 | 优点 | 缺点 / 限制 | 借鉴与采纳点 |
|---|---|---|---|---|
| **HAProxy** (`mode tcp`) | Linux epoll 事件驱动 + `splice()` 零拷贝管道 | 工业级极值性能，毫秒级健康检查，极其详尽的运行时状态仪表盘 | C 语言编写，作为嵌入式组件难以与 C# 统一编译与单体分发 | **完全借鉴其双向 Half-Close 状态机与详尽的 Runtime Status（连接数、字节流、错误状态）设计** |
| **Nginx Stream** (`ngx_stream_core_module`) | 异步非阻塞事件驱动，支持 `listen [::]:port` 双栈与 upstream 轮询 | 配置简单，模块成熟，支持动态 DNS 解析 | 动态热更新需要 Reload 进程或购买商业版 Nginx Plus API | **借鉴其双栈监听配置与 upstream connect_timeout 快速失败熔断机制** |
| **Envoy Proxy** | C++ 编写，基于 LDS (Listener Discovery) 的动态 L4 Filter 链 | 云原生标准，极强的观测性与动态 API 配置 | 内存占用较高（>100MB），不适合轻量级单容器边缘工具 | **借鉴其动态 Listener 管理与状态观测模型** |
| **Linux IPVS / iptables** (内核态) | 内核 Netfilter / IPVS 表，PREROUTING DNAT | 真正的内核态 0 拷贝，极高吞吐 | 依赖容器必须持有 `NET_ADMIN` 特权，无法在跨平台（Windows 开发环境）运行，缺乏应用层观测与统计 | 作为底层机制可供参考，但业务网关仍需用户态可观测套接字 |
| **.NET BedrockFramework / Kestrel ConnectionHandler** | 微软官方团队出品，基于 `System.IO.Pipelines` + Socket 双工管道 | .NET 体系最高性能，完全兼容 Native AOT，零内存分配 | 依赖完整的 Kestrel 管线，偏重量级 | **借鉴其基于 PipeReader/PipeWriter 或 ArrayPool Memory 的零分配双向 Pump 模式** |
| **ProxyYARP** (本地同类参考项目) | 基于原生 Socket + ArrayPool + `SafeClose` 优雅关闭 | 纯 C# 原生，100% 兼容 Native AOT，轻量无第三方重量依赖 | 原版同样缺乏双栈监听与前台真实探针反馈 | **直接吸纳其优雅关闭机制与双向转发优化，并补充双栈、探针与实时状态观测能力** |

---

## 4. 可用库与代码借鉴

1. **.NET BCL `System.Net.Sockets.TcpListener.Create(port)`**：
   - 官方推荐的双栈工厂方法，内部封装了 `IPAddress.IPv6Any` 与 `DualMode = true`，自动支持 IPv4 与 IPv6。
   - 当操作系统环境禁用 IPv6 时，提供捕获 `SocketError.AddressFamilyNotSupported` 并回退至 `IPAddress.Any` 的鲁棒容灾能力。
2. **`ProxyYARP.Proxy.Tcp.TcpProxyEngine`**：
   - 借鉴其 `SafeClose`（显式执行 `Shutdown(SocketShutdown.Both)` 后关闭）、`Task.WhenAny` 并发退出以及 `ArrayPool<byte>` 高性能双向 Pump 模型。
3. **`System.Net.Sockets.Socket.ConnectAsync` + 多 IP 串行降级尝试**：
   - 针对目标主机（可能是域名、IPv4 或 IPv6），利用 `Dns.GetHostAddressesAsync` 获取所有候选 IP，循环连接直到第一个成功，超时短设为 3~5 秒，避免单个不可达 IP 阻塞整个客户端连接。

---

## 5. 具体解决方案与系统级重构

### 5.1 解决方案架构

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                            GatewayController                                │
│   ├─ GET /api/Gateway/TcpRoutes      ──▶ 合并持久化配置与 Engine 真实运行状态   │
│   └─ POST /api/Gateway/TcpRoutes/TestTarget ──▶ 主动发起目标连通性预检测试   │
└──────────────────────────────────────┬──────────────────────────────────────┘
                                       │
                      ┌────────────────┴────────────────┐
                      ▼                                 ▼
         【TcpProxyEngine (TCP)】             【UdpProxyEngine (UDP)】
   • TcpListener.Create(port) 双栈监听    • 双栈与多 IP 目标绑定
   • SemaphoreSlim 防并发重入锁            • 活跃会话跟踪与自动淘汰
   • SafeClose 优雅 Half-Close 挥手       • 真实运行状态字典暴露
   • ExecuteAsync 定时巡检自愈(Auto-Heal) • 活跃连接数与吞吐度量
   • 捕获启动异常并持久化到内存状态池
                      │
                      ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                           GatewayView.js 前端重构                           │
│   • 真实状态展示: 绿灯 [监听中] / 红灯 [监听失败: 错误详情] / 灰灯 [已停止] │
│   • 新增 [测试目标连接] 按钮: 保存前主动探活 10.126.1.77:5270，拒绝盲盒配置 │
│   • 容器环境提示横幅: 显式提示 --net=host 与宿主机 UFW 防火墙放行命令        │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 5.2 实施步骤

1. **增强 Contracts 契约模型** (`GatewayModels.cs`)：
   - `GatewayTcpRouteItem` 扩充实时状态字段：`bool IsRunning`, `string? LastError`, `int ActiveConnections`, `string? BoundEndpoint`。
   - 新增连通性探测请求与响应模型：`TestL4TargetRequest` 与 `TestL4TargetResponse`。
2. **重构 `TcpProxyEngine.cs`**：
   - **双栈监听支持**：使用 `TcpListener.Create(port)` 优先监听双栈，捕获不可用异常后自动降级到 `IPAddress.Any`。
   - **真实状态存储**：内部维护 `ConcurrentDictionary<int, L4ListenerStatus>`，包含 `IsRunning`、`LastError`、`ActiveConnections`。
   - **优雅连接关闭**：实现 `SafeClose`，在客户端退出或上游失败时规范执行 `Shutdown(SocketShutdown.Both)`，防止内核误发 RST。
   - **转发管道优化**：改用 `Task.WhenAny`，一端断开立即取消对端，释放资源。
   - **并发锁与定时巡检自愈**：引入 `SemaphoreSlim` 保证热重载原子性；后台巡检线程每 10 秒对失败的规则尝试重新绑定（自愈）。
3. **重构 `UdpProxyEngine.cs`**：
   - 同样暴露运行时状态并维护 `IsRunning` 与 `LastError`。
4. **扩展 `GatewayController.cs`**：
   - `GetTcpRoutes` 动态汇入 `TcpProxyEngine` 与 `UdpProxyEngine` 的实时运行状态返回给前端。
   - 新增 `POST /api/Gateway/TcpRoutes/TestTarget` 端点，支持在界面中输入目标（如 `10.126.1.77:5270`）后立即探测 TCP/UDP 连通性并返回往返延迟。
5. **升级 `GatewayView.js` 视图**：
   - 彻底废除假阳性 `t.isEnabled ? '● 监听中'`，根据 `t.isRunning` 与 `t.lastError` 渲染真实红绿状态。
   - 在添加/编辑对话框中新增「测试目标连通性」操作按钮。
   - 增加容器宿主网络与防火墙运维排查引导卡片（`sudo ufw allow <port>` 说明）。

---

## 6. 同类门禁与避免回归测试体系

为了杜绝此类“假绿灯、无状态、双栈缺失、连接静默重置”问题在未来的版本中再次发生，建立以下门禁机制与自动化测试：

### 6.1 架构门禁规则 (`LinuxArch025`)
在 `docs/dev/architecture-gates.md` 中新增诊断规则：
- **编号**：`LinuxArch025`
- **含义**：**L4 网络服务必须具备双栈监听容灾机制、实时运行状态暴露与优雅关闭保障**。
- **强制方式**：xUnit 架构与单元测试。生产网络监听引擎必须支持状态查询接口，禁止在前端仅凭配置标志推断运行状态。

### 6.2 自动化测试用例矩阵 (`GatewayUnitTests.cs`)
1. **双栈监听与容灾降级测试 (`TcpProxyEngine_DualStack_And_Fallback`)**：验证系统在支持 IPv6 的环境下成功建立双栈监听，在不支持的环境下优雅降级。
2. **端口冲突与错误捕获测试 (`TcpProxyEngine_Port_Conflict_Reports_LastError`)**：人为预先占用端口，启动转发规则，断言引擎记录 `IsRunning == false` 且包含清晰的 `Address already in use` 错误描述，而非抛出未处理异常。
3. **目标探活端点测试 (`GatewayController_TestTarget_Reachability`)**：验证目标连通性测试 API 对开放端口返回成功及延迟，对关闭端口返回明确的拒连或超时信息。
4. **真实运行时状态合并测试 (`Gateway_TcpRoutes_Includes_Runtime_Status`)**：验证 API 返回的列表正确映射引擎中的活跃连接数与运行标识。

---

## 7. 用户现场处置与修复指引（针对当前容器）

针对用户当前运行的容器，可按照以下两步快速定位并恢复：

1. **检查宿主机 UFW 防火墙（最主要外部原因）**：
   ```bash
   # 查看当前防火墙状态
   sudo ufw status
   # 若防火墙处于 active 状态，必须显式放行 10000 端口（因为 --net=host 绕过了 Docker 的自动放行规则）
   sudo ufw allow 10000/tcp
   ```
2. **检查端口是否在宿主机上真实监听**：
   ```bash
   sudo ss -tulpn | grep 10000
   ```
   若输出中无 10000 端口，在更新本补丁镜像后，网关后台将直接用红字高亮显示具体绑定失败的原因（如已被其他进程占用）。
3. **若在 Windows/macOS 的 Docker Desktop 运行**：
   必须在 `docker run` 命令中补齐 `-p 10000:10000` 端口映射，因为非 Linux 平台的 `--net=host` 无法直通物理宿主机网卡。
