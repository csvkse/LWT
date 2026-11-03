# SOCKS5 客户端边缘中继网关：多上游协议、链式代理、分流路由与透明网关架构调研与深度设计

> **文档标识**：`LWT-ARCH-FORWARDER-004`  
> **状态**：已评审 (Approved)  
> **核心主题**：多上游协议适配 (Poly-Protocol Upstreams)、链式代理 (Proxy Chaining)、全维度分流路由 (Rule-Based Dispatching)、TUN 用户态透明网关 (Transparent Gateway)、特权自愈与优雅降级  
> **适用模块**：`plugins/socks5-forwarder` / LinuxWebTool 微服务平台  
> **设计模式**：适配器模式 (Adapter)、组合模式 (Composite)、责任链模式 (Chain of Responsibility)、策略模式 (Strategy)、有限状态机 (FSM)  

---

## 1. 执行摘要与演进定位

当前的 `socks5-forwarder` 是一个基于全双工管道的轻量级 TCP 边缘中继器，其核心职责是将本地多入站端口流量通过 WebSocket 加密隧道转发至远端 LWT Ingress。随着应用场景的拓宽，用户对**协议通用性、网络拓扑穿透深度、流量精细化分流控制与免配置终端接入**提出了四项深层次诉求：

```mermaid
flowchart TD
    subgraph "四大演进诉求 (User Requirements)"
        R1["1. 远端多上游协议支持<br/>(LWT-WSS / SOCKS5 / HTTP / SS / Trojan / Direct)"]
        R2["2. 链式代理支持<br/>(自定义多级静态中间代理 + 动态优选故障自愈代理链)"]
        R3["3. 智能分流路由<br/>(私有局域网直连 / 域名后缀 / IP-CIDR / 端口分流)"]
        R4["4. 透明网关可选启用<br/>(TUN 用户态 TCP/IP 栈 + 特权探测与平滑降级)"]
    end

    subgraph "核心引擎中枢 (SOCKS5 Forwarder Engine v2)"
        InboundEngine["入站协议引擎<br/>(Port Pool + Mixed SOCKS5/HTTP + TUN 网卡)"]
        RoutingCore["责任链分流决策中枢<br/>(Private IP / Domain Trie / IP-CIDR / Default)"]
        ChainingEngine["链式多跳调度器<br/>(Hop 1 -> Hop 2 -> Hop 3 -> Target)"]
        OutboundAdapters["多协议出口适配器池<br/>(SOCKS5 / WSS / HTTP / SS / Trojan / Direct / Reject)"]
    end

    R1 --> OutboundAdapters
    R2 --> ChainingEngine
    R3 --> RoutingCore
    R4 --> InboundEngine

    InboundEngine --> RoutingCore
    RoutingCore --> ChainingEngine
    ChainingEngine --> OutboundAdapters

    style InboundEngine fill:#4a90e2,stroke:#333,stroke-width:2px,color:#fff
    style RoutingCore fill:#f5a623,stroke:#333,stroke-width:2px,color:#000
    style ChainingEngine fill:#bd10e0,stroke:#333,stroke-width:2px,color:#fff
    style OutboundAdapters fill:#50e3c2,stroke:#333,stroke-width:2px,color:#000
```

本调研全面分析该四项特性的可行性、性能瓶颈、逻辑陷阱、设计模式重构、开源库选型、业界标杆实践与 LWT 现有基础设施复用方案，给出工业级落地蓝图。

---

## 2. 四大核心特性可行性与头脑风暴深度剖析

### 2.1 特性一：远端上游服务节点的多上游协议支持

#### 1. 现状痛点与业务价值
目前上游节点硬编码依赖 `ws://` 或 `wss://` 协议，仅能对接开启了 WebSocket Ingress 伪装隧道的 LWT 宿主节点。用户若已有标准 SOCKS5 代理服务器、企业 HTTP CONNECT 代理、Shadowsocks 商业节点或 Trojan 节点，无法直接接入。

#### 2. 协议矩阵与技术特征对比

| 协议类型 | URL Scheme | 传输层载荷与握手机制 | 适用场景与优缺点 | 依赖与实现复杂度 |
| :--- | :--- | :--- | :--- | :--- |
| **LWT WSS (已有)** | `ws://`, `wss://` | HTTP 升级 WebSocket，自定义子协议 Token 鉴权 | 专为跨越公共 CDN/反向代理设计，抗主动探测极强。 | 零外部依赖（已有自研 `ws_client.go`） |
| **标准 SOCKS5** | `socks5://`, `socks5h://` | RFC 1928，协商认证（无认证或账密），CONNECT 目标 | 通用性最强，局域网与内网代理事实标准。无原生加密。 | Go 标准库 `golang.org/x/net/proxy` 原生提供 |
| **HTTP CONNECT** | `http://`, `https://` | HTTP `CONNECT target:port HTTP/1.1`，支持 Basic Auth | 企业级防火墙合规代理，通用网关。明文 HTTP 无加密，HTTPS 具 TLS。 | 纯原生 Go 实现仅需 ~40 行代码 |
| **Shadowsocks** | `ss://` | AEAD 加密报文（AES-128/256-GCM、Chacha20-Poly1305） | 传统抗封锁利器，协议轻量无 HTTP 封装开销。 | 集成 `go-shadowsocks2`，纯 Go 跨平台 |
| **Trojan** | `trojan://` | 原生 TLS 1.3 载荷首包写入 `SHA224(password) + CRLF + SOCKS5` | 高度隐匿，外表完全伪装为合规 HTTPS 流量。 | 原生 Go `crypto/tls` + 首包序列化，~80 行 |
| **Direct 直连** | `direct://` | 本地套接字直连出站 (`net.Dialer`) | 国内免流、局域网设备直连、分流策略兜底。 | 原生标准库 `net` |
| **Reject 阻断** | `reject://`, `block://` | 0 字节立即断开或 SOCKS5 拒绝响应 | 广告过滤、恶意隐私追踪拦截、合规限制。 | 瞬间执行 `conn.Close()` |

#### 3. 架构设计：统一出站适配器策略（Outbound Adapter Pattern）

```go
// IOutboundDialer 抽象所有出站协议的统一拨号行为
type IOutboundDialer interface {
    Protocol() string
    // DialContext 建立直达目标的出站连接
    DialContext(ctx context.Context, network, targetAddr string) (net.Conn, error)
    // DialThrough 基于现有流进行下一级协议封装握手（链式代理核心！）
    DialThrough(ctx context.Context, baseConn net.Conn, targetAddr string) (net.Conn, error)
}
```

---

### 2.2 特性二：本地多入站中继规则的链式代理支持 (Proxy Chaining)

#### 1. 业务场景与用户需求
- **自定义静态多级代理（Custom Static Chain）**：  
  客户端入站 ➔ 前置跳板节点 (国内 BGP SOCKS5) ➔ 核心跨境中继 (香港 WSS 隧道) ➔ 落地节点 (美国出口 Shadowsocks) ➔ 目标网站。此架构可显著优化跨国跨运营商路由质量，隐藏真实源客户端。
- **动态多级代理（Dynamic Resilient Chain）**：  
  在中间跳板或出口阶段指定“节点池”，根据实时延迟（`LatencyMs`）或健康状态（`Healthy`）动态选择最优跳板。若中间某跳发生网络拥塞或崩溃，动态状态机自动漂移至备选跳板，实现**零人工干预的高可用（HA）**。

#### 2. 逻辑与性能缺陷分析（头脑风暴）
- **缺陷 A：握手 RTT 线性累加（Latency Stacking）**：
  若 3 跳代理分别在每次用户请求到达时独立执行三次完整的 TCP 握手 + TLS 握手 + 协议握手，建连延迟将膨胀至：
  $$\text{Latency} = \text{RTT}_{0-1} + \text{RTT}_{1-2} + \text{RTT}_{2-3} + \text{RTT}_{3-\text{Target}} \approx 800\text{ms} \sim 2500\text{ms}$$
  **对策**：在前两跳（如 Inbound 到核心中继）引入**多路复用连接池（Yamux Mux Pool）**。保持一条物理长连接，业务流通过虚拟 Stream 瞬间复用分发，将额外握手开销压制到 **0 RTT**。
- **缺陷 B：级联超时与死锁穿透（Cascading Timeouts）**：
  若某中间跳板出现 Silent Drop（静默丢包），下游会陷入长达数十秒的阻塞等待。
  **对策**：全链路采用 `context.WithTimeout` 统一传导，分段预算控制（如单跳超时 2 秒，全链路不超过 6 秒），超时立即触发备用链路熔断重试。

#### 3. 架构设计：组合模式实现多跳级联

```mermaid
sequenceDiagram
    autonumber
    participant Client as 客户端应用 (浏览器)
    participant Inbound as 本地 Inbound (:10801)
    participant Hop1 as 跳板 1 (SOCKS5 Relay)
    participant Hop2 as 跳板 2 (WSS Stealth Tunnel)
    participant Exit as 落地节点 (Trojan Exit)
    participant Target as 目标网站 (google.com:443)

    Client->>Inbound: SOCKS5 握手 (请求连接 google.com:443)
    Inbound->>Hop1: 1. net.Dial 物理连接跳板 1
    Inbound->>Hop1: 2. SOCKS5 CONNECT (目标: Hop2 地址)
    Inbound->>Hop2: 3. 基于 Hop1 管道建立 WSS 握手 (Upgrade ws://)
    Inbound->>Exit: 4. 基于 Hop2 管道发送 Trojan 鉴权并请求 CONNECT google.com:443
    Exit->>Target: 5. 落地节点与目标建立真实 TCP 连接
    Inbound-->>Client: 6. 返回 SOCKS5 成功响应
    Note over Client,Target: 进入全双工 BridgeDuplexStream 极速对拷
```

---

### 2.3 特性三：本地多入站中继规则的分流代理功能 (Rule-Based Routing)

#### 1. 业务痛点与协议感知前提
目前微服务是“四层盲转发”，只要客户端连入 10801 端口，微服务就将整个 TCP 流盲目塞入上游。这导致：
- 用户电脑设置了系统代理后，访问本地局域网 NAS（`192.168.1.x`）或本地打印机也被送入远端节点，导致内网无法访问！
- 访问国内网站（百度、微信）全部绕行海外代理，延迟高且白白消耗上游流量。

**核心技术突破**：入站监听器必须具备**应用层协议感知能力（Protocol Sniffing & Handshake Parsing）**！
当客户端建立连接时，入站先解析 SOCKS5 或 HTTP 握手，提取目标主机 `TargetHost`（域名或 IP）与目标端口 `TargetPort`，再将其送入**分流路由决策引擎**。

#### 2. 分流规则维度全景设计

```mermaid
graph TD
    In[客户端请求到来: TargetHost:TargetPort] --> R0{规则 0: 是否为局域网/私网 IP?<br>127.0.0.1, 10.0.0.0/8, 192.168.0.0/16...}
    R0 -->|是| Act_Direct[动作: DIRECT 直连出站]
    R0 -->|否| R1{规则 1: 域名完全匹配 / 关键词?<br>adservice.google.com, *track*}
    R1 -->|命中广告/黑名单| Act_Reject[动作: REJECT 快速阻断]
    R1 -->|否| R2{规则 2: 域名后缀规则?<br>*.cn, *.edu.cn, *.bilibili.com}
    R2 -->|命中国内域名| Act_Direct
    R2 -->|否| R3{规则 3: IP-CIDR 段规则?<br>223.5.5.0/24, 114.114.114.0/24}
    R3 -->|命中直连 IP 段| Act_Direct
    R3 -->|否| R4{规则 4: 专用内网域名?<br>*.corp.internal}
    R4 -->|命中企业私有云| Act_Chain[动作: CHAIN 走多级代理链]
    R4 -->|否| Act_Default[动作: PROXY 走当前默认上游节点]

    style Act_Direct fill:#50e3c2,stroke:#333,stroke-width:2px,color:#000
    style Act_Reject fill:#e74c3c,stroke:#333,stroke-width:2px,color:#fff
    style Act_Chain fill:#bd10e0,stroke:#333,stroke-width:2px,color:#fff
    style Act_Default fill:#4a90e2,stroke:#333,stroke-width:2px,color:#fff
```

#### 3. 亚微秒级（< 1µs）分流匹配算法设计
- **局域网私网判定 (`IsPrivateNetwork`)**：
  在 Go 中使用位掩码预编译 RFC 1918、RFC 3927、RFC 4193 CIDR 列表，单次判定开销 **< 5 纳秒**。
- **双向域名字典树（Domain Trie）**：
  - 完全匹配：直接哈希表 `map[string]Action`（$O(1)$，约 12 纳秒）。
  - 后缀匹配：按域名前缀反转分段构建 Radix Trie（如 `com.google.*`），支持单次遍历判定，万级规则匹配耗时 **< 0.4 微秒**。
- **IP-CIDR 树（Patricia Trie）**：
  利用 Radix Tree 存储 CIDR 路由，避免遍历循环，纳秒级定位。

---

### 2.4 特性四：透明代理功能 (Transparent Proxying, 可选启用)

#### 1. 业务定位
普通代理要求客户端设备（如 PC、手机）显式配置代理 IP 和端口；而透明网关使得局域网内的所有设备（包括电视盒子、物联网设备、开发容器）**无需进行任何代理设置，只需将默认网关或 DNS 指向 LWT 节点**，所有流量即刻自动受规则引擎接管。

#### 2. 技术路线决策：Linux iptables TProxy vs TUN 虚拟网卡 (gVisor)

| 评估维度 | 方案 A：Linux 原生 iptables TPROXY / REDIRECT | 方案 B：TUN 虚拟网卡 + gVisor netstack (推荐首选) |
| :--- | :--- | :--- |
| **底层要求** | 强依赖宿主机内核 `iptables`、`mangle` 表与 `ip rule` 策略路由 | 仅依赖标准字符设备 `/dev/net/tun` |
| **容器侵入性** | **极高**。需篡改宿主机或共享网络命名空间的防火墙规则，极易与 Docker 自身的 NAT 规则冲突。 | **极低**。在容器内部生成独立虚拟网卡（如 `lwt-tun0`），自闭环运行，完全不污染外部防火墙。 |
| **稳定性与冲突风险** | 高。一旦规则配置有误可能导致宿主断网；容器重启残留规则会破坏宿主网络。 | 极高。关闭时直接释放 TUN 文件句柄，虚拟网卡与网络栈自动销毁，零残留。 |
| **协议栈承载能力** | 仅能在四层进行端口重定向，获取原始目标需 `SO_ORIGINAL_DST`。 | 完整用户态 TCP/IP 协议栈（TCP/UDP/ICMP 完整支持），自动具备重组与流分发能力。 |
| **LWT 现有底座契合度** | 差 | **极佳！宿主容器已原生挂载 `/dev/net/tun` 并经 EasyTier 验证完全可用！** |

#### 3. 依赖探测与优雅降级状态机（Zero-Panic Graceful Fallback）

透明代理是一项特权功能。如果用户在非特权容器、未挂载 `/dev/net/tun` 的环境或 Windows 开发机上运行微服务，**系统必须实现优雅降级，绝对严禁引发 Panic 崩溃或导致其他入站端口瘫痪**：

```mermaid
stateDiagram-v2
    [*] --> Probing: 启动透明代理组件
    Probing --> CheckingEnv: 读取 LWT_HAS_TUN 环境变量
    
    CheckingEnv --> DegradedNoTun: LWT_HAS_TUN != "true"
    CheckingEnv --> ProbingDevice: LWT_HAS_TUN == "true"
    
    ProbingDevice --> DegradedPermDenied: /dev/net/tun 打开失败 (EACCES / ENOENT)
    ProbingDevice --> CreatingTun: 获得设备描述符 fd
    
    CreatingTun --> Running: gVisor netstack 初始化成功
    
    DegradedNoTun --> PassiveFallback: 记录警告日志, 标记状态为 Degraded
    DegradedPermDenied --> PassiveFallback
    
    PassiveFallback --> [*]: 仅提供常规端口代理 (10801~10815)
    Running --> [*]: 透明网关与端口代理并行工作
```

---

## 3. 设计模式全景架构建模

为了保证后续接入 10+ 种出站协议、任意深度代理链及复合分流规则时代码不腐化，系统采用以下经典设计模式进行整体建模：

```mermaid
classDiagram
    direction TB

    class IInboundListener {
        <<interface>>
        +Start(dispatcher IDispatcher) error
        +Stop()
        +Profile() InboundProfile
    }

    class PortInboundListener {
        -listeners []net.Listener
        -acl *ACLManager
    }

    class TunInboundListener {
        -tunDev io.ReadWriteCloser
        -netstack *NetstackStack
    }

    class IDispatcher {
        <<interface>>
        +Dispatch(conn net.Conn, metadata *SessionMetadata)
    }

    class RuleRoutingDispatcher {
        -rules []IRoutingRule
        -defaultOutbound IOutboundDialer
        +Route(metadata *SessionMetadata) IOutboundDialer
    }

    class IRoutingRule {
        <<interface>>
        +Match(metadata *SessionMetadata) bool
        +TargetTag() string
    }

    class PrivateNetworkRule {
        +Match(metadata *SessionMetadata) bool
    }

    class DomainTrieRule {
        -trie *DomainTrie
        +Match(metadata *SessionMetadata) bool
    }

    class IpCidrRule {
        -trie *PatriciaTrie
        +Match(metadata *SessionMetadata) bool
    }

    class IOutboundDialer {
        <<interface>>
        +Protocol() string
        +DialContext(ctx, network, target) (net.Conn, error)
        +DialThrough(ctx, baseConn, target) (net.Conn, error)
    }

    class Socks5OutboundAdapter {
        +DialContext()
        +DialThrough()
    }

    class WssOutboundAdapter {
        +DialContext()
        +DialThrough()
    }

    class ChainedCompositeOutbound {
        -hops []IOutboundDialer
        +DialContext()
    }

    IInboundListener <|.. PortInboundListener
    IInboundListener <|.. TunInboundListener
    IDispatcher <|.. RuleRoutingDispatcher
    RuleRoutingDispatcher o-- IRoutingRule
    IRoutingRule <|.. PrivateNetworkRule
    IRoutingRule <|.. DomainTrieRule
    IRoutingRule <|.. IpCidrRule
    RuleRoutingDispatcher ..> IOutboundDialer : 派发出站
    IOutboundDialer <|.. Socks5OutboundAdapter
    IOutboundDialer <|.. WssOutboundAdapter
    IOutboundDialer <|.. ChainedCompositeOutbound : 组合模式
```

### 1. 适配器模式 (Adapter Pattern)
- `IOutboundDialer` 抹平 `SOCKS5`、`WSS`、`HTTP`、`Shadowsocks`、`Trojan` 与 `Direct` 的协议差异。
- 业务流和调度器只针对抽象接口通信，新增出站协议无需修改路由分发核心。

### 2. 组合模式 (Composite Pattern)
- `ChainedCompositeOutbound` 实现了 `IOutboundDialer` 接口。
- 对于路由引擎而言，访问一条多跳代理链与访问一个单体上游节点完全等价，支持无限级嵌套与递归穿越。

### 3. 责任链模式 (Chain of Responsibility)
- 分流引擎采用过滤器责任链（`PrivateNetwork ➔ AdsBlock ➔ DirectDomains ➔ CorpChains ➔ DefaultProxy`）。
- 规则按优先级顺序挂载，命中即返回，未命中下传，支持动态重载与顺序微调。

### 4. 状态机模式 (State Machine Pattern)
- **上游节点健康度状态机**：`Healthy (延迟 < 200ms)` ➔ `Degraded (延迟 > 800ms)` ➔ `Unhealthy (连通失败)` ➔ `CircuitBroken (熔断冷却)`。
- **透明网关特权状态机**：`Probing` ➔ `Running` ➔ `DegradedFallback`（无特权平滑降级）。

---

## 4. 业界标杆方案对比与主流开源库选型

### 4.1 业界主流开源网络工具横向评测

| 维度 | Sing-Box (SagerNet) | Clash Meta / Mihomo | Gost (ginuerzh) | LWT 演进架构方案 (本方案) |
| :--- | :--- | :--- | :--- | :--- |
| **架构哲学** | 极简微内核，纯模块化，专注极致性能与跨平台 | 全功能代理工具，强大规则集与 Web 控制面板 | 链式代理先驱，任意协议转接（GO Simple Tunnel） | **高内聚微服务插件，轻量纯 Go，与 LWT 宿主深度协同** |
| **链式代理能力** | 支持 `chain` outbound 级联 | 支持 `relay` 代理组多跳转发 | 业界最强大的链式多跳转发（`gost -L ... -F ... -F ...`） | **原生组合模式实现，支持静态显式跳板与动态最优延迟跳板** |
| **分流规则引擎** | RuleSet 编译二进制，支持 Geosite / GeoIP 极速匹配 | DOMAIN-SUFFIX / IP-CIDR / GEOIP，生态成熟 | 相对简陋，主要以旁路/转发为主 | **双向 Domain Trie + CIDR Patricia Trie，亚微秒级判定，内置局域网智能直连** |
| **透明代理方案** | TUN 虚拟网卡 + gVisor netstack (Mixed 模式) | TUN 模式 (gVisor / System 协议栈) | Redirect / TProxy 四层重定向 | **TUN + gVisor 用户态栈，具备 `LWT_HAS_TUN` 环境自适应与优雅降级** |
| **内存与包袱** | 极佳 (内存通常 < 30MB) | 较重 (内存通常 60~120MB) | 极佳 (轻量) | **极轻量 (预计常驻内存 20~35MB，32KB 内存池对拷)** |

### 4.2 核心开源库选型矩阵

| 关键模块 | 推荐候选库 | 许可协议 | 选型结论与决策理由 |
| :--- | :--- | :--- | :--- |
| **SOCKS5 出站/入站** | `golang.org/x/net/proxy` + 原生实现 | BSD-3 | **推荐优先原生实现握手解析（~150行）**。无外部依赖，零编译负担，掌控力最高。 |
| **HTTP CONNECT** | Go 原生 `net/http` | Go 官方 | **推荐自研轻量握手（~40行）**。仅需发出 CONNECT 请求并校验 200 响应。 |
| **Shadowsocks** | `github.com/shadowsocks/go-shadowsocks2` | Apache-2.0 | **推荐集成**。纯 Go 实现，无 CGO 依赖，支持主流 AEAD 加密算法。 |
| **用户态 TCP/IP 栈** | `gvisor.dev/gvisor/pkg/tcpip` | Apache-2.0 | **推荐集成 (用于透明网关)**。Google 官方沙箱核心，业界最成熟的双栈用户态协议栈。 |
| **TUN 网卡读写接口** | `github.com/songgao/water` | BSD-3 | **推荐集成**。轻量跨平台 TUN/TAP 读写通道，几行代码即可创建与打开 Linux `/dev/net/tun`。 |
| **多路复用连接池** | `github.com/hashicorp/yamux` | MPL-2.0 | **推荐集成 (用于多跳代理)**。单物理连接承载数百虚拟 Stream，解决多跳建连延迟叠加问题。 |
| **域名字典树** | `github.com/armon/go-radix` 或 自研 Radix Trie | MIT | **推荐轻量自研 Domain Trie**。针对域名分段优化（`com.example.*`），无内存碎片。 |

---

## 5. 当前 LWT 系统基础设施复用分析

本套架构无需从零建设庞大的周边生态，可直接无缝复用 LWT 平台的现有基础设施：

```mermaid
mindmap
  root((LWT 系统基础设施复用))
    系统与内核特权底座
      LWT_HAS_TUN 环境变量自省 (PluginStandardEnvBuilder)
      宿主特权容器与 /dev/net/tun 驱动通道 (EasyTier 已验证成熟可用)
      内部极速直连端口 5270 与外部发布端口 5271 (零 NAT 环回黑洞)
    高并发与网络转发基础设施
      BridgeDuplexStream 全双工流对拷 (32KB sync.Pool 零内存分配)
      DialWebSocket Ingress 伪装加密隧道客户端 (已实测 1ms 延迟)
      ReactiveIngressRoutingTable 纯内存只读路由表 (RCU 亚毫秒并发)
    存储与状态隔离
      LWT_VOLUME_DIR 独占持久化卷 (跨版本升级受系统保护绝不丢失)
      LWT_STATE_DIR 持久化配置中心 (放置 config.json)
    前端与交互
      原生 ESM + Tailwind 暗黑赛博控制台 (ui/index.html & ui/app.js)
      PluginUrlResolver 统一策略仲裁 (自动注入 Token 与反代路径)
    极速流水线
      -HotUpdatePlugin 秒级在线热更新 (原子 Inode 替换防 ETXTBSY)
      pre-commit & security-gate.cjs 全仓安全门禁
```

---

## 6. 配置契约全景演进设计 (`config.json` v2)

为了平滑兼容旧配置并承载多协议、链式代理、分流规则与透明网关，配置结构升级为 v2 契约规范：

```json
{
  "$schema": "https://linuxwebtool.local/schemas/socks5-forwarder-v2.json",
  "version": 2,
  "transparentProxy": {
    "enabled": false,
    "interfaceName": "lwt-tun0",
    "inet4Address": "172.19.0.1/30",
    "sniffHost": true,
    "fallbackOnNoPrivilege": true
  },
  "upstreams": [
    {
      "id": "default-lwt-node",
      "name": "远程 LWT 默认宿主节点",
      "type": "lwt_wss",
      "url": "ws://127.0.0.1:5270/telemetry/metrics",
      "token": "lwt_live_stream_token_8f3a9e",
      "skipTlsVerify": true
    },
    {
      "id": "upstream-us-ss",
      "name": "美国出口节点 (Shadowsocks)",
      "type": "shadowsocks",
      "server": "us.example.com",
      "port": 8388,
      "method": "aes-128-gcm",
      "password": "your_secure_password_here"
    },
    {
      "id": "upstream-hk-socks5",
      "name": "香港跳板节点 (SOCKS5)",
      "type": "socks5",
      "server": "hk.example.com",
      "port": 1080,
      "username": "relay_user",
      "password": "relay_password_here"
    }
  ],
  "chains": [
    {
      "id": "chain-hk-to-us",
      "name": "香港跳板 -> 美国出口 (多级隐私中继)",
      "strategy": "static",
      "hops": ["upstream-hk-socks5", "upstream-us-ss"],
      "mux": true
    },
    {
      "id": "chain-dynamic-fastest",
      "name": "动态最优跳板链",
      "strategy": "best_latency",
      "hopPools": [
        ["upstream-hk-socks5", "default-lwt-node"],
        ["upstream-us-ss"]
      ]
    }
  ],
  "inbounds": [
    {
      "id": "inbound-office-mixed",
      "name": "办公室自适应代理端口",
      "enabled": true,
      "listenPort": 10801,
      "additionalPorts": [10802, 10803],
      "protocol": "mixed",
      "allowedCidr": ["192.168.1.0/24", "127.0.0.1/32"],
      "routePolicy": {
        "directPrivateNetwork": true,
        "rules": [
          { "type": "domain_suffix", "value": ".cn", "action": "direct" },
          { "type": "domain_suffix", "value": ".bilibili.com", "action": "direct" },
          { "type": "ip_cidr", "value": "223.5.5.0/24", "action": "direct" },
          { "type": "domain_keyword", "value": "internal-cloud", "action": "chain", "target": "chain-hk-to-us" }
        ],
        "defaultAction": "proxy",
        "defaultUpstream": "default-lwt-node"
      }
    }
  ]
}
```

---

## 7. 四阶段渐进式落地实施蓝图 (Phased Implementation Roadmap)

系统建议采取**“协议先行、分流驱动、级联增强、网关收尾”**的稳健四阶段演进路线：

```mermaid
timeline
    title SOCKS5 Forwarder 次世代功能演进路线图
    阶段一 : 多上游协议适配矩阵 : 抽象 IOutboundDialer 适配器 : 落地标准 SOCKS5 与 HTTP CONNECT 出站 : 完善多协议健康检查与延迟探测
    阶段二 : 应用层握手感知与分流路由 : 实现 SOCKS5/HTTP 握手目标嗅探 : 落地 IsPrivateNetwork 局域网直连 : 落地 Domain Trie 树与 IP-CIDR 分流引擎
    阶段三 : 链式代理与多跳级联中继 : 组合模式实现 ChainedCompositeOutbound : 实现 DialThrough 流式嵌套升级 : 支持动态跳板池与故障熔断漂移
    阶段四 : TUN 虚拟网卡与透明网关 : 集成 water / gVisor 用户态协议栈 : 读取 LWT_HAS_TUN 实现特权探测与优雅降级 : Webview 控制台一键开关与拓扑可视化
```

### 阶段一：多上游协议适配矩阵（预计 1.5 天）
- **核心任务**：
  1. 重构出站抽象，引入 `IOutboundDialer` 接口与 `OutboundFactory` 策略字典；
  2. 落地 `Socks5Outbound`（支持账密）与 `HttpConnectOutbound`；
  3. 扩充 `health.go`，支持针对不同协议执行存活探测与高精度毫秒延迟测速；
  4. Webview 界面扩充“添加上游节点”弹窗，支持下拉选择协议类型。

### 阶段二：应用层握手感知与智能分流路由（预计 1.5 天）
- **核心任务**：
  1. 升级 `InboundListener`，在接收到新连接时执行轻量 SOCKS5 / HTTP CONNECT 协议握手，解析出真实 `TargetHost` 与 `TargetPort`；
  2. 实现 `IsPrivateNetwork` 算法，默认对局域网及私网 IP 流量开启 `DIRECT` 直连；
  3. 构建轻量级双向 `DomainTrie` 与 `IpCidr` 匹配器；
  4. Webview 规则编辑弹窗新增“分流规则表”，支持用户可视化添加直连域名与代理规则。

### 阶段三：链式代理与多跳级联中继（预计 1.5 天）
- **核心任务**：
  1. 基于组合模式实现 `ChainedCompositeOutbound`；
  2. 各出站适配器实现 `DialThrough(ctx, baseConn, nextTarget)` 方法，支持在已有全双工流上嵌套多层握手；
  3. 实现动态跳板池（Dynamic Hop Pool）与状态机熔断检测；
  4. Webview 增加“代理链可视化看板”，直观呈现跳板跳转路径。

### 阶段四：TUN 虚拟网卡与透明网关（预计 2 天）
- **核心任务**：
  1. 引入 `water` 库管理 Linux `/dev/net/tun` 设备；
  2. 引入 `gVisor/netstack` 在用户态接管 TCP 数据包，直接桥接至分流路由调度器；
  3. 严格遵循 `LWT_HAS_TUN` 环境变量自省，特权不足时平滑降级（记录 Warning 日志，保障常规端口服务正常运行）；
  4. Webview 控制台新增“透明网关开关”卡片与状态指示灯。

---

## 8. 总结与决策建议

1. **架构可行性完全确立**：用户的 4 大诉求不仅技术上完全可行，而且在 LinuxWebTool 当前的微服务体系中有着极佳的落地土壤（特权容器底座已验证可用、双端口元数据已对齐、高并发全双工内存池已就绪）。
2. **消灭技术腐化红线**：通过**适配器模式**解耦多协议、通过**组合模式**统一单跳与多跳、通过**责任链模式**组织分流规则、通过**状态机**治理透明网关特权降级，整个微服务代码结构将保持高度清晰优雅。
3. **建议行动**：优先执行**阶段一（多上游协议）**与**阶段二（智能分流）**，迅速解决用户在日常开发中遇到的“无法连外部 Socks5 节点”和“内网局域网被误代理”两大高频痛点，随后推进阶段三与阶段四！
