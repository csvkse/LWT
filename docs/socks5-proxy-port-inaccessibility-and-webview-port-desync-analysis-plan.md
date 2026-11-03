# SOCKS5 安全代理微服务端口无法访问与 Webview 模态框端口脱节深度调研与治理方案

> **文档标识**：`DOC-SOCKS5-PORT-DESYNC-20261103`  
> **归属模块**：扩展中心 (Plugins Hub) / 平台网络基础设施 / Webview 视图集成  
> **涉及核心组件**：
> - 插件清单与微服务源码：`plugins/socks5-proxy/plugin.json`、`plugins/socks5-proxy/cmd/socks5-proxy/main.go`、`plugins/socks5-proxy/ui/index.html`
> - 后端调度与端口管理：`PluginPortManager.cs`、`GoRuntimeAdapter.cs`、`ResidentExecutionStrategy.cs`、`PluginsController.cs`
> - 数据契约与模型：`PluginModels.cs` (`PluginInstanceDto`)、`PluginManagementModels.cs`
> - 前端交互组件：`PluginCard.js`、`PluginWebviewModal.js`、`PluginPortSwitchModal.js`  
> **关联架构门禁**：`LinuxArch046_SOCKS5代理网段白名单与域名过滤安全门禁`、`LinuxArch047_Go构建包路径解析与SOCKS5三维安全防御门禁`、`LinuxArch050_插件长时服务快速换端与双栈冲突避让门禁`、`LinuxArch069_微服务Webview控制台端口契约对齐与抗覆盖门禁` (新建)

---

## 一、执行摘要与核心结论

针对用户提出的两个核心问题，经对代码库全链路追踪、网络协议分析与实测推导，核心结论如下：

### 1. SOCKS5 安全代理微服务 socks 端口为什么无法访问？
**直接致命根因**：**Webview 前端与后端 DTO 契约字段脱节，导致界面硬编码回退至未监听的 `:18001`，用户复制的验证命令访问了空端口。**
- 外部配置中，SOCKS5 微服务的默认端口是 **18080**（`plugin.json` 与 `PluginManager.Defaults.cs` 中声明 `network.port = 18080`），Go 微服务进程在启动后实际监听在 `0.0.0.0:18080`；
- 但在 Webview 模态框 (`plugins/socks5-proxy/ui/index.html`) 中，前端尝试通过 `p?.port` 读取端口。而后端 DTO (`PluginInstanceDto`) 的真实属性为 `allocatedPort` 与 `configuredPort`，**根本不存在 `port` 字段**，导致字段取值恒为 `undefined`；
- 加上 IFrame 内部仅从 `localStorage` 读取 Token，未解析父级通过 URL 传递的 `?token=`，在特定隔离场景下请求 401 失败，直接触发静态回退；
- 最终 Webview 模态框不仅在卡片上展示硬编码的 `:18001`，还生成了 `curl -x socks5://127.0.0.1:18001 ...` 等测试命令。用户复制该命令访问 18001 端口，由于操作系统上并无进程监听该端口，直接报 **`Connection Refused`（连接被拒绝）**！
- 此外，还存在 **端口冲突避让导致实际端口变更**、**SOCKS5 四层 TCP 协议被误用 HTTP 浏览器访问**、**三维 ACL 规则拦截** 以及 **Webview 保存配置时抹除 network 节点导致端口随机化** 等潜在连锁诱因。

### 2. 外部配置端口和 Webview 模态框中端口有什么关系？
- **外部配置端口 (ConfiguredPort / Declared Port)**：代表运维管理人员在系统层面的**声明意图（Declared Intent）**，定义在 `plugin.json` 的 `network.port`（如 18080），可在扩展中心卡片点击端口号呼出 `PluginPortSwitchModal` 进行热修改；
- **平台分配端口 (AllocatedPort / Runtime Port)**：代表底层调度器 `PluginPortManager` 在启动微服务前分配的**真实运行端口（Effective Runtime Lease）**。无冲突时等于配置端口；冲突时根据避让策略自动分配新端口（如 18002）；
- **Webview 模态框中端口 (Operational Console Port / Guidance Port)**：代表微服务向操作人员展示的**运行态投影与交互指引（Projected State & Action Guidance）**。
- **理想与现状的割裂**：理论上 Webview 中的端口应忠实反映 `AllocatedPort`，并随端口切换和自动避让实时响应；而**现存系统的真实情况是两者数据流彻底断裂**，外部卡片配置并运行在 18080，Webview 内部却固守静态的 18001，形成了“认知孤岛”。

---

## 二、深度技术调研与根因剖析 (Root Cause Analysis)

### 2.1 问题 1：SOCKS5 端口无法访问的五大根因链条

通过对 `src/` 与 `plugins/` 目录的端到端代码审计，梳理出导致 SOCKS5 端口无法访问的 5 个层次递进的缺陷：

```
+--------------------------------------------------------------------------------------------------+
|                                    SOCKS5 端口无法访问 根因故障树                                   |
+--------------------------------------------------------------------------------------------------+
                                                 |
         +---------------------------------------+---------------------------------------+
         |                                       |                                       |
  [链路 1: 呈现与契约断裂]                [链路 2: 端口调度避让]                  [链路 3: 协议混淆错位]
  Webview 硬编码 18001                     端口冲突被平台避让至 18002             用 HTTP 浏览器直接访问
  DTO 缺少 port 字段映射                  用户仍使用原配置 18080 访问            TCP 握手协议版本校验失败
  生成错误 cURL 连接命令                  连接未监听端口超时/拒绝                 抛出 71 异常并关闭连接
         |                                       |
  [链路 4: 三维安全 ACL 拦截]             [链路 5: 清单配置覆盖抹除]
  入站客户端非 LAN IP 短路               Webview 保存缺少 network 段
  RFC 1929 认证缺失或失败                 重启后落盘 network: null
  出站非内网/防 DNS 重绑定拦截           启动退化为 0.0.0.0:0 (随机端口)
```

#### 根因 1（核心直接 Bug）：前端 DTO 契约脱节与 Webview 静态硬编码回退
- **外部配置事实**：
  在 `plugins/socks5-proxy/plugin.json`：
  ```json
  "network": {
    "mode": "port",
    "port": 18080,
    "portEnv": "SOCKS5_PORT",
    "portConflictPolicy": "avoid"
  }
  ```
  在 `src/LinuxWebTool.Infrastructure/Features/Plugins/PluginManager.Defaults.cs`：
  ```csharp
  Network = new PluginNetworkConfig
  {
      Mode = "port",
      Port = 18080,
      PortEnv = "SOCKS5_PORT",
      PortConflictPolicy = "avoid"
  }
  ```
- **Go 运行时实际绑定**：
  `GoRuntimeAdapter.cs` 将启动参数 `--listen 0.0.0.0:{port}` 替换为实际分配的 `allocatedPort`（18080）。Go 进程实际绑定并监听在 `0.0.0.0:18080`。
- **Webview 前端代码缺陷**：
  在 `plugins/socks5-proxy/ui/index.html` 第 548 行：
  ```javascript
  const res = await fetch('/api/plugins/socks5-proxy', {
    headers: token ? { 'Authorization': 'Bearer ' + token } : {}
  });
  const data = await res.json();
  const p = data?.data || data;
  currentManifest = p?.manifest;

  if (p?.port) { // ❌ BUG: 后端返回的 PluginInstanceDto 字段为 allocatedPort / configuredPort，无 port 属性！
    document.getElementById('proxy-port').textContent = ':' + p.port;
  }
  ```
  因为 `p?.port` 为 `undefined`，该赋值分支永远不会执行！
  而在 HTML 模版中（第 284 行）：
  ```html
  <div class="val" id="proxy-port">:18001</div> <!-- ❌ BUG: 静态写死 18001 -->
  ```
  在第 519 行：
  ```javascript
  const port = document.getElementById('proxy-port').textContent.replace(':', '') || '18001';
  const proxyUrl = 'socks5://' + authPrefix + '127.0.0.1:' + port;
  document.getElementById('curl-preview').textContent = 'curl -x ' + proxyUrl + ' http://127.0.0.1:5270/health';
  ```
  用户在 Webview 模态框中看到的**只有 18001**，复制指引命令测试的也是 `18001`。由于 18001 并无程序监听，操作系统立即响应 RST，报 `Connection Refused`。

#### 根因 2：双栈端口冲突探测与自动避让（Port Avoidance）使实际端口漂移
- 系统中 `PluginPortManager.cs` 具备严格的端口预检逻辑：
  1. 检查内部插件分配表；
  2. 读取操作系统内核 `IPGlobalProperties.GetActiveTcpListeners()`；
  3. 执行 IPv4 与 IPv6 原生套接字独占试探绑定（`ExclusiveAddressUse = true`）。
- 若宿主机上已有其他服务（如之前的测试进程、历史遗留孤儿进程或生产容器）占用了 `18080`，`PluginPortManager` 会执行 `avoid` 避让策略，自动分配如 `18002` 作为 `AllocatedPort`。
- 此时 Go 微服务在 `18002` 上成功启动，卡片上显示 `:18002 [避让]`；但如果使用者未注意卡片变动，依然使用配置的 18080 或模态框中的 18001 发起连接，由于端口不符，同样无法访问。

#### 根因 3：协议错位：四层 TCP SOCKS5 代理不可直接通过 HTTP 浏览器访问
- SOCKS5 规范定义于 **RFC 1928**，属于第四/五层会话层代理，客户端在 TCP 连接建立后必须立即发起握手包：
  `+----+----------+----------+`
  `|VER | NMETHODS | METHODS  |`
  `+----+----------+----------+`
  `| 1  |    1     | 1 to 255 |`
  `+----+----------+----------+`
- 如果使用者在浏览器地址栏直接输入 `http://127.0.0.1:18080/` 或尝试通过 Web 反向代理网关访问：
  浏览器发送的是明文 HTTP 请求：`GET / HTTP/1.1\r\n...`，首字节为 ASCII `'G'` (`0x47`)。
- 在 `plugins/socks5-proxy/cmd/socks5-proxy/main.go` 第 404 行：
  ```go
  if header[0] != socks5Version { // socks5Version == 0x05
      return fmt.Errorf("unsupported protocol version: %d", header[0])
  }
  ```
  Go 服务端收到 `0x47`，协议校验不匹配，立即关闭连接。浏览器端呈现为“连接已重置（ERR_CONNECTION_RESET）”或“未发送任何数据（ERR_EMPTY_RESPONSE）”，误以为服务挂死或不可访问。

#### 根因 4：三维安全防御策略（ACL）与身份认证机制拦截阻断
在 `plugins/socks5-proxy/cmd/socks5-proxy/main.go` 与 `acl.go` 中，设计了严格的三维防御机制：
1. **维度一（身份认证模式 RFC 1929）**：
   若启用了 `--auth-mode password`，客户端必须完成明文子协商（`0x01`）。若使用无密码的 `curl -x socks5://127.0.0.1:18080` 访问，握手直接被拒（返回 `0xFF` No Acceptable Methods）。
2. **维度二（入站客户端来源 IP 白名单）**：
   在 TCP 接收首包前执行快速检查：
   ```go
   if s.acl != nil && !s.acl.IsClientAllowed(conn.RemoteAddr().String()) {
       s.logger.Warn("client rejected by ingress whitelist policy", "client", conn.RemoteAddr().String())
       _ = conn.Close()
       continue
   }
   ```
   若预设被设置为 `loopback`，而客户端来源于局域网其他机器、Docker 网桥（如 172.17.x.x）或跨宿主机访问，直接被短路断开。
3. **维度三（出站目标网段与域名白名单 + Anti-DNS-Rebinding）**：
   默认预设 `lan` 仅放行私网网段与 `.local, .internal, .lan, localhost` 域名。若客户端试图代理访问公网网站（如 `curl -x socks5://127.0.0.1:18080 http://www.google.com`），微服务会返回 SOCKS5 状态码 `0x02` (Rule violation by network policy)，表现为“代理拒绝连接”。

#### 根因 5：Webview「保存配置并重启」可能导致 network 清单覆盖丢失
在 `plugins/socks5-proxy/ui/index.html` 的 `saveAcl()` 函数中：
```javascript
const newArgs = [
  '--listen', '0.0.0.0:{port}',
  ...
];
const manifest = {
  ...(currentManifest || {}),
  id: 'socks5-proxy',
  name: 'SOCKS5 安全代理微服务',
  arguments: newArgs
};
const res = await fetch('/api/plugins', {
  method: 'POST',
  body: JSON.stringify({ manifest })
});
```
- 如果 Webview IFrame 打开时由于 Token 未就绪或同源权限隔离，`loadPluginInfo()` 发生异常使得 `currentManifest` 为 `null`；
- 用户在页面上点击“保存三维策略并重启生效”，提交的 `manifest` 中**没有 `network` 属性**！
- 后端 `PluginManager.CreateOrUpdateAsync` 将此缺少 `network` 的清单原样持久化到磁盘 `plugin.json`。
- 随后执行重启时，由于 `manifest.Network == null`，调度器不分配端口（`allocatedPort = null`），`GoRuntimeAdapter` 将 `{port}` 替换为 `"0"`，导致 Go 进程执行 `--listen 0.0.0.0:0`，绑定到操作系统的随机动态高位端口（如 48921），且该端口未被系统感知，用户配置的 18080 端口彻底失效！

---

### 2.2 问题 2：外部配置端口和 Webview 模态框中端口有什么关系？

#### 2.2.1 架构生命周期与数据流模型
外部配置端口、调度分配端口与 Webview 模态框中的端口构成了一个**多层递进的生命周期关系**：

```mermaid
flowchart TD
    subgraph S1["1. 外部配置层 (Configuration Layer)"]
        P1["plugin.json (network.port = 18080)"] --> D1["声明期望端口 (ConfiguredPort)"]
        UI1["PluginPortSwitchModal (换端弹窗)"] -->|POST /Port/Switch| D1
    end

    subgraph S2["2. 调度与冲突解决层 (Supervision & Allocation)"]
        D1 --> PM["PluginPortManager.AllocatePortWithDetails"]
        PM -->|双栈套接字探活| CHK{"端口是否被占用?"}
        CHK -->|未占用| AP1["AllocatedPort = 18080 (正常)"]
        CHK -->|已被占用 (avoid策略)| AP2["AllocatedPort = 18002 (自动避让)"]
    end

    subgraph S3["3. 运行引擎适配层 (Runtime Adapter)"]
        AP1 & AP2 --> GRA["GoRuntimeAdapter.BuildStartSpec"]
        GRA -->|替换宏参数| ARG["--listen 0.0.0.0:{port} -> 0.0.0.0:18080/18002"]
        GRA -->|注入环境变量| ENV["SOCKS5_PORT=18080/18002, PORT=..."]
        ARG & ENV --> PROC["Go 子进程 (socks5-proxy)"]
    end

    subgraph S4["4. 平台 DTO 透出层 (Contract & DTO)"]
        AP1 & AP2 --> DTO["PluginInstanceDto\n- AllocatedPort: 18080/18002\n- ConfiguredPort: 18080\n- IsPortAvoided: true/false"]
    end

    subgraph S5["5. 前端展示层 (Frontend Presentation)"]
        DTO --> CARD["PluginCard.js (插件卡片)\n显示: :18080 或 :18002 避让"]
        DTO -.->|期望正确映射| WV_EXP["Webview 模态框 (理想投影)\n动态展示实际监听端口与连接指引"]
        DTO --x|实际现状 (字段未对齐)| WV_ACT["Webview ui/index.html (现状断裂)\n读取 p.port 为 undefined -> 跌落回退硬编码 :18001"]
    end

    classDef normal fill:#0284c7,stroke:#0369a1,color:#fff;
    classDef warning fill:#d97706,stroke:#b45309,color:#fff;
    classDef error fill:#dc2626,stroke:#b91c1c,color:#fff;
    class P1,D1,PM,AP1,GRA,ARG,ENV,PROC,DTO,CARD,WV_EXP normal;
    class AP2 warning;
    class WV_ACT error;
```

#### 2.2.2 角色分工与概念对比表

| 概念维度 | 外部配置端口 (ConfiguredPort) | 平台分配端口 (AllocatedPort) | Webview 模态框中端口 (GuidancePort) |
| :--- | :--- | :--- | :--- |
| **所属载体** | `plugin.json` 中的 `network.port` | 运行时内存分配表与进程真实监听端点 | `ui/index.html` DOM 元素 `#proxy-port` |
| **语义本质** | **声明期望 (Declared Intent)** | **有效租约 (Effective Lease)** | **状态投影与指引 (Status & Guidance)** |
| **是否会改变** | 用户手动修改或 API 保存时改变 | 启动时动态决定（遇到冲突避让自动递增） | 应始终追随 `AllocatedPort` 动态渲染 |
| **作用范围** | 作为端口分配器的优先级输入依据 | 注入进程启动参数（`--listen`）与环境变量 | 生成 cURL、Git、环境变量配置代码块供人复制 |
| **脱节时后果** | 用户预期该端口能连通 | 真实监听端口不同，导致直连失败 | **产生严重误导，复制给出的命令 100% 报错** |

---

## 三、现有开源库与借鉴调研 (Library & Technology Survey)

在优化 SOCKS5 代理微服务、端口动态治理以及 Webview 父子窗口通信时，业界已有诸多成熟的开源库与技术方案可供借鉴：

### 3.1 Go 语言 SOCKS5 核心实现库对比

| 库名 / 仓库 | Star 数 | 架构特点 | 优缺点分析 | 适用度评估 |
| :--- | :---: | :--- | :--- | :--- |
| **`github.com/things-go/go-socks5`** | ~1.5k | HashiCorp 现代升级分支；支持 Context 取消传播、自定义 RuleSet、CredentialStore 与自定义 DNS 解析器。 | **优点**：规范完整，接口设计优雅，生产级健壮性。<br>**缺点**：引入外部依赖，脱离单文件/零依赖原则。 | **极高参考价值**（参考其 RuleSet 与 Credential 接口设计）。 |
| **`github.com/armon/go-socks5`** | ~4.2k | HashiCorp 经典标准库实现，SOCKS5 Go 生态基石。 | **优点**：极简、稳定。<br>**缺点**：维护变慢，缺少部分现代网络 context 特性。 | **高参考价值**。 |
| **`github.com/txthinking/socks5`** | ~3.8k | 高性能纯 SOCKS5 实现，支持 TCP/UDP ASSOCIATE、TCP BIND、用户认证与超时管理。 | **优点**：性能强悍，经过大规模代理网络验证。<br>**缺点**：部分 API 封装较厚。 | **中等参考价值**。 |
| **`golang.org/x/net/proxy`** | 官方 | Go 官方扩展库，包含标准的 SOCKS5 客户端 Dialer。 | **优点**：官方出品，用于端到端自动化测试与连通性自测无敌。<br>**缺点**：仅有 Client 端，未暴露 Server 端。 | **测试必备**（用于回归测试套件）。 |

> **架构权衡结论**：对于本系统中的 `socks5-proxy` 微服务，**继续保持纯 Go 标准库（`net`, `io`, `crypto/subtle`）自研是最佳策略**。它保证了源码在任何 Linux/Alpine 离线环境下均可在 200ms 内完成 JIT 增量编译，无需通过公网拉取 `go.mod` 依赖，杜绝供应链风险与编译卡死。重点在于**完善其与宿主控制面的契约对齐**。

### 3.2 父容器与 Webview IFrame 跨上下文状态通信库

| 库名 | 核心机制 | 适用场景 | 借鉴点 |
| :--- | :--- | :--- | :--- |
| **`penpal`** (Promise-based postMessage) | 基于 `window.postMessage` 封装的双向 RPC 通信库，通过 Promise 调用对端方法并返回结果。 | 父应用与嵌入式 IFrame 之间解耦通信。 | **解决 Webview 端口脱节的最优模式**：父容器在打开模态框时，直接通过 postMessage 发送 `{ type: 'INIT_PORT', port: plugin.allocatedPort }`，无需 Webview 额外发起 fetch。 |
| **`postmate`** | 类似 Penpal，父页面挂载子页面时握手并同步 Model 数据模型。 | 复杂沙箱隔离 UI。 | 支持父向子响应式推送 state 变化。 |
| **原生 `BroadcastChannel` API** | 浏览器原生同源上下文消息通道，支持 Tab、Window、IFrame 之间多播广播。 | 同源多页面同步端口与 Token。 | 零外部依赖，现代浏览器 100% 支持。 |

---

## 四、业界主流方案对比与参考 (Industry Comparison)

调研业内成熟的轻量容器管理系统、微服务编排面板与插件化桌面系统（如 **Portainer**、**1Panel**、**CasaOS**、**VSCode Webview**、**Docker Desktop Extensions**），它们在处理**配置端口、动态分配与嵌入式 UI**时的设计模式如下：

### 4.1 业界主流方案对比矩阵

| 架构维度 | 方案 A: VSCode / Docker Desktop 插件总线模型 | 方案 B: Portainer / K8s 服务声明与发布模型 | 方案 C: 1Panel / CasaOS 应用中心模板模型 | LinuxWebTool 当前演进方案 (推荐最佳实践) |
| :--- | :--- | :--- | :--- | :--- |
| **端口管理抽象** | 插件不直接分配宿主端口，由主宿主代理网络通道（Stream RPC / Pipe）。 | 显式区分 `ContainerPort`、`HostPort`、`NodePort` 三种端口状态。 | 在应用 Compose 中提供模板占位符 `${APP_PORT}`，启动前检测并持久化。 | **三态融合模型**：配置声明端口 (`ConfiguredPort`) + 运行时租约端口 (`AllocatedPort`) + 避让标志 (`IsPortAvoided`)。 |
| **Webview 端口获取** | **禁止 Webview 自行发起 HTTP 请求探测**；父容器在创建 Webview 时通过消息总线将配置压入。 | Webview 通过管理 API 轮询 Service 状态中的 `PublishedPort`。 | Webview 读取全局 localStorage 缓存的环境变量。 | **双通道容错**：优先接收父容器 postMessage 注入；降级时通过修复后的 DTO 字段 (`allocatedPort || port`) 请求 API。 |
| **端口冲突处理** | 宿主自动分配空闲回环端口，通过内部 IPC 映射，外部无需感知。 | 端口冲突直接报错阻止启动，要求用户手工更换。 | 自动探测并自增递增（如 8080 -> 8081），并将新端口写回持久化配置。 | **自动避让 (avoid) 与严格模式 (strict) 可选**；避让后全链路（卡片、DTO、Webview）联动透出黄色避让告警。 |
| **四层与七层指引** | 严格区分 HTTP Web 服务与 TCP Raw Socket，提供专用交互组件。 | 卡片上根据协议类型渲染不同图标（TCP 渲染终端命令，HTTP 渲染链接）。 | 应用商店卡片针对非 HTTP 容器明确注明“TCP 端口”，不显示“进入网页”按钮。 | 在卡片与 Webview 突出标识四层 SOCKS5 协议，提供一键复制命令并屏蔽浏览器误跳。 |

---

## 五、当前系统现有基础设施复用确认 (Reusability Audit)

经全面审查，LinuxWebTool 系统内部已具备大量优秀的现成基础设施，完全无需引入厚重第三方库，即可完美解决此问题：

| 基础设施模块 | 文件位置 | 现有成熟能力 | 改造复用建议 |
| :--- | :--- | :--- | :--- |
| **双栈端口探测与冲突避让器** | `PluginPortManager.cs` | 1. `CheckPort(port)`：结合系统内核监听表与 IPv4/IPv6 原生 Socket 独占绑定试探；<br>2. `AllocatePortWithDetails`：支持 `avoid` 避让与 `strict` 严格模式；<br>3. `GetRecommendedPort`：智能搜索下一个完全空闲端口。 | **100% 复用**。调度能力完备无缺，继续作为端口分配唯一权威源。 |
| **端口检测与切换 API** | `PluginsController.cs` | 1. `GET /api/Plugins/{id}/Port/Check?port=...`<br>2. `POST /api/Plugins/{id}/Port/Switch` | **100% 复用**。已有完善的输入校验和热重启同步逻辑。 |
| **前端端口快速切换弹窗** | `PluginPortSwitchModal.js` | 实时端口探测、防抖、一键应用系统推荐空闲端口、自动平滑重启。 | **100% 复用**。 |
| **运行时宏替换与环境变量** | `GoRuntimeAdapter.cs` | 自动将清单中的 `{port}` 宏替换为实际的 `AllocatedPort`，并注入 `SOCKS5_PORT`、`PORT` 等环境变量。 | **100% 复用**。确保宏替换与环境变量的一致性。 |
| **Webview 模态框宿主** | `PluginWebviewModal.js` | 具备 IFrame 沙箱隔离、页面重载、新标签页打开以及通过 URL 注入 `?token=` 的能力。 | **稍作增强**：在 IFrame `onload` 时通过 `postMessage` 向子窗口下发当前微服务的端口与环境元数据。 |
| **数据契约模型** | `PluginModels.cs` | `PluginInstanceDto` 已包含 `AllocatedPort`, `ConfiguredPort`, `IsPortAvoided`, `PortConflictReason`。 | **增加别名兼容**：增加只读属性 `public int? Port => AllocatedPort ?? ConfiguredPort;`，确保旧代码或前端取 `port` 字段时平滑兼容。 |

---

## 六、具体解决方案与落地实施细节 (Implementation Plan)

### 6.1 修复 1：后端契约对齐与属性兼容 (`PluginModels.cs`)
在 `src/LinuxWebTool.Contracts/Features/Plugins/Contracts/PluginModels.cs` 的 `PluginInstanceDto` 中，添加只读别名属性 `Port`：

```csharp
public sealed record PluginInstanceDto
{
    public required PluginManifest Manifest { get; init; }
    public string Id => Manifest.Id;
    public string Name => Manifest.Name;
    public PluginProfile Profile => Manifest.Profile;
    public PluginRuntime Runtime => Manifest.Runtime;
    public bool IsEnabled => Manifest.IsEnabled;
    public PluginState State { get; init; } = PluginState.Stopped;
    public int? AllocatedPort { get; init; }
    public bool IsPortAvoided { get; init; }
    public int? ConfiguredPort { get; init; }
    
    /// <summary>
    /// 兼容性运行端口：优先取运行时分配端口，未启动时取预设配置端口。
    /// 解决 Webview 控制台及外部客户端序列化取值字段不匹配问题。
    /// </summary>
    public int? Port => AllocatedPort ?? ConfiguredPort;

    public string? PortConflictReason { get; init; }
    public int? ProcessId { get; init; }
    public string? AccessUrl { get; init; }
    public string? GatewayUrl => AccessUrl;
    // ...其余属性保持不变
}
```

### 6.2 修复 2：彻底重构 Webview 控制台前端 (`plugins/socks5-proxy/ui/index.html`)

在 `ui/index.html` 中进行深度加固：
1. **多源 Token 容错获取**：优先从 `window.location.search` 获取 `?token=`，降级从 `localStorage` 获取；
2. **多层端口安全提取**：
   ```javascript
   const activePort = p?.allocatedPort || p?.port || p?.configuredPort || p?.manifest?.network?.port || 18080;
   ```
3. **避让状态动态徽标透出**：若检测到 `p?.isPortAvoided`，界面展示黄色“自动避让”提示，并标明“原配置端口已被外部占用”；
4. **HTML 默认占位符改造**：将硬编码的 `:18001` 修改为 `:...`，拉取成功后动态填入真实端口；
5. **清单保存防覆盖切面 (`saveAcl`)**：在提交更新时，强制合并并保留原有 `network` 配置，防止配置丢失导致微服务绑定临时随机端口；
6. **SOCKS5 协议防呆提示**：界面显著位置增加警示横幅：“⚠️ SOCKS5 为四层 TCP 协议，请勿在浏览器地址栏直接打开”。

#### 核心修复代码对比：
````carousel
```javascript
// ❌ 修复前 (存在重大缺陷)
async function loadPluginInfo() {
  const token = localStorage.getItem('lwt_token'); // 缺少 URL token 兼容
  const res = await fetch('/api/plugins/socks5-proxy', { ... });
  const p = data?.data || data;
  if (p?.port) { // 无法匹配后端 allocatedPort
    document.getElementById('proxy-port').textContent = ':' + p.port;
  }
}
```
<!-- slide -->
```javascript
// ✅ 修复后 (多层防护与动态避让感知)
async function loadPluginInfo() {
  const urlParams = new URLSearchParams(window.location.search);
  const token = urlParams.get('token') || localStorage.getItem('lwt_token') || '';
  
  const res = await fetch('/api/plugins/socks5-proxy', {
    headers: token ? { 'Authorization': 'Bearer ' + token } : {}
  });
  if (!res.ok) return;
  const data = await res.json();
  const p = data?.data || data;
  currentManifest = p?.manifest;

  // 健壮取值：运行时分配端口 > DTO兼容端口 > 预设配置端口 > 缺省 18080
  const activePort = p?.allocatedPort || p?.port || p?.configuredPort || p?.manifest?.network?.port || 18080;
  
  const portEl = document.getElementById('proxy-port');
  if (portEl) {
    portEl.textContent = ':' + activePort;
    if (p?.isPortAvoided) {
      portEl.classList.add('text-amber-400');
      portEl.title = p.portConflictReason || '声明目标端口被占用，已自动避让';
      showAvoidedBanner(p.portConflictReason);
    }
  }
  updateClientPreview();
}
```
````

### 6.3 修复 3：后端配置更新安全守卫 (`PluginManager.cs`)
在 `src/LinuxWebTool.Infrastructure/Features/Plugins/PluginManager.cs` 的 `CreateOrUpdateAsync` 中增加保护：如果传入的 `request.Manifest.Network` 为空，而磁盘上原有 `plugin.json` 包含 `Network` 配置，则自动继承原有网络配置，禁止被意外清空：

```csharp
var oldManifest = File.Exists(Path.Combine(dir, "plugin.json"))
    ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(dir, "plugin.json"), ct), PluginJsonContext.Default.PluginManifest)
    : null;

var finalManifest = manifest;
if (finalManifest.Network == null && oldManifest?.Network != null)
{
    finalManifest = finalManifest with { Network = oldManifest.Network };
}
```

### 6.4 修复 4：父子窗口 `postMessage` 响应式增强 (`PluginWebviewModal.js`)
在 `PluginWebviewModal.js` 中增加双向通道握手：在 iframe 加载完成时，向子窗口 postMessage 投递环境上下文：
```javascript
function onIframeLoad() {
  if (iframeRef.value?.contentWindow && props.plugin) {
    iframeRef.value.contentWindow.postMessage({
      type: 'LWT_PLUGIN_ENV',
      pluginId: props.plugin.id,
      allocatedPort: props.plugin.allocatedPort,
      configuredPort: props.plugin.configuredPort || props.plugin.manifest?.network?.port,
      isPortAvoided: Boolean(props.plugin.isPortAvoided),
      token: auth.token
    }, '*');
  }
}
```

---

## 七、架构门禁与避免回归测试 (Guardrails & Regression Tests)

为彻底阻断“前端契约脱节”、“静态端口死值残留”与“清单配置覆盖抹除”等同类问题的再次发生，建立以下门禁与防回归测试体系：

### 7.1 新增架构门禁：`LinuxArch069_微服务Webview控制台端口契约对齐与抗覆盖门禁`

在 `tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs` 中新增自动化代码守卫：

```csharp
[Fact]
public void LinuxArch069_微服务Webview控制台端口契约对齐与抗覆盖门禁()
{
    // 1. 验证 PluginInstanceDto 暴露 Port 兼容性别名
    var modelsFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Contracts", "Features", "Plugins", "Contracts", "PluginModels.cs");
    Assert.True(File.Exists(modelsFile), "PluginModels.cs 必须存在");
    var modelsContent = File.ReadAllText(modelsFile);
    Assert.Contains("public int? Port => AllocatedPort ?? ConfiguredPort;", modelsContent);

    // 2. 验证 SOCKS5 代理控制台 ui/index.html 杜绝死端口 18001 硬编码与具备 token 兼容
    var uiFile = Path.Combine(RepoRoot, "plugins", "socks5-proxy", "ui", "index.html");
    Assert.True(File.Exists(uiFile), "ui/index.html 必须存在");
    var uiContent = File.ReadAllText(uiFile);
    
    // 严禁以 18001 作为写死的最终展示端口
    Assert.DoesNotContain("<div class=\"val\" id=\"proxy-port\">:18001</div>", uiContent);
    // 必须具备 allocatedPort 判定与 URL token 容错提取
    Assert.Contains("allocatedPort", uiContent);
    Assert.Contains("URLSearchParams", uiContent);

    // 3. 验证 saveAcl 具备 network 清单防丢失继承切面
    Assert.Contains("network:", uiContent);

    // 4. 验证 PluginManager.CreateOrUpdateAsync 具备网络配置继承防护
    var pmFile = Path.Combine(RepoRoot, "src", "LinuxWebTool.Infrastructure", "Features", "Plugins", "PluginManager.cs");
    var pmContent = File.ReadAllText(pmFile);
    Assert.Contains("finalManifest.Network == null && oldManifest?.Network != null", pmContent);
}
```

### 7.2 单元与集成测试用例设计

在 `tests/LinuxWebTool.UnitTests/` 下新增 `PluginPortContractAndSocks5SyncTests.cs`：
1. **测试用例 1（DTO 序列化兼容性）**：
   验证 `PluginInstanceDto` 在序列化为 JSON 时，`allocatedPort`、`configuredPort`、`port` 字段均存在且数值正确；
2. **测试用例 2（端口冲突避让动态下发）**：
   模拟 18080 被占用场景，验证 `ResidentExecutionStrategy` 分配的 `allocatedPort` 为 18002，且传入 Go 适配器的启动参数为 `--listen 0.0.0.0:18002`；
3. **测试用例 3（清单更新抗意外置空保护）**：
   调用 `CreateOrUpdateAsync` 传入未配置 `network` 的 manifest，断言更新后的 `plugin.json` 依然保留原有的 `network` 属性；
4. **测试用例 4（SOCKS5 原生 RFC 1928 握手连通性验证）**：
   使用 C# 原生 `TcpClient` 向微服务发送 `[0x05, 0x01, 0x00]`，断言收到服务端合规的 `[0x05, 0x00]` 回复，确保四层连通性健全。

---

## 八、总结与后续演进建议

通过本次系统性技术调研与根因剖析，我们厘清了 SOCKS5 微服务端口无法访问的核心故障链条，并阐明了外部配置端口与 Webview 模态框端口从“声明期望”到“运行时租约”再到“界面投影”的完整映射关系。

**后续演进建议**：
1. **立即合入修复**：优先应用 `PluginModels.cs` 契约别名与 `plugins/socks5-proxy/ui/index.html` 的前端防脱节改造；
2. **启用自动化架构门禁**：通过 `LinuxArch069` 将 DTO 字段对齐与 Webview 静态端口扫描固化为 CI 必检项，彻底杜绝此类软性回归隐患；
3. **推广四层服务通用指引组件**：为平台所有 TCP 类型的微服务（如 EasyTier、FRP、TCP 网关等）抽象统一的“四层协议连接助手”，自动提供多种语言与命令行客户端的连通性诊断脚本。
