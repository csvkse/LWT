# SOCKS5 代理微服务 `socks5://10.126.1.70:18080` 无法访问根因调研与解决方案报告

> **文档状态**：已完成技术调研与实证复盘  
> **文档路径**：`docs/socks5-service-10-126-1-70-inaccessibility-analysis-and-solution.md`  
> **关联服务**：`socks5-proxy` (Go 原生网络微服务)  
> **目标端点**：`socks5://10.126.1.70:18080` (远程 Linux 宿主机物理 IP 与暴露端口)  
> **测试客户端**：`10.126.1.88` (本地开发者工作站)  

---

## 一、 问题现象与实证复盘 (Live Evidence & Traces)

用户提出问题：`socks5://10.126.1.70:18080 为什么无法使用？`

我们在本地工作站（`10.126.1.88`）与远程 Linux 宿主机（`10.126.1.70`）上进行了全链路抓包、实测与代码跟踪，获取了绝对确凿的排查证据链：

### 1. 现象 1：直接连接触发 TCP RST（连接被重置）
当在客户端执行如下标准 cURL 测试命令时：
```bash
curl.exe -v -x socks5://10.126.1.70:18080 http://10.126.1.70:5271/health
```
输出：
```text
* Trying 10.126.1.70:18080...
* Recv failure: Connection was reset
* Failed to receive SOCKS response: Failure when receiving data from the peer
* closing connection #0
curl: (97) Recv failure: Connection was reset
```
TCP 三次握手成功建立，但在客户端发送 SOCKS5 首字节协商包后，连接**立即被服务端内核重设（RST）**。

### 2. 现场日志抓取：触发了握手前入站白名单短路阻断
通过微服务后台环形日志与 API 查询，捕捉到持续密集的拦截告警：
```text
time=2026-11-03T22:01:15.003+08:00 level=WARN msg="client rejected by ingress whitelist policy" client=10.126.1.88:57344
time=2026-11-03T22:01:16.829+08:00 level=WARN msg="client rejected by ingress whitelist policy" client=10.126.1.88:54039
time=2026-11-03T22:03:01.072+08:00 level=WARN msg="client rejected by ingress whitelist policy" client=10.126.1.70:36520
```
查询微服务内部监控接口 `http://127.0.0.1:19080/api/status`，证实：
```json
{
  "acl": {
    "preset": "lan",
    "blockedClients": 67,
    "clientRules": ["127.0.0.1/32"]
  },
  "authMode": "password",
  "username": "proxyuser"
}
```
**铁证**：微服务内部统计到 `blockedClients: 67`，当前入站客户端白名单被严格锁死在 `["127.0.0.1/32"]`，导致所有来自宿主机局域网 IP（`10.126.1.70`）及外部客户端（`10.126.1.88`）的 TCP 握手被微服务内部的 `conn.Close()` 彻底拒连！

### 3. 现象 2：放行 IP 后，未带凭据触发 RFC 1929 认证拒绝
当我们把入站客户端规则扩展允许 `10.0.0.0/8` 后，再次运行无密码连接：
```bash
curl.exe -v -x socks5://10.126.1.70:18080 http://10.126.1.70:5271/health
```
输出变为：
```text
* Trying 10.126.1.70:18080...
* No authentication method was acceptable.
* closing connection #0
curl: (97) No authentication method was acceptable.
```
这证明：由于持久化配置开启了 `authMode: "password"`，客户端若使用单纯的 `socks5://10.126.1.70:18080`（未带 `user:pass@`），协议协商阶段直接因认证方式不支持而失败。

### 4. 现象 3：携带凭据后，代理完全连通成功
当附带正确凭据执行连接：
```bash
curl.exe -v -x socks5://proxyuser:secretpassword123@10.126.1.70:18080 http://10.126.1.70:5271/health
```
输出：
```text
* Opened SOCKS connection from 10.126.1.88 port 63668 to 10.126.1.70 port 5271 (via 10.126.1.70 port 18080)
< HTTP/1.1 200 OK
{"status":"healthy","version":"1.0.0.0",...}
```
代理链路彻底打通，HTTP 200 验证成功。

### 5. 现象 4：访问外网公共目标触发 RFC 1928 规则拒绝
当携带凭据尝试访问公网 IP（如 `1.1.1.1`）时：
```bash
curl.exe -v -x socks5://proxyuser:secretpassword123@10.126.1.70:18080 http://1.1.1.1
```
输出：
```text
* cannot complete SOCKS5 connection to 1.1.1.1. (2)
curl: (97) cannot complete SOCKS5 connection to 1.1.1.1. (2)
```
微服务返回了 RFC 1928 错误响应字 `0x02` (`repRuleViolation` 规则拒绝)，因为出站目标 ACL 限制在局域网内网段（`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`）。

---

## 二、 核心根因深层剖析 (Root Cause Analysis)

`socks5://10.126.1.70:18080` 无法使用的本质，是**多层安全防御机制生效、状态持久化残留、控制台前端指引误导以及容器网络拓扑共同作用**的结果：

```mermaid
flowchart TD
    Client["客户端发起请求 (10.126.1.88)"] --> NAT["Docker NAT 转发到容器 (0.0.0.0:18080)"]
    NAT --> Step1{"【阻断一】入站 IP 白名单检查<br>IsClientAllowed(remoteAddr)"}
    Step1 -- "不匹配 127.0.0.1/32" --> Reject1["短路 conn.Close()<br>触发 TCP RST (连接被对方重置)"]
    Step1 -- "匹配放行" --> Step2{"【阻断二】SOCKS5 协商认证<br>authMode == password?"}
    Step2 -- "未提供凭据" --> Reject2["协商返回 0xFF<br>No authentication method acceptable"]
    Step2 -- "凭据匹配" --> Step3{"【阻断三】出站目标 ACL 检查<br>EvaluateTarget(target)"}
    Step3 -- "公网目标 1.1.1.1 违背 LAN 策略" --> Reject3["返回 RFC 1928 rep=0x02<br>cannot complete connection (2)"]
    Step3 -- "内网白名单目标" --> Success["代理连通，转发数据"]

    style Reject1 fill:#fee,stroke:#f66,stroke-width:2px
    style Reject2 fill:#fef0de,stroke:#faad14,stroke-width:2px
    style Reject3 fill:#fef0de,stroke:#faad14,stroke-width:2px
    style Success fill:#e6ffed,stroke:#52c41a,stroke-width:2px
```

### 1. 根本阻断 1：维度二（入站客户端来源白名单）短路切断
在 [`plugins/socks5-proxy/cmd/socks5-proxy/main.go`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/socks5-proxy/cmd/socks5-proxy/main.go#L373-L378) 中：
```go
if s.acl != nil && !s.acl.IsClientAllowed(conn.RemoteAddr().String()) {
    s.logger.Warn("client rejected by ingress whitelist policy", "client", conn.RemoteAddr().String())
    _ = conn.Close()
    continue
}
```
当配置中持久化保存了 `"allowedClients": "127.0.0.1"` 时，SOCKS5 微服务在 TCP 握手一完成就调用 `conn.Close()` 挂断连接。由于未读取客户端发送的握手协商字节流便单向关闭 Socket，操作系统内核直接向对端回送 TCP RST（重置），导致客户端呈现 `Recv failure: Connection was reset`。

### 2. 根本阻断 2：维度一（RFC 1929 用户名密码认证）阻断无凭据访问
当前持久化配置开启了 `authMode: "password"`。用户使用标准连接串 `socks5://10.126.1.70:18080` 时，客户端默认不发送身份验证方法（只发送 `0x00: NO AUTH`），而微服务仅接受 `0x02: USER/PASS`，握手协商直接破裂。

### 3. 根本阻断 3：维度三（出站目标过滤）阻断公网目标访问
默认出站预设为 `lan` 模式，仅允许内网私有地址（`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`）及局域网域名。若将本代理作为常规全局梯子访问公网网址，会被代理微服务出站拦截。

### 4. 关键体验诱因 4：Webview 模态框指引硬编码 `127.0.0.1` 导致心智认知脱节
在 [`plugins/socks5-proxy/ui/index.html`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/socks5-proxy/ui/index.html) 中：
- 模态框下方的连接指引硬编码展示：
  `curl -x socks5://127.0.0.1:18080 http://127.0.0.1:5270/health`
- 用户在宿主机外部（浏览器地址栏为 `http://10.126.1.70:5271/`）看到指引时，直觉将其中的 `127.0.0.1` 替换为服务器物理 IP `10.126.1.70`。
- 但是控制台**并未告知用户此时入站来源白名单只放行了 `127.0.0.1`**，也未提示需要携带凭据。用户按直觉修改 IP 后立即踩中白名单拦截。

### 5. 容器拓扑隐患 5：Docker Bridge 模式 vs Host 模式下的客户端 IP 溯源
- 开发容器 `linuxwebtool-dev` 运行在 Docker Bridge 模式下，端口由宿主机 `docker-proxy` 映射。
- 外部机器 `10.126.1.88` 连入时，容器内看到的源 IP 是真实的 `10.126.1.88`；
- 但如果宿主机本地通过 `curl -x socks5://10.126.1.70:18080` 连入，容器内看到的源 IP 是宿主机 IP `10.126.1.70`；
- 若入站规则未配置局域网网段（如 `10.0.0.0/8`），无论宿主机还是局域网客户机均会被拦截。

---

## 三、 业界主流方案调研与对比 (Industry Benchmarks)

针对企业级微服务安全代理、内网穿透网关与访问控制，我们对比了业界主流的成熟实现：

| 维度 | Dante SOCKS5 (`danted`) | GOST (`go-gost`) | Envoy Proxy (L4 Filter) | 本系统 socks5-proxy 当前现状 | 本系统推荐演进方案 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **入站来源控制 (Ingress ACL)** | 完善的 `client pass/block` 规则块，支持 CIDR 与接口绑定 | Limiter / Bypass 插件体系，支持 IP 白名单 | RBAC 过滤器，基于 Downstream IP 匹配 | 自研 `AclEngine`，支持标准 CIDR 与便捷前缀 | **保持现有引擎，优化默认预设为 LAN 网段而非回环** |
| **未授权拦截行为 (Reject Behavior)** | 可配置：`drop` (静默丢弃), `reset` (TCP RST), `reject` | 默认关闭连接或按配置返回 SOCKS 协议拒绝 | 发送自定义终止标志并记录在 Access Log (`UAEX`) | 握手前 `conn.Close()` 触发 TCP RST | **握手阶段返回 RFC 1928 拒绝码，或在 Webview 醒目告警** |
| **客户端主机名引导 (UI Guidance)** | 无原生 UI，纯配置文件 | 命令行工具，提供连接模板 | 依靠周边 Portal | Webview 硬编码 `127.0.0.1` | **根据 `window.location.hostname` 动态替换主机名与凭据** |
| **诊断与可观测性 (Observability)** | 详尽连接日志与审计输出 | 支持 Prometheus 指标与多级别日志 | 极其丰富的 Access Log 与 StatsD 指标 | 提供 `19080/api/status` 指标，但 Webview 未展示 | **在 Webview 增加连通性诊断横幅与被阻断实时统计** |
| **Docker NAT 兼容性** | 需配置 `external.rotation` 与真实接口 | 支持自动嗅探绑定网卡 | 丰富的网络监听器绑定支持 | `0.0.0.0` 绑定，但白名单配置易被配死 | **提供“一键放行当前来源 IP”与“局域网放行”预设** |

### 业界最佳实践启示：
1. **默认安全但具备可用性 (Default Safe & Usable)**：作为网络微服务，其默认配置不应导致外部宿主局域网直接拒连。默认入站来源预设应当是 **“局域网私网来源 (LAN: 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16)”**，而非仅限回环。
2. **拒绝即诊断 (Diagnostics on Denial)**：当连接被安全策略拦截时，不能仅在后台悄无声息地单向 Reset，控制台应当实时投影被拦截指标（`blockedClients`），让排查人员一目了然。
3. **动态环境感知 (Environment-Aware URL)**：控制台生成的代码示例严禁硬编码 `127.0.0.1`，必须动态读取当前浏览器访问的主机名与端口，并自动拼接鉴权凭据。

---

## 四、 当前系统基础设施复用可能性分析

当前系统中已有非常完善的基础设施可以直接复用，无需引入额外依赖：

1. **`PluginPortManager.cs`**：
   已具备双栈内核端口探活与避让能力，负责将端口 `18080` 稳定分配给微服务。
2. **`PluginWebviewModal.js` 跨窗口上下文通道**：
   在上次修复中我们已建立 `LWT_PLUGIN_ENV` 的 `postMessage` 广播通道。可直接将父页面的 `window.location.hostname`、`window.location.protocol` 作为宿主环境参数下发给 Webview。
3. **`AclEngine` (Go 微服务内部)**：
   微服务内部已有完整的 RCU 无锁原子规则重载引擎、标准 CIDR 解析器与 `/api/acl`、`/api/status` 内部控制接口。
4. **`PluginProcessRingBuffer`**：
   后端已有针对插件输出的环形日志缓冲，并已暴露 `/api/Plugins/{id}/Logs` 接口，前端可随时调取诊断。

---

## 五、 具体完整解决方案

### 1. Webview 控制台动态主机与凭据拼装（消除 `127.0.0.1` 误导）
修改 [`plugins/socks5-proxy/ui/index.html`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/socks5-proxy/ui/index.html)：
- 消除 `127.0.0.1` 硬编码：动态提取宿主控制台的主机名（优先使用 `parentHost` 或 `window.location.hostname`）。
- 动态拼装认证信息：若开启密码认证，自动在 cURL / Git / 环境变量命令中添加 `username:password@` 前缀；若无认证则生成干净的 `socks5://host:port`。
- 新增**连接诊断检测与“一键放行当前 IP”快捷按钮**：
  在控制台展示用户当前访问 Web 控制台的客户端 IP（例如 `10.126.1.88`），若当前 IP 未在允许列表内，给出黄色高亮警示并支持一键添加。

### 2. 规范化默认入站客户端策略
- 在 `plugin.json` 与 `ui/index.html` 中，将**入站客户端 IP 的默认预设统一为“局域网私网来源 (LAN)”**：
  ```text
  10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 127.0.0.0/8, ::1/128
  ```
  避免新建或重置服务后，因为写死 `127.0.0.1` 导致外部局域网客户端根本无法连接。

### 3. 可观测性看板（拦截指标直接可视化）
- 从 `/api/status`（或微服务指标接口）轮询获取 `blockedClients` 与 `authFailures`。
- 当 `blockedClients > 0` 且有新增时，在 Webview 顶部显示诊断横幅：
  > ⚠️ **检测到入站拦截**：系统已累计拦截 67 次未授权客户端连接（最近尝试来自非白名单网段），如无法连接请检查“入站客户端 IP 访问限制”。

---

## 六、 架构门禁与同类防回归测试设计

为了从根本上避免同类“微服务控制台命令误导”、“入站白名单默认导致外部无法访问”的架构回归，设计如下双重防线：

### 1. 架构测试门禁：`LinuxArch070`
在 [`tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs) 中新增门禁：
- **断言 1**：所有具备网络对外交互的微服务 Webview 页面，生成的客户端指引（cURL / Git / 环境变量）严禁写死 `127.0.0.1`，必须通过脚本动态解析宿主主机名。
- **断言 2**：网络类插件的清单声明中，`--allowed-clients` 默认不可硬编码写死仅限回环 `127.0.0.1`，必须支持局域网私有网段或默认采用安全 LAN 预设。

### 2. 自动化 E2E 冒烟套件强化
在 [`tests/smoke/acceptance-socks5-webview.mjs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/tests/smoke/acceptance-socks5-webview.mjs) 中补充端到端断言：
1. 断言 Webview 内部展示的 cURL 连接指引中的主机名为当前服务器 IP（`10.126.1.70`），而非 `127.0.0.1`；
2. 自动化模拟客户端从外网直连 SOCKS5 端口的握手连通性测试。

---

## 七、 总结

`socks5://10.126.1.70:18080` 无法访问的问题并非网络宕机，而是由**“入站白名单写死 127.0.0.1 短路挂断”**、**“密码认证凭据缺失”**与**“Webview 客户端指引硬编码 127.0.0.1 误导用户”**所共同导致。  
通过将入站白名单策略默认对齐为局域网私网段（LAN）、使 Webview 动态绑定宿主机真实 IP 与认证凭据、并在前端直观透视拦截诊断信息，即可实现既保障内网微服务安全防御，又具备开箱即用的良好连通体验。
