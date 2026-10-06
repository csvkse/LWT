# ProxyByCF FRP 客户端集成技术调研与实施方案

## 1. 目标与背景

将 `ProxyByCF` 的 FRP 内网反向穿透客户端核心功能原生集成至 `LinuxWebTool`，使宿主服务在无需公网 IPv4 / IPv6、无需配置路由器端口映射与 NAT 穿透的前提下，通过轻量安全的长连接隧道映射至 Cloudflare 边缘网关或自定义域名。并在 Web 控制台中提供完整的隧道配置、实时状态监控、一键启动/停止与穿透访问引导。

---

## 2. 调研对象与协议深度解析

参考来源：`E:\WorkProject\Node\_CFWorkerProject\ProxyByCF\tools\frp`（`@vkse/proxybycf-tunnel`）。

### 2.1 协议拓扑与握手模型

ProxyByCF FRP 采用 **HTTP-over-WebSocket** 复合双向多路复用协议：
- **边缘网关**：Cloudflare Worker 边缘节点，监听用户公网入口及 `/tunnel/connect` WebSocket 汇聚端点。
- **本地客户端**：建立出站单向 WebSocket 长连接至网关，利用反向流承载入站 HTTP 请求。

```
[ 外网访客 ]
     │ HTTP 请求 (https://gw.com/tunnel/{host}/...)
     ▼
[ Cloudflare Edge Worker ]
     │ 匹配隧道映射，将 HTTP 请求封装为帧
     │ WebSocket (WSS) 双向长连接
     ▼
[ LinuxWebTool 内部 FRP 引擎 (C# Native) ]
     │ 内存/本地环回转发 (http://127.0.0.1:8080/...)
     ▼
[ LinuxWebTool 核心 API / 终端 / 挂载 / Web 界面 ]
```

### 2.2 握手与鉴权规范

- **连接端点**：`wss://<gateway>/tunnel/connect?host=<tunnel_host>&key=<api_key>&token=<api_key>`
- **请求标头**：`x-pyw-token: <api_key>`
- **权限校验**：网关验证该 APIKey 是否具备 `frp_tunnel` 作用域权限；未授权直接以 HTTP 401/403 拒绝 WebSocket 升级。

### 2.3 报文帧格式规范

隧道内部采用 JSON 控制报文与二进制数据切片混合编解码机制：

| 帧类型 | 方向 | 报文格式 | 说明 |
|---|---|---|---|
| `TUNNEL_CONNECTED` | 网关 → 客户端 | JSON: `{ type, host, pathModeUrl, connectedAt }` | 握手就绪确认，返回公网分配路径 |
| `ping` / `pong` | 双向保活 | JSON: `{ type: "ping"/"pong", timestamp }` | 15s 周期保活，规避 CF 边缘 100s 空闲中断 |
| `HTTP_REQUEST` | 网关 → 客户端 | JSON: `{ type, requestId, method, path, headers, body }` | 外部访客入站 HTTP 请求，body 为 Base64 |
| `HTTP_RESPONSE` | 客户端 → 网关 | JSON: `{ type, requestId, status, headers, body }` | 快速单包响应（小文件、非流式、204/304） |
| `HTTP_RESPONSE_START` | 客户端 → 网关 | JSON: `{ type, requestId, status, headers }` | 大响应/流媒体首包头 |
| **二进制数据分片** | 客户端 → 网关 | **Binary Frame**（见下文协议结构） | 零拷贝二进制数据切片，避免 Base64 膨胀 |
| `HTTP_RESPONSE_END` | 客户端 → 网关 | JSON: `{ type, requestId }` | 流式传输正常结束标识 |
| `HTTP_ABORT` | 网关 → 客户端 | JSON: `{ type, requestId }` | 访客取消拉流、跳转或断开连接 |
| `HTTP_ACK` | 网关 → 客户端 | JSON: `{ type, requestId, bytes }` | 边缘下行消费确认，用于流控额度补充 |

#### 二进制切片帧结构 (Binary Slice Framing)

```
0                   1                   2                   3
0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1 2 3 4 5 6 7 8 9 0 1
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| 0x01 (分片标识) | ReqIdLen (1B) | Request ID UTF-8 字节串...   |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
| Payload 实际数据分片 (通常 64KB ~ 128KB) ...                     |
+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+-+
```

### 2.4 关键防御与韧性机制

1. **防同名互踢死循环 (Code 4002 / DISPLACED 保护)**：
   若另一实例以相同 `host` 上线，网关以状态码 `4002` 关闭当前连接。客户端必须捕获该状态码并进入长周期退避状态（15s 以上冷却），防止两个进程互相顶替导致网关出现 Ping-Pong 抖动。
2. **逐跳标头剔除 (Hop-by-Hop Headers)**：
   严格过滤 `connection`, `keep-alive`, `transfer-encoding`, `upgrade`, `proxy-authorization` 等，重写 `x-forwarded-host` 与 `x-forwarded-proto: https`。
3. **僵尸连接自愈探针**：
   若超过 2.5 个心跳周期（75s）未收到任何 `pong` 回包，主动销毁底层 Socket 并触发指数退避重连。

---

## 3. 可行性调研与实现选型

### 3.1 方案对比

| 评估维度 | 方案 A：子进程调用 Node.js CLI (`npx`) | 方案 B：.NET 10 原生 C# 嵌入式引擎 (推荐) |
|---|---|---|
| **运行时依赖** | 宿主机必须安装 Node.js 与 npm/npx 环境 | **零外部依赖**，直接编译进单一 Native AOT 二进制 |
| **内存与资源开销** | 额外消耗 V8 引擎内存（~40MB ~ 70MB RAM） | **极轻量**，仅消耗 3MB ~ 5MB RAM |
| **进程生命周期管控** | 跨进程 PID 管理、僵死子进程回收困难 | `IHostedService` 原生生命周期绑定，随程序优雅退出 |
| **网络转发效率** | 经由本地 TCP 端口进行中转，多一跳操作系统套接字 | 支持内部直接内存转发或高性能 `SocketsHttpHandler` |
| **Native AOT 契合度** | 不符合项目 Native AOT 纯单文件交付愿景 | **100% 纯 AOT 兼容**（使用 BCL `ClientWebSocket`） |

**结论**：坚决采用 **方案 B：C# 原生嵌入式隧道引擎**。

---

## 4. 架构与组件设计

```
┌─────────────────────────────────────────────────────────────┐
│                       Web 控制台 (Vue 3)                     │
│   FrpView.js: 状态大盘 / 配置表单 / 实时日志 / 公网访问入口   │
└──────────────────────────────┬──────────────────────────────┘
                               │ REST API
┌──────────────────────────────▼──────────────────────────────┐
│                    FrpTunnelController                      │
│   GET / Config, PUT / Config, POST / Start, POST / Stop     │
└──────────────────────────────┬──────────────────────────────┘
                               │
┌──────────────────────────────▼──────────────────────────────┐
│                    FrpTunnelService                         │
│  (IHostedService 后台托管服务 + 单例隧道生命周期协调器)     │
├─────────────────────────────────────────────────────────────┤
│  ┌───────────────────────┐       ┌───────────────────────┐  │
│  │    FrpTunnelEngine    │ ◄───► │   FrpTunnelConfigStore│  │
│  │ (有限状态机/WebSocket)│       │     (SQLite 持久化)   │  │
│  └───────────┬───────────┘       └───────────────────────┘  │
│              │                                              │
│  ┌───────────▼───────────┐       ┌───────────────────────┐  │
│  │     FrpFrameCodec     │       │   LocalForwardDispatcher│
│  │ (JSON / Binary 分片)  │ ◄───► │  (HttpClient/本地分发) │  │
│  └───────────────────────┘       └───────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

### 4.1 核心组件职责

1. **`FrpTunnelConfigStore`**（持久化）：
   - 管理 SQLite 表 `frp_tunnel_config`。
   - 读取、更新隧道连接配置与自启开关。
2. **`FrpTunnelEngine`**（连接与状态机引擎）：
   - 维护状态枚举：`Disconnected`, `Connecting`, `Connected`, `Reconnecting`, `Displaced`, `Stopped`。
   - 管理 `ClientWebSocket` 连接生命周期、心跳调度与指数退避重试。
3. **`FrpFrameCodec`**（协议编解码器）：
   - 基于 `System.Text.Json` 源生成器处理文本控制帧。
   - 使用 `ReadOnlyMemory<byte>` 和 `ArrayPool<byte>` 高效封装与解构二进制流分片。
4. **`LocalForwardDispatcher`**（本地转发器）：
   - 收到 `HTTP_REQUEST` 帧后，重写请求标头，使用内部 `HttpClient` 派发至本地监听地址（默认 `http://127.0.0.1:{Port}`）。
   - 将上游响应以流式分片回送至 WebSocket 发送信道。
5. **`FrpTunnelService`**（后台服务）：
   - 实现 `IHostedService`。若配置开启 `AutoStart`，在系统启动完成并开放监听后异步激活穿透。

---

## 5. 数据存储与 SQLite DDL

在 `DbSetup.cs` 中增加表 `frp_tunnel_config`：

```sql
CREATE TABLE IF NOT EXISTS frp_tunnel_config (
  Id TEXT PRIMARY KEY,
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
```

---

## 6. REST API 设计规范

控制器：`LinuxWebTool.WebHost.Routes.FrpTunnelController`，基础路由 `/api/FrpTunnel`。

### 6.1 接口列表

| 路径 | 方法 | 鉴权要求 | 说明 |
|---|---|---|---|
| `/api/FrpTunnel/Config` | `GET` | 管理员 | 获取当前穿透配置（敏感 Key 脱敏） |
| `/api/FrpTunnel/Config` | `PUT` | 管理员 | 更新穿透配置 |
| `/api/FrpTunnel/Status` | `GET` | 管理员 / APIKey | 获取实时连接状态、公网访问地址、已连接时长 |
| `/api/FrpTunnel/Start` | `POST` | 管理员 | 手动启动反向穿透长连接 |
| `/api/FrpTunnel/Stop` | `POST` | 管理员 | 手动关闭反向穿透长连接 |
| `/api/FrpTunnel/Logs` | `GET` | 管理员 | 获取最近 100 条隧道连接与报文调度日志 |

### 6.2 关键 DTO 定义（Contracts）

```csharp
public sealed record FrpTunnelConfigDto(
    string ServerUrl,
    string TunnelHost,
    string ApiKey,
    string LocalTargetUrl,
    bool AutoStart,
    int HeartbeatIntervalSeconds,
    DateTime UpdateTime);

public sealed record FrpTunnelStatusDto(
    string State, // Disconnected | Connecting | Connected | Reconnecting | Displaced | Stopped
    string? PublicUrl,
    string? LocalTargetUrl,
    DateTime? ConnectedAt,
    long UptimeSeconds,
    long SentBytes,
    long ReceivedBytes,
    string? LastError);

public sealed record FrpTunnelLogItem(
    DateTime Timestamp,
    string Level,
    string Message);
```

---

## 7. 前端配置页面设计 (FrpView.js)

1. **路由与导航**：
   - 侧边栏/导航栏新增 **「公网穿透」**（图标：🌐 或 🔗），路由指向 `/frp`。
2. **视图结构**：
   - **大盘状态区**：
     - 当前状态徽章（绿色 `已建立连接`、黄色 `正在重连`、灰色 `已停止`、红色 `被顶替/离线`）。
     - 公网访问入口（可一键点击外跳，支持快速复制公网 URL）。
     - 连接统计卡片：已连接时长、累计下行请求数、流量计数。
   - **配置卡片**：
     - **网关地址**：输入框，如 `https://frp.example.com`。
     - **隧道名称**：输入框，如 `nas-tool`（限制字母与数字）。
     - **API 密钥**：密码输入框（带明文切换按钮，支持脱敏保存）。
     - **本地目标**：默认读取当前本地端口 `http://127.0.0.1:8080`，可修改为内网局域网其它服务。
     - **开机自启**：开关按钮。
     - **操作按钮**：保存配置、启动穿透、停止连接。
   - **实时日志窗口**：
     - 类终端样式的黑色控制台卡片，展示隧道握手、心跳 PING/PONG 与请求转发往返日志，支持自动滚动与一键清空。

---

## 8. 架构门禁与 Native AOT 兼容保证

1. **AOT 零反射**：
   - 所有 DTO（`FrpTunnelConfigDto`、`FrpTunnelStatusDto`、`FrpTunnelLogItem` 等）均显式注册在 `AppJsonSerializerContext`。
   - 内部 WebSocket 控制帧采用预分配 JSON 选项与源生成上下文编解码。
2. **架构规则契合**：
   - 数据实体置于 `LinuxWebTool.Infrastructure.Persistence.Entities`。
   - DTO 置于 `LinuxWebTool.Contracts.Models`。
   - 依赖注入遵循 `PipelineExtensions` 与 `EndpointsMapper.g.cs` 架构闭环。
