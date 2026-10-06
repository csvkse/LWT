# ProxyByCF FRP 内网穿透客户端：多线路配置、302 内网重定向代理与 HTTP/SOCKS 代理支持调研及方案

> **文档状态**：已完成技术调研与方案设计  
> **面向对象**：LinuxWebTool 核心开发、网关与网络系统架构师  
> **关联源码参考**：
> - `E:\WorkProject\Node\_CFWorkerProject\ProxyByCF\tools\frp` (`tunnel-client.mjs`, `media-adapter.mjs`, `proxy-agent.mjs`)
> - `LinuxWebTool.Infrastructure.Tunnel` (`FrpTunnelEngine.cs`, `FrpTunnelService.cs`)
> - `LinuxWebTool.WebHost.Gateway` (`WebsiteProxyTransformProvider.cs`)

---

## 1. 背景与核心问题分析

在 `LinuxWebTool v2.0` 全景架构中，我们已成功使用纯 C# 原生标准库实现了基于 HTTP-over-WebSocket 协议的 ProxyByCF FRP 穿透客户端。在实际复杂的家庭边缘与私有云部署场景中，用户提出了三个高频且核心的进阶诉求：

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ 诉求 1：多线路支持 (Multi-Line Configuration)                                │
│ ├─ 能否同时穿透多个不同的内网服务 (如 NAS / 路由器后台 / 媒体服务器 / 本工具)   │
│ └─ 能否为同一服务配置多条边缘通道 (如 Cloudflare Worker 主线 + 自建中继备线)   │
├─────────────────────────────────────────────────────────────────────────────┤
│ 诉求 2：自动代理 302 转发的内网地址 (302 Intranet Redirect Auto-Proxy)       │
│ ├─ AList / STRM / Emby / Jellyfin / Docker 容器经常 302 跳转到私有 IP        │
│ └─ 外网用户直接收到 302 时，公网无法解析 192.168.x.x / 10.x.x.x，导致断连黑洞 │
├─────────────────────────────────────────────────────────────────────────────┤
│ 诉求 3：HTTP / SOCKS 代理支持 (Upstream Egress & Forwarding Proxy)           │
│ ├─ 国内网络直连 Cloudflare 边缘常被阻断或高延迟，能否通过 HTTP/SOCKS5 代理出网 │
│ ├─ 能否按线路独立指定代理服务器 (支持认证、本地 Clash/Sing-box 转发)          │
│ └─ 局域网代拉时如何做到内外分流 (隧道走代理、内网 192.168.x.x 强制直连绕过)     │
└─────────────────────────────────────────────────────────────────────────────┘
```

本文针对上述三大课题，深入结合参考项目 `ProxyByCF` 的实战经验与 `LinuxWebTool` 当前的 .NET 10 Native AOT 架构，进行可行性论证并给出完备的工程落地方案。

---

## 2. 课题一：配置多线路支持调研与设计

### 2.1 概念维度与需求拆解

“多线路”在反向穿透与网关系统中有两个层面的典型业务定义：

1. **业务多实例 (Multi-Profile / Multi-Tunnel)**：
   - 用户有多个内网应用需要同时发布到公网。例如：
     - **线路 A**：`host: lwt` 映射至 `http://127.0.0.1:8080`（运维工具本体）
     - **线路 B**：`host: nas` 映射至 `http://192.168.1.200:5000`（群晖 DSM Web）
     - **线路 C**：`host: emby` 映射至 `http://192.168.1.150:8096`（家庭媒体服务器）
     - **线路 D**：`host: router` 映射至 `http://192.168.1.1:80`（OpenWrt 路由后台）
   - **特点**：每个线路有独立的公网 Host/域名、独立的内网目标、独立的流量统计和启停控制。

2. **边缘节点多线容灾 / 故障转移 (Multi-Gateway Failover & Active-Standby)**：
   - 单个内网服务接入多个边缘 WebSocket 网关，防范单点故障或国内 DNS 污染：
     - **主线**：`wss://cf-us.yourdomain.com/frp`（Cloudflare 优选节点 A）
     - **备线 1**：`wss://cf-hk.yourdomain.com/frp`（Cloudflare 香港中继）
     - **备线 2**：`wss://vps-cn.yourdomain.com/frp`（自建国内 VPS 备用中继）
   - **特点**：心跳连续丢包或网络中断时，毫秒级平滑漂移至备选边缘节点。

### 2.2 可行性结论：完全可行（100%）

- **协议兼容性**：ProxyByCF 服务端基于 WebSocket 握手标头 `X-Tunnel-Host` 或 `TUNNEL_HOST` 区分租户与通道，服务端本就支持一个或多个客户端并发接入不同的 `host`。
- **资源开销**：在 .NET 10 下，每个空闲 WebSocket 仅占用数十 KB 内存，即便同时运行 10~20 条线路，总内存增量通常低于 15MB，无任何性能瓶颈。
- **架构改动点**：将当前的单例 `FrpTunnelEngine` 演进为 **`FrpTunnelManager`（线路管理器）+ `FrpTunnelInstance`（独立运行实例）** 体系。

### 2.3 数据库模型升级设计

将原本单行记录的 `frp_tunnel_config` 升级/平滑迁移至 `frp_tunnel_lines` 表：

```sql
CREATE TABLE IF NOT EXISTS frp_tunnel_lines (
    id TEXT PRIMARY KEY,                       -- 线路唯一 UUID (如 line_01j7...)
    name TEXT NOT NULL,                        -- 线路可读别名 (例如: 群晖 NAS / 运维控制台)
    server_url TEXT NOT NULL,                  -- 服务端 WebSocket URL (支持主备逗号分隔或主节点)
    backup_server_urls TEXT,                   -- 备选边缘节点列表 (JSON 数组或分号分隔)
    tunnel_host TEXT NOT NULL,                 -- 分配的穿透域名/主机名前缀 (唯一)
    api_key TEXT,                              -- 边缘鉴权 APIKey
    local_target_url TEXT NOT NULL,            -- 本地转发内网服务完整地址
    auto_start INTEGER NOT NULL DEFAULT 1,     -- 是否随应用自动拉起 (1/0)
    heartbeat_interval_seconds INTEGER NOT NULL DEFAULT 15, -- 心跳探活周期 (秒)
    enable_lan_302_proxy INTEGER NOT NULL DEFAULT 1,        -- 是否自动代理 302 内网地址 (1/0)
    proxy_type TEXT NOT NULL DEFAULT 'Direct', -- 代理类型: Direct / Http / Socks5 / System
    proxy_url TEXT,                            -- 代理服务器地址: 如 socks5://127.0.0.1:7890
    proxy_bypass TEXT,                         -- 代理绕过名单 (默认包含局域网私网段)
    status TEXT NOT NULL DEFAULT 'Disconnected',            -- 运行时状态 (Connected/Reconnecting/Stopped)
    sort_order INTEGER NOT NULL DEFAULT 0,     -- 显示排序权重
    create_time TEXT NOT NULL,
    update_time TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_frp_tunnel_host ON frp_tunnel_lines(tunnel_host);
```

### 2.4 C# 多线路管理器架构设计

```mermaid
classDiagram
    class FrpTunnelManager {
        -ConcurrentDictionary~string, FrpTunnelInstance~ _instances
        +GetAllLinesAsync()
        +GetLineStatusAsync(id)
        +StartLineAsync(id)
        +StopLineAsync(id)
        +SaveLineAsync(line)
        +DeleteLineAsync(id)
    }

    class FrpTunnelInstance {
        +string LineId
        +FrpTunnelLineEntity Config
        +string State
        +long SentBytes
        +long ReceivedBytes
        +RingBuffer~FrpTunnelLogItem~ Logs
        +StartAsync()
        +StopAsync()
        -RunLoopAsync()
        -ForwardHttpRequestAsync()
    }

    class FrpFailoverPolicy {
        +SelectBestServer(primary, backups)
        +RecordFailure(serverUrl)
        +RecordSuccess(serverUrl)
    }

    FrpTunnelManager "1" *-- "many" FrpTunnelInstance
    FrpTunnelInstance --> FrpFailoverPolicy
```

---

## 3. 课题二：自动代理 302 转发的内网地址调研与方案

### 3.1 核心痛点：302 内网重定向黑洞

在 NAS、流媒体和私有网盘部署中，最常见的一种链路架构如下：

```
[外网用户浏览器/播放器]
       │
       ▼ (1) GET https://myapp.example.com/api/v1/play/video.mkv
[ProxyByCF 边缘 Worker]
       │
       ▼ (2) WebSocket HTTP_REQUEST 隧道
[LinuxWebTool FRP 客户端]
       │
       ▼ (3) 本地请求转发至 http://127.0.0.1:5244 (例如 AList / STRM 挂载服务)
[AList / 本地媒体网关]
       │
       ▼ (4) 返回 302 Found
           Location: http://192.168.1.100:5211/d/local/video.mkv  (局域网私有地址！)
           (或者 Docker 容器内地址: http://alist-engine:5211/...)
```

#### 若客户端不处理 302：
- 客户端将原始 `302 Found` 与 `Location: http://192.168.1.100:5211/...` 封包并通过 WebSocket 回传；
- 外网浏览器/播放器解析出跳转地址为 `192.168.1.100`；
- 用户的公网网络根本找不到这个 IP，立即报错：`ERR_CONNECTION_REFUSED` 或 `ERR_NAME_NOT_RESOLVED`，**播放与文件下载彻底中断**。

---

### 3.2 方案对比：三种代理应对策略

| 策略维度 | 策略 A：局域网内部智能代拉流 (LAN Follow-Redirect) | 策略 B：改写 Location 为网关代理路径 (Location Rewrite) | 策略 C：公网 CDN 智能放行 (Smart CDN Bypass) |
| :--- | :--- | :--- | :--- |
| **参考来源** | `ProxyByCF` (`tunnel-client.mjs` 第 1055-1122 行) | `ProxyYARP` (`WebsiteProxyTransformProvider.cs`) | `ProxyByCF` 流媒体直链优化 |
| **执行机制** | 客户端收到私网 302 后**拦截不上报**，在局域网内直接发二次 HTTP 请求拉流并传回 Body | 客户端改写 `Location` 为 `https://<公网>/proxy/192.168.1.100:5211/...` 让浏览器重定向跳回网关 | 当 302 指向阿里云/115/COS 等公网 CDN 时，**不代拉，原样放行 302** |
| **适用场景** | **流媒体 (Emby/STRM)、文件下载、API 服务** | **纯 Web 管理控制台 (多系统后台跨站跳转)** | **网盘挂载服务、公网对象存储直链** |
| **家庭带宽消耗** | 消耗局域网下行 + 宽带上行推给公网 | 消耗两次公网请求握手 + 代理带宽 | **零消耗家庭宽带上行！公网直连 CDN 极速下载** |
| **客户端感知** | 客户端无感，直接收到 200 OK 数据流 | 客户端感知跳转，地址栏变为代理 URL | 客户端原生支持 302 跳转直连 |

---

### 3.3 推荐最佳实践：智能自适应混合代拉方案 (Adaptive Smart-Proxy)

最佳实现是以 **策略 A + 策略 C** 为主体，辅以 **SingleFlight 微缓存加速**。其流程逻辑如下：

```mermaid
flowchart TD
    A["本地服务响应 HTTP 301/302/303/307/308"] --> B{"检查 Location 标头"}
    B -- 无 Location --> C["按原响应透传"]
    B -- 有 Location --> D{"判定 Location URL 类型"}

    D -- "局域网私有地址 / 内网域名 / Docker容器" --> E["进入内网智能代拉逻辑 (策略 A)"]
    D -- "公网直链 (阿里云/115/七牛/R2等 CDN)" --> F["智能放行 302 (策略 C) \n直接交付给外网播放器直连下载 \n卸载家庭宽带上行带宽！"]

    E --> G{"检测是否发生重定向环路 (visitedUrls > 5)"}
    G -- 循环重定向 --> H["终止跟随，返回 508 Loop Detected"]
    G -- 正常 --> I["按 RFC 9110 规范调整方法 (POST->GET) \n重写 Host 标头与移除 Content-Length"]
    I --> J["局域网内发起二次 HTTP 请求获取数据"]
    J --> K{"是否为流媒体/大文件?"}
    K -- 是 --> L["写入直链微缓存 (TTL 30min) \n加速后续 Range 请求，跳过多跳 302"]
    K -- 否 --> M["将最终拉流结果打包为 HTTP_RESPONSE 送回隧道"]
    L --> M
```

---

### 3.4 私有网络边界识别算法与 RFC 规范

```csharp
public static class NetworkAddressClassifier
{
    public static bool IsPrivateNetworkUrl(string urlString)
    {
        if (string.IsNullOrWhiteSpace(urlString)) return false;
        if (!Uri.TryCreate(urlString, UriKind.Absolute, out var uri)) return false;

        var host = uri.DnsSafeHost.ToLowerInvariant();

        // 1. 本机环回地址 (RFC 1122 / RFC 4291)
        if (host is "localhost" or "127.0.0.1" or "::1") return true;

        // 2. 本地局域网单标签主机名或内网专用后缀 (Docker / mDNS / 家用路由)
        // 例如: http://alist:5211, http://synology.local, http://openwrt.lan
        if (!host.Contains('.')) return true;
        if (host.EndsWith(".local") || host.EndsWith(".lan") || 
            host.EndsWith(".internal") || host.EndsWith(".home.arpa"))
        {
            return true;
        }

        // 3. IP 地址段检查
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                // RFC 1918 10.0.0.0/8
                if (bytes[0] == 10) return true;
                // RFC 1918 172.16.0.0/12 (172.16.0.0 ~ 172.31.255.255)
                if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
                // RFC 1918 192.168.0.0/16
                if (bytes[0] == 192 && bytes[1] == 168) return true;
                // RFC 6598 CGNAT / Tailscale 虚拟网段 (100.64.0.0/10)
                if (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127) return true;
                // RFC 3927 链路本地 (169.254.0.0/16)
                if (bytes[0] == 169 && bytes[1] == 254) return true;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // RFC 4193 唯一本地地址 ULA (fc00::/7 包括 fd00::/8)
                if (ip.IsIPv6UniqueLocal) return true;
                // RFC 4291 链路本地 (fe80::/10)
                if (ip.IsIPv6LinkLocal) return true;
            }
        }

        return false;
    }
}
```

---

## 4. 课题三：FRP 线路新增 HTTP / SOCKS 代理支持调研与设计

### 4.1 核心需求与实战场景剖析

在实际生产中，FRP 穿透与网络代理有密切的结合需求，主要体现在以下两个核心方向：

```
方向 A：上游出网前置代理 (Upstream Egress Proxy)
[FRP 客户端] ──(SOCKS5 / HTTP 代理)──► [海外/局域网代理服务器] ──► [Cloudflare Worker 边缘节点]
(解决境内运营商直连 Cloudflare 丢包、阻断、SNI 审查、高延迟问题)

方向 B：智能分流与防环绕行 (Split-Tunneling & Bypass)
[FRP 客户端] ──┬─ (上游代理) ──► 连接 Cloudflare Worker 长连接隧道
               └─ (强制直连) ──► 局域网 192.168.1.100 (代拉 302 或本地转发目标)
(确保内网私有地址不走外部代理，避免出网代理无法访问本地机器的问题)
```

1. **场景 1：上游出网代理（Upstream Egress Proxy）**
   - **痛点**：国内宽带直接建立 `wss://edge.example.com/frp` 时，往往受限于 Cloudflare 免费节点在境内的路由劣化，容易频繁重连或握手超时；部分公司内部网络严苛，必须通过企业内网的 `http://proxy.corp.internal:8080` 才能访问外部互联网。
   - **解决方案**：在 FRP 线路配置中，指定 `ProxyUrl`，让长连接 WebSocket 握手经由指定的 HTTP / SOCKS5 前置代理（如本地 Clash/Sing-box `socks5://127.0.0.1:7890`）中转出网。
   - **参考项目经验**：ProxyByCF 参考工程专门编写了 `proxy-agent.mjs`，正是为了在 Node 端支持通过 `socks5://` 或 `http://` 代理发起底层 TCP/TLS 握手。

2. **场景 2：分流规则与内网代拉直连（Split-Tunneling & Bypass）**
   - **痛点**：如果全局开启代理，当发生**课题二**中的 302 局域网重定向（例如 `http://192.168.1.100:5211/video.mp4`）时，若该二次请求也被送往 SOCKS5/HTTP 代理服务器，而代理服务器位于公网或海外，根本无法解析或路由局域网 IP，导致代拉失败！
   - **解决方案**：严格实施**内外分流策略**：
     - **WebSocket 长连接（公网流量）**：按配置走指定的上游 HTTP/SOCKS 代理；
     - **本地转发与 302 代拉（内网流量）**：通过 `NetworkAddressClassifier` 判定，若目标为私网地址，**强制绕过代理直连**。

3. **场景 3：反向 SOCKS5 代理网关（Reverse SOCKS5 Jumpbox）**
   - **扩展可能**：将内网机器本身作为 SOCKS5 代理出口，通过 FRP 隧道把家庭局域网暴露给外部可信访问者，使外部客户端借助该隧道访问家庭整网。

---

### 4.2 可行性结论：完全可行（.NET 10 BCL 原生支持，零额外依赖）

在 C# / .NET 10 体系中，增加 HTTP / SOCKS 代理支持具备极高的技术优势：

| 代理协议 | .NET BCL 支持情况 | 凭据认证支持 | AOT 兼容性 | 对应实现类 |
| :--- | :--- | :--- | :--- | :--- |
| **HTTP CONNECT** | ✅ 原生内置支持 (`http://host:port`) | ✅ Basic 认证 | ✅ 100% Native AOT | `System.Net.WebProxy` |
| **HTTPS CONNECT** | ✅ 原生内置支持 (`https://host:port`) | ✅ Basic 认证 | ✅ 100% Native AOT | `System.Net.WebProxy` |
| **SOCKS4 / SOCKS4a** | ✅ .NET 6+ 原生内置支持 (`socks4://`) | ✅ 用户名认证 | ✅ 100% Native AOT | `System.Net.WebProxy` |
| **SOCKS5 / SOCKS5h** | ✅ .NET 6+ 原生内置支持 (`socks5://`) | ✅ 用户名/密码认证 (RFC 1928) | ✅ 100% Native AOT | `System.Net.WebProxy` |

> [!IMPORTANT]
> **零第三方库优势**：
> 传统的 Node.js 需要额外引入 `socks-proxy-agent`、`https-proxy-agent`、`undici` 等一整套依赖链；而在 .NET 10 中，`SocketsHttpHandler` 与 `ClientWebSocket` 内部已由微软官方原生实现了 SOCKS4/SOCKS5 协议状态机。
> **我们无需引入任何第三方 NuGet 包，即可无缝支持全协议代理，完全符合 `<IsAotCompatible>true</IsAotCompatible>` 的严苛门禁要求！**

---

### 4.3 C# 原生 WebSocket 代理接入代码示例

在 `FrpTunnelEngineInstance.cs` 建立连接时，代码仅需数行即可完成无缝注入：

```csharp
private ClientWebSocket CreateConfiguredWebSocket(FrpTunnelLineEntity line)
{
    var ws = new ClientWebSocket();
    ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(line.HeartbeatIntervalSeconds);

    // 1. 配置上游出网代理 (HTTP / HTTPS / SOCKS4 / SOCKS5)
    var proxyUrl = ResolveProxyUrl(line);
    if (!string.IsNullOrWhiteSpace(proxyUrl))
    {
        var proxyUri = new Uri(proxyUrl);
        var webProxy = new WebProxy(proxyUri);

        // 支持 proxy://user:pass@host:port 内联认证或显式配置
        if (!string.IsNullOrEmpty(proxyUri.UserInfo))
        {
            var parts = proxyUri.UserInfo.Split(':', 2);
            webProxy.Credentials = new NetworkCredential(
                Uri.UnescapeDataString(parts[0]),
                parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : string.Empty);
        }

        // 必须配置 Bypass 列表：确保局域网回环与私网地址绝不走代理
        webProxy.BypassProxyOnLocal = true;
        webProxy.BypassList = new[]
        {
            "localhost", "127.0.0.1", "[::1]",
            "192.168.*", "10.*", "172.16.*", "172.17.*", "172.18.*", "172.19.*",
            "172.2*.*", "172.30.*", "172.31.*", "*.local", "*.lan"
        };

        ws.Options.Proxy = webProxy;
        _logger.LogInformation("线路 [{Name}] 启用上游出口网络代理: {ProxyScheme}://{Host}:{Port}",
            line.Name, proxyUri.Scheme, proxyUri.Host, proxyUri.Port);
    }
    else
    {
        // 显式直连，避免被系统全局环境变量误干扰
        ws.Options.Proxy = null;
    }

    return ws;
}

private static string? ResolveProxyUrl(FrpTunnelLineEntity line)
{
    if (line.ProxyType == "Custom" && !string.IsNullOrWhiteSpace(line.ProxyUrl))
    {
        return line.ProxyUrl.Trim();
    }
    if (line.ProxyType == "System")
    {
        return Environment.GetEnvironmentVariable("ALL_PROXY")
            ?? Environment.GetEnvironmentVariable("HTTPS_PROXY")
            ?? Environment.GetEnvironmentVariable("HTTP_PROXY");
    }
    return null; // Direct
}
```

---

### 4.4 本地转发与局域网代拉的独立 HttpClient 配置

为了确保**「WebSocket 走代理出网」与「本地转发/302代拉绝不走代理」**并存，为局域网转发创建专用的 `_localHttpClient`：

```csharp
private readonly HttpClient _localHttpClient = new(new SocketsHttpHandler
{
    AllowAutoRedirect = false,  // 手动拦截并处理 302，防止不可控跳转
    UseCookies = false,
    EnableMultipleHttp2Connections = true,
    PooledConnectionLifetime = TimeSpan.FromMinutes(15),
    Proxy = null,               // ★ 核心保障：本地与私网请求强制直连，绝不经由上游代理
    UseProxy = false
});
```

---

## 5. 架构升级实现方案与 Native AOT 保障

### 5.1 Native AOT 零反射要求
在升级多线路模型、302 自动代理与代理配置时，所有交互数据包均需在静态源码生成器中显式注册：
1. `FrpTunnelLineEntity` 与 `FrpTunnelLineDto`；
2. 包含 `ProxyType`、`ProxyUrl`、`ProxyBypass`、`EnableLan302Proxy` 的新增字段；
3. 全部加入 `AppJsonSerializerContext.cs`，彻底杜绝运行时 `MakeGenericType` 或反射序列化。

### 5.2 配置页面 UI 演进设计
前端 `FrpView.js` 升级为 **网格化多线路面板 (Multi-Tunnel Dashboard)**，并在新建/编辑弹窗中加入完整的代理配置面板：

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ ProxyByCF FRP 内网穿透多线路控制台                          [ + 新建穿透线路 ] │
├─────────────────────────────────────────────────────────────────────────────┤
│ ┌──────────────────────┐ ┌──────────────────────┐ ┌──────────────────────┐  │
│ │ 🟢 运维总控端 (活跃)    │ │ 🟢 群晖 NAS (活跃)    │ │ ⚪ Emby 流媒体 (空闲) │  │
│ │ 域名: lwt.example.com │ │ 域名: nas.example.com │ │ 域名: emby.example.com │  │
│ │ 本地: 127.0.0.1:8080 │ │ 本地: 192.168.1.200  │ │ 本地: 192.168.1.150  │  │
│ │ 出网: 直连           │ │ 出网: SOCKS5 代理     │ │ 出网: HTTP 代理      │  │
│ │ 302代拉: [已开启]     │ │ 302代拉: [已开启]     │ │ 302代拉: [已开启]     │  │
│ │ ↑ 24.5MB  ↓ 112MB    │ │ ↑ 1.2GB   ↓ 840MB    │ │ ↑ 0B      ↓ 0B       │  │
│ │ [断开连接] [编辑]    │ │ [断开连接] [编辑]    │ │ [启动连接] [编辑]    │  │
│ └──────────────────────┘ └──────────────────────┘ └──────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────┘
```

#### 编辑弹窗中的代理设置项：
- **出网代理类型**：
  - 🔘 直连 (Direct)
  - 🔘 SOCKS5 代理 (`socks5://127.0.0.1:7890`)
  - 🔘 HTTP 代理 (`http://user:pass@192.168.1.1:8080`)
  - 🔘 继承环境变量 (`ALL_PROXY` / `HTTPS_PROXY`)
- **代理服务器地址**：支持包含用户名密码（如 `socks5://admin:token123@10.0.0.1:1080`）
- **自动代理 302 内网地址**：`[✓ 启用]`（自动识别私网 IP 代拉流，公网 CDN 直链放行直连）

---

## 6. 落地执行路线图 (Actionable Roadmap)

```
[第 1 阶段：轻量落地] 302 局域网智能代拉引擎
  ├─ 引入 NetworkAddressClassifier 私网判定逻辑
  ├─ 在 FrpTunnelEngine.ForwardHttpRequestAsync 中实现 301-308 跟随与公网 CDN 放行
  └─ 编写 xUnit 单元测试覆盖 192.168.x.x 跟随与公网直链放行验证

[第 2 阶段：多线路与代理核心改造]
  ├─ 创建 frp_tunnel_lines 实体与持久化 Store (包含 proxy_type, proxy_url 字段)
  ├─ 实现 FrpTunnelManager 线路调度池与独立生命周期管理
  ├─ 为 WebSocket 客户端与 HttpClient 分离注入 WebProxy (支持 HTTP/SOCKS5 零依赖接入)
  ├─ 升级 Minimal API 控制器 /api/FrpTunnel/Lines
  └─ AppJsonSerializerContext 静态 DTO 注册

[第 3 阶段：前端多线路交互视图]
  ├─ 改造 FrpView.js 为卡片流布局，支持多线路独立监控与配置弹窗
  ├─ 增设「网络代理设置」与「自动代理 302 内网地址」功能开关
  └─ 运行 npm run gate 确保前端架构门禁 100% 通过
```

---

## 7. 总结

1. **多线路支持**：在当前纯 C# 架构下**完全可行且易于水平扩展**。通过升级到 `FrpTunnelManager` 架构，用户可以在单一程序内同时映射多套内网业务（如 NAS、路由器、媒体库），并支持边缘多节点容灾。
2. **302 内网自动代理**：是解决家庭内网穿透断链的**核心杀手锏功能**。采纳 ProxyByCF 验证成熟的“**私网地址智能代拉 + 公网 CDN 放行 + 302 单飞微缓存**”自适应体系，既能让内网服务无缝穿透发布，又能最大化节省家庭宽带上行流量。
3. **HTTP / SOCKS 代理支持**：.NET 10 的 `System.Net.WebProxy` 与 `SocketsHttpHandler` 原生内置了对 **HTTP / HTTPS / SOCKS4 / SOCKS5** 的完整支持，**零第三方库引入、100% Native AOT 兼容**。结合“**出网走代理、内网代拉强制直连**”的内外分流机制，可完美化解境内访问 Cloudflare 边缘的阻断与高延迟困扰。
