# SOCKS5 边缘中继网关进阶演化：常规链接导入、分流模板、自适应链式代理、垂直切片架构与透明网关落地方案

> **文档标识**：`LWT-ARCH-FORWARDER-008`  
> **状态**：已评审 (Approved)  
> **核心领域**：标准代理协议 URI 解释器 (URI Link Parser)、常用分流策略模板库 (Routing Rule Presets)、自适应低时延链式跳板 (Adaptive Multi-Hop Chaining)、垂直切片架构 (Vertical Slice Architecture, VSA)、元数据驱动动态表单 (Metadata-Driven UI)、TUN 虚拟网卡用户态透明网关 (gVisor Netstack TUN Gateway)  
> **适用版本**：LinuxWebTool v2.1.0+ / `plugins/socks5-forwarder`  
> **设计模式**：解析器策略模式 (Parser Strategy Pattern)、模板方法模式 (Template Method Pattern)、观察者与有限状态机模式 (FSM Pattern)、垂直切片架构 (Vertical Slice Architecture)

---

## 1. 执行摘要与架构演进动因

在 SOCKS5 边缘中继网关完成多上游协议（SOCKS5, HTTP, Trojan, VLESS, Shadowsocks, LWT-WSS）、责任链分流与两级健康探针改造后，为了进一步迈向**工业级、开箱即用、具备自主决策能力与零门槛交互的下一代边缘网络网关**，针对用户提出的五大核心演进方向进行系统性头脑风暴与架构建模：

```mermaid
flowchart TD
    subgraph Core ["SOCKS5 边缘中继网关 5 大核心演进维度"]
        D1["1. 常规链接一键导入<br/>(vless://, trojan://, ss://, socks5://, vmess:// 标准 URI 解析与批量导入)"]
        D2["2. 分流规则预设模板<br/>(大陆直连、AI 专线、广告阻断、内网穿透、全量出海模板库)"]
        D3["3. 链式代理双模演进<br/>(手动静态拓扑模式 vs 动态低时延自愈优选模式)"]
        D4["4. 架构重塑: 垂直切片与适配器<br/>(Vertical Slice 彻底消除霰弹修改，元数据驱动前端动态表单)"]
        D5["5. 透明代理网关 (TUN / TProxy)<br/>(基于 /dev/net/tun 与用户态 gVisor 协议栈，前端安全开关受控集成)"]
    end

    D1 --> UX["极简用户体验 (Zero-Barrier UX)"]
    D2 --> UX
    D3 --> Perf["自愈韧性与性能 (Autonomous Resilience)"]
    D4 --> Arch["高内聚低耦合 (Clean Extensibility)"]
    D5 --> Net["全局流量接管 (Global Gateway Mode)"]
```

---

## 2. 议题 1：全协议标准 URI 链接一键解析与批量导入引擎

### 2.1 痛点与现有交互瓶颈
当前在 Webview 控制台添加上游节点时，用户必须手动在多个表单项中逐一输入服务器 IP、端口、UUID、密码、SNI、加密算法等。而在实际运维中，业界通用且跨客户端流通的均为标准 URI 链接（如 `vless://...#东京1`）。缺乏链接导入功能导致录入效率低下且极易输错。

### 2.2 业界主流链接规范与解析矩阵 (Standard URI Specifications)

根据 IETF RFC 3986 及主流客户端（Xray、V2Ray、Shadowsocks SIP002、Clash）协议链接规范，建立标准映射矩阵：

| 协议 Scheme | 标准 URI 格式规范 | 核心提取字段 | 示例 |
| :--- | :--- | :--- | :--- |
| **`vless://`** | `vless://{uuid}@{host}:{port}?security={tls\|none}&sni={sni}&type={tcp\|ws}#{tag}` | `UUID`, `Server`, `Port`, `SNI`, `SkipTLS`, `Name` | `vless://01234567-89ab-cdef-0123-456789abcdef@node.example.com:443?security=tls&sni=node.example.com#HK-Node` |
| **`trojan://`** | `trojan://{password}@{host}:{port}?security=tls&sni={sni}#{tag}` | `Password`, `Server`, `Port`, `SNI`, `SkipTLS`, `Name` | `trojan://sk-proj-test-12345@trojan.example.com:443?sni=trojan.example.com#JP-Trojan` |
| **`ss://`** | `ss://{base64(method:password)}@{host}:{port}#{tag}`<br/>或 SIP002: `ss://{base64(method:password@host:port)}#{tag}` | `Method`, `Password`, `Server`, `Port`, `Name` | `ss://YWVzLTI1Ni1nY206c2VjcmV0QDEyNy4wLjAuMTo4Mzg4#US-SS` |
| **`socks5://`** | `socks5://{username}:{password}@{host}:{port}#{tag}`<br/>或 `socks5://{host}:{port}#{tag}` | `Server`, `Port`, `Username`, `Password`, `Name` | `socks5://proxyuser:proxypass@1.2.3.4:1080#Socks5-Auth` |
| **`http://` / `https://`** | `http(s)://{username}:{password}@{host}:{port}#{tag}` | `Server`, `Port`, `Username`, `Password`, `TLS`, `Name` | `http://user:pwd@gateway.local:8080#Http-Corporate` |
| **`vmess://`** | `vmess://{base64(json)}` | JSON: `add`, `port`, `id`, `aid`, `net`, `tls`, `ps` | `vmess://eyJhZGQiOiIxLjIuMy40IiwicG9ydCI6NDQzLCJpZCI6Ii4uLiJ9` |

### 2.3 解析器策略模式设计 (Parser Strategy Pattern)

```mermaid
classDiagram
    direction TB
    class IProtocolLinkParser {
        <<interface>>
        +Scheme() string
        +CanParse(rawLink string) bool
        +Parse(rawLink string) (UpstreamNode, error)
    }

    class LinkParserRegistry {
        -parsers map[string]IProtocolLinkParser
        +Register(parser IProtocolLinkParser)
        +Parse(rawLink string) (UpstreamNode, error)
        +ParseBatch(rawText string) ([]UpstreamNode, []error)
    }

    class VlessLinkParser { }
    class TrojanLinkParser { }
    class ShadowsocksLinkParser { }
    class Socks5LinkParser { }
    class HttpLinkParser { }
    class VmessLinkParser { }

    IProtocolLinkParser <|.. VlessLinkParser
    IProtocolLinkParser <|.. TrojanLinkParser
    IProtocolLinkParser <|.. ShadowsocksLinkParser
    IProtocolLinkParser <|.. Socks5LinkParser
    IProtocolLinkParser <|.. HttpLinkParser
    IProtocolLinkParser <|.. VmessLinkParser

    LinkParserRegistry o-- IProtocolLinkParser : 聚合管理所有解析策略
```

#### 双轨制解析实施规范：
1. **前端即时解析（Zero-Latency ESM Parser）**：
   - 在 Webview 模态框提供【一键解析导入】按钮与粘贴区，通过客户端原生 JavaScript 毫秒级提取字段并自动回填各表单项，用户可复核与微调后再点击保存；
2. **后端统一 REST API 批量导入（Batch Persistence API）**：
   - 提供 `POST /api/upstreams/import-links`，支持从多行文本框中一次性解析、校验并持久化数十个节点，自动去重与排重。

---

## 3. 议题 2：常用分流规则预设模板库与开箱即用矩阵

### 3.1 痛点与现有交互瓶颈
目前分流规则配置需要用户手动编写 `domain_suffix: .cn -> direct`，对于不熟悉语法规则的运维人员门槛较高，容易因手误产生语法异常。

### 3.2 五大开箱即用常用分流模板设计

```mermaid
graph TD
    Templates["分流模板库 (Routing Templates)"]
    
    Templates --> T1["1. 基础平衡直连模板<br/>(Default Balanced)"]
    Templates --> T2["2. AI 研发专线分流模板<br/>(AI Dedicated Gateway)"]
    Templates --> T3["3. 强力广告与隐私拦截模板<br/>(AdBlock & Anti-Tracking)"]
    Templates --> T4["4. 全量出海代理模板<br/>(Global Proxy except LAN)"]
    Templates --> T5["5. 极简白名单模式<br/>(Minimal Whitelist)"]

    T1 --> R1["私网直连 + 国内域名/公共DNS直连 + 广告拦截 + 默认代理"]
    T2 --> R2["OpenAI/Claude/Copilot 走专用节点 + 大陆直连 + 默认代理"]
    T3 --> R3["拦截 500+ 常见广告与统计上报 + 其余正常出站"]
    T4 --> R4["私网直连 + 其余全部走代理出海"]
    T5 --> R5["默认全部直连，仅特定被阻断域名走代理"]
```

#### 模板规则详细定义（契约规范）：

```json
{
  "templates": [
    {
      "id": "tpl_balanced",
      "name": "标准平衡分流 (推荐)",
      "description": "局域网与国内主流服务直连，拦截广告隐私追踪，境外服务走代理",
      "directPrivateNetwork": true,
      "defaultAction": "proxy",
      "rules": [
        { "type": "domain_keyword", "value": "adservice", "action": "reject" },
        { "type": "domain_keyword", "value": "telemetry", "action": "reject" },
        { "type": "domain_keyword", "value": "tracking", "action": "reject" },
        { "type": "domain_suffix", "value": ".cn", "action": "direct" },
        { "type": "domain_suffix", "value": "bilibili.com", "action": "direct" },
        { "type": "domain_suffix", "value": "qq.com", "action": "direct" },
        { "type": "domain_suffix", "value": "aliyun.com", "action": "direct" },
        { "type": "ip_cidr", "value": "223.5.5.0/24", "action": "direct" },
        { "type": "ip_cidr", "value": "119.29.29.29/32", "action": "direct" },
        { "type": "ip_cidr", "value": "114.114.114.114/32", "action": "direct" }
      ]
    },
    {
      "id": "tpl_ai_developer",
      "name": "AI 研发与模型加速专线",
      "description": "OpenAI、Claude、Copilot 专属路由分流，大陆服务直连",
      "directPrivateNetwork": true,
      "defaultAction": "proxy",
      "rules": [
        { "type": "domain_suffix", "value": "openai.com", "action": "proxy" },
        { "type": "domain_suffix", "value": "chatgpt.com", "action": "proxy" },
        { "type": "domain_suffix", "value": "oaistatic.com", "action": "proxy" },
        { "type": "domain_suffix", "value": "oaiusercontent.com", "action": "proxy" },
        { "type": "domain_suffix", "value": "anthropic.com", "action": "proxy" },
        { "type": "domain_suffix", "value": "claude.ai", "action": "proxy" },
        { "type": "domain_suffix", "value": "githubcopilot.com", "action": "proxy" },
        { "type": "domain_suffix", "value": ".cn", "action": "direct" }
      ]
    },
    {
      "id": "tpl_global_proxy",
      "name": "全局代理出海模式",
      "description": "仅保留局域网私网直连，其余所有流量均通过代理网关转发",
      "directPrivateNetwork": true,
      "defaultAction": "proxy",
      "rules": []
    }
  ]
}
```

### 3.3 前端交互模式：一键套用与差异化合并
在入站编辑弹窗中增加下拉选择框：【📋 快速套用预设模板】。选中后，系统自动在下方规则编辑区填充格式化规则，并保留用户追加自定义规则的自由度。

---

## 4. 议题 3：链式代理双模演进：手动严格模式 vs 自动自愈优选模式

### 4.1 手动模式 (Manual Strict Chaining)
- **运行机制**：用户在配置界面中显式指定跳板节点的固定顺序：
  $$\text{Hop 1 (前置接入中转)} \longrightarrow \text{Hop 2 (境外落地节点)} \longrightarrow \text{目标服务器}$$
- **行为特征**：严格确定性（Deterministic）。若中间任意一跳发生物理故障或连接超时，该次代理请求立即宣告失败并返回明确的跳数错误（如 `链式代理第 2 跳握手失败`），绝不私自跨跳。

### 4.2 自动模式：全网格动态自愈 (Adaptive Full-Mesh Chaining)
针对需要“高可用（High Availability）与最低时延保障”的企业级场景，引入**自适应链路编排策略**：

```mermaid
sequenceDiagram
    autonumber
    participant Client as 入站客户端
    participant Supervisor as 链式拓扑调度监控器 (AdaptiveSupervisor)
    participant Pool1 as Hop 1 候选节点池 (亚太中转)
    participant Pool2 as Hop 2 候选节点池 (美西落地)
    participant FSM as 链路健康状态机

    Supervisor->>Pool1: 并发异步握手测速 (Ping RTT)
    Supervisor->>Pool2: 并发异步握手测速 (Ping RTT)
    Supervisor->>Supervisor: 计算笛卡尔积综合时延: Min(RTT1 + RTT2)
    Supervisor->>FSM: 激活最优链路 (Hop1_B -> Hop2_A)
    
    Client->>Supervisor: 发起代理请求
    Supervisor->>Client: 通过激活链路流畅转发

    Note over Pool2,FSM: 异常突发: Hop2_A 遭遇海外网络丢包/熔断
    Supervisor->>FSM: 探测到连续 2 次连接失败，状态转为 Failing
    FSM->>Supervisor: 触发秒级自愈: 平滑重组为 (Hop1_B -> Hop2_B)
    Client->>Supervisor: 后续连接无感迁移，零业务中断！
```

### 4.3 增强子模式：固定落地出口自适应模式 (Pinned Exit Node in Adaptive Chaining)

> [!IMPORTANT]
> **真实生产痛点 (The Fixed Egress IP Dilemma)**：
> 在许多严肃生产场景中，**最终的落地出口 IP 绝对不能频繁漂移**：
> 1. **IP 白名单访问**：企业内网安全网关、云服务安全组、特定 API 服务只放行固定的外部 IP 地址；
> 2. **风控与防封禁**：流媒体、ChatGPT/Claude、银行与交易所账户对频繁变更出口 IP（IP 跳动/异地登录）极度敏感，容易触发二次风控或封号；
> 3. **专属静态宽带资源**：用户特意购买了高成本的“原生静态住宅 IP”作为固定出口，但该出口直连国内线路质量极差、丢包率高，必须依赖优质中继加速。

#### 1. 架构拓扑与动态调度机制：
为了完美平衡“出口 IP 恒定”与“链路高质量自愈”，在自动模式中支持配置**可选固定出口节点 (`fixedExitNodeId`)**：

```mermaid
flowchart LR
    subgraph InboundZone ["本地入站"]
        Client["客户端应用<br/>(浏览器/应用)"]
    end

    subgraph MidRelayPool ["前置跳板候选池 (动态自适应优选)"]
        Hop1["香港 CN2 中继<br/>RTT: 18ms"]
        Hop2["日本 BGP 中继<br/>RTT: 35ms"]
        Hop3["新加坡 中继<br/>RTT: 52ms"]
    end

    subgraph FixedExitZone ["固定落地出口 (Pinned Egress)"]
        FixedExit["🇺🇸 美国静态住宅节点<br/>(固定落地 IP: 198.51.100.22)"]
    end

    subgraph Internet ["最终互联网端点"]
        Target["OpenAI / 银行 / 企业专网<br/>(仅识别固定落地 IP)"]
    end

    Client -->|请求接管| MidRelayPool
    Hop1 -.->|优选第一跳: 最佳链路| FixedExit
    Hop2 -.->|备选第一跳: 故障秒级接管| FixedExit
    Hop3 -.->|备选第一跳: 降级候选| FixedExit
    FixedExit ==>|恒定静态出口 IP| Target

    style FixedExit fill:#2e7d32,stroke:#1b5e20,stroke-width:2px,color:#fff
```

#### 2. 最优时延评估算法与数学模型：
当用户指定了 `fixedExitNodeId` 时，调度器的优化目标函数退化为单变量求极值，计算复杂度大幅下降：
$$\text{OptimalHop} = \arg\min_{h \in \text{HopCandidates}} \Big( RTT(\text{Client} \to h) + RTT(h \to \text{FixedExit}) \Big)$$

- **前置跳板故障转移 (Hop Failover)**：若当前活跃的前置跳板（如香港 CN2）发生丢包或断线，调度器在 **500ms 内** 无感将后续连接切至备选前置跳板（如日本 BGP），而**对外暴露的出口落地 IP 始终为该固定节点，业务 Session 永不中断**！
- **落地出口基准锚定 (Egress Health Anchor)**：状态机以固定出口节点的生命周期为锚点。若固定出口节点自身离线（如机器宕机），状态机直接触发 `CRITICAL_EGRESS_DOWN` 熔断告警，拒绝无意义的前置跳板盲目轮询，杜绝网络雪崩。

#### 3. 配置模型与数据契约规范 (JSON Schema)：
```json
{
  "chainConfig": {
    "enabled": true,
    "strategy": "auto",
    "description": "美西固定住宅出口 + 亚太优质中继动态自适应加速",
    "fixedExitNodeId": "node-us-residential-static-01",
    "hopCandidates": [
      "node-hk-cn2-01",
      "node-jp-bgp-02",
      "node-sg-direct-01"
    ],
    "exitCandidates": [],
    "probeIntervalSeconds": 15,
    "failoverThreshold": 2,
    "rttWeight": 0.7,
    "lossWeight": 0.3
  }
}
```
*注：当 `fixedExitNodeId` 留空（`null` 或 `""`）时，系统自动回退至全网格双候选池优选模式（`exitCandidates` 生效）。*

#### 4. 前端 Webview 交互设计：
在链式代理配置弹窗中，当用户切换至【自动自适应模式】时，界面呈现：
- **固定落地出口节点（可选）**：下拉单选框。
  - 选项首项：`🌐 自动全网格优选 (不固定出口)`；
  - 其余选项：按上游节点列表列出已保存的出口节点（如 `🇺🇸 美国原生住宅 IP (198.51.100.22)`、`🇯🇵 东京企业专网出口` 等）；
- **动态拓扑示意图**：实时根据下拉选择，动态更新拓扑预览：
  - 若选择固定出口：呈现 `客户端 ➔ [亚太跳板池 (3个候选自适应)] ➔ [固定落地: 美国原生住宅] ➔ 目标`；
  - 辅助提示：`💡 已锁定落地出口 IP，系统将仅在中间跳板中自动寻优与故障无感自愈，适合 IP 白名单与防封控业务。`

#### 关键算法与设计模式：
1. **策略模式 (`IProxyChainStrategy`)**：
   - `ManualStaticChainStrategy`：固定顺序穿透；
   - `PinnedExitAdaptiveChainStrategy`：固定落地出口 + 前置跳板动态寻优自愈；
   - `FullMeshAdaptiveChainStrategy`：全动态网格双向笛卡尔积寻优。
2. **有限状态机 (`ChainStateFSM`)**：
   - 节点池状态迁移：`Optimal (最优)` $\rightarrow$ `Degraded (轻度抖动)` $\rightarrow$ `CircuitBreaker (熔断摘除)` $\rightarrow$ `Recovering (探活自愈)`。

---

## 5. 议题 4：垂直切片架构 (VSA) 与元数据驱动适配器策略模式革新

### 5.1 架构反模式审视：霰弹式修改 (Shotgun Surgery)
在当前代码库中，虽然已具备抽象接口，但新增一个协议需要跨文件修改：
1. `config.go`：修改 `UpstreamNode` 结构体，增加该协议特有字段；
2. `outbound.go`：在注册表中追加构造函数；
3. `outbound_secure.go`：编写 Dialer 实现；
4. `index.html`：手写该协议专属的 DOM 容器与控件；
5. `app.js`：编写该协议的表单显隐与序列化分支。

### 5.2 垂直切片架构革新 (Vertical Slice Architecture)
将系统从“按技术分层”重塑为“**按业务/协议垂直切片**”：

```text
plugins/socks5-forwarder/cmd/socks5-forwarder/
├── core/                       # 微内核基础设施
│   ├── outbound.go             # IOutboundDialer 统一核心接口与 OutboundRegistry
│   ├── router.go               # 责任链分流决策中枢
│   ├── health.go               # 两级分层健康探针引擎
│   └── manager.go              # Profile 生命周期管控
└── protocols/                  # 协议垂直切片目录 (各切片高度内聚自洽)
    ├── vless/
    │   ├── dialer.go           # VLESS 拨号器实现
    │   ├── parser.go           # vless:// 链接解析器
    │   ├── schema.go           # VLESS 元数据与表单 Schema
    │   └── vless_test.go       # VLESS 专属测试切片
    ├── trojan/
    │   ├── dialer.go           # Trojan 拨号器实现
    │   ├── parser.go           # trojan:// 链接解析器
    │   └── schema.go           # Trojan 表单 Schema
    └── shadowsocks/
        ├── dialer.go           # Shadowsocks AEAD 实现
        └── parser.go           # ss:// 链接解析器
```

### 5.3 元数据驱动前端动态表单 (Metadata-Driven Dynamic UI)
后端通过 `GET /api/protocols/schema` 输出协议元数据描述，前端完全通过 Schema 自动渲染控件：

```json
{
  "protocols": [
    {
      "type": "vless",
      "displayName": "VLESS (TLS + UUID)",
      "fields": [
        { "name": "server", "label": "服务器主机/IP", "type": "text", "required": true },
        { "name": "port", "label": "端口", "type": "number", "default": 443, "required": true },
        { "name": "uuid", "label": "用户 UUID", "type": "text", "placeholder": "32位十六进制或带连字符", "required": true },
        { "name": "sni", "label": "TLS SNI", "type": "text", "required": false },
        { "name": "skipTlsVerify", "label": "跳过证书校验", "type": "boolean", "default": false }
      ]
    }
  ]
}
```
**收益**：未来新增任何协议（如 Hysteria2、WireGuard、TUIC），**前端 0 行代码修改**，实现真正的开闭原则（OCP）！

---

## 6. 议题 5：透明代理网关模式 (TUN / TProxy) 落地可行性与集成规范

### 6.1 现状诊断与实机环境实证
为什么当前前端没有开启透明代理选项？
- **历史原因**：前序版本专注于构建坚固的四/七层端口中继（SOCKS5/HTTP）；
- **实机诊断结果**：
  我们通过远程诊断工具对 `linuxwebtool-dev` 容器进行了底层探查：
  - `/dev/net/tun` 设备**100% 存在且权限完全放开**（`crw-rw-rw- 1 root root 10, 200`）；
  - 容器启动参数中已显式注入 `--privileged` 与 `--device=/dev/net/tun`；
  - 容器内的 Linux 内核具备完整的 TUN/TAP 驱动支持！

### 6.2 用户态协议栈 vs 内核级 TProxy 架构抉择

```mermaid
flowchart LR
    subgraph ModeA ["方案 A: Linux 内核 iptables / nftables TProxy"]
        A1["物理流量"] --> A2["iptables PREROUTING 规则注入"]
        A2 --> A3["内核级套接字透传"]
        A3 --> A4["优点: 零拷贝，极高吞吐<br/>缺点: 极易与宿主 Docker / EasyTier 发生路由冲突"]
    end

    subgraph ModeB ["方案 B: 用户态 TUN + Google gVisor Netstack (推荐主流)"]
        B1["虚拟网卡 lwt-tun0"] --> B2["Google gVisor 用户态 TCP/IP 协议栈"]
        B2 --> B3["无损解包为 TCP/UDP 数据流"]
        B3 --> B4["直接喂入 RuleRoutingDispatcher 分流引擎"]
        B4 --> B5["优点: 100% 容器隔离，零宿主网络污染，安全可控"]
    end
```

**结论**：强烈推荐**方案 B（用户态 TUN + gVisor netstack）**。该方案也是 Sing-box 与 Clash 商业版的核心技术方案，彻底消除因滥用宿主 iptables 导致宿主机断网的致命风险。

### 6.3 前端控制面交互设计与安全守卫 (Guardrails)

```mermaid
stateDiagram-v2
    [*] --> PrivilegeCheck: 进入网关设置或加载控制台
    
    PrivilegeCheck --> TunAvailable: 检测到 /dev/net/tun 存在且具备 CAP_NET_ADMIN
    PrivilegeCheck --> TunDisabled: 无 /dev/net/tun 或未特权挂载
    
    TunDisabled --> GrayedOutUI: 开关置灰锁定，呈现黄色安全告警提示
    
    TunAvailable --> ToggleReady: 渲染正常开光控件 (绿色就绪态)
    ToggleReady --> TunRunning: 用户开启 TUN 透明代理网关
    TunRunning --> TunStopped: 用户关闭透明网关
```

#### 前端交互卡片规范：
- **安全检查指示器**：
  - 若系统检测到特权可用：呈现 `🟢 TUN 设备就绪 (/dev/net/tun)`；
  - 若环境受限：呈现 `⚠️ 当前运行容器未挂载 /dev/net/tun，透明网关已被安全保护（传统端口中继正常可用）`；
- **配置参数**：
  - `tunName`：虚拟网卡接口名（默认 `lwt-tun0`）；
  - `tunCidr`：虚拟网段（默认 `198.18.0.1/16`，Fake-IP 预留段）；
  - `dnsHijack`：是否劫持 53 端口 DNS 请求实现域名级精准透明分流；
  - `bypassSubnets`：透明代理旁路绕过网段（默认包含内网私有网段与宿主网段）。

---

## 7. 实施路线图与行动分解 (Implementation Roadmap)

```mermaid
timeline
    title SOCKS5 边缘中继网关演进实施路线图
    第一阶段 : 核心协议链接解析器 : 实现 vless://, trojan://, ss:// 等标准 URI 解释器 : 前端 Webview 增加一键链接导入弹窗
    第二阶段 : 分流模板库与预设加载器 : 落地 5 大开箱即用模板 (平衡、AI、广告拦截等) : 前端下拉框一键套用与差异化合并
    第三阶段 : 链式代理自适应模式 : 引入动态时延监控器与最优组合算法 : 状态机驱动的跳板故障自动熔断与秒级自愈
    第四阶段 : 协议垂直切片重构 : 拆分 protocols 目录，落地元数据驱动 Dynamic UI : 彻底消除新增协议的代码霰弹修改
    第五阶段 : 用户态 TUN 透明代理网关 : 引入 gVisor netstack 驱动 lwt-tun0 : 前端受控开关、特权守卫与全局流量接管验收
```

---

本方案全面打通了从用户交互体验（链接一键导入、规则预设模板）、高可用业务韧性（自适应链式优选）、系统扩展内聚（垂直切片架构）到全局网络接管（用户态 TUN 透明网关）的完整技术脉络，为 LinuxWebTool 打造工业级边缘网络中枢确立了清晰、健壮且安全的演进准则！
