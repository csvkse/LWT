# 插件中心长时 HTTP/TCP 服务快速换端与端口冲突检测避让方案调研

> **文档状态**：已完成技术调研与架构设计  
> **关联架构门禁**：`LinuxArch041_插件资产与代码安全隔离门禁`、`LinuxArch047_Go构建包路径解析与SOCKS5三维安全防御门禁`、`LinuxArch050_插件长时服务快速换端与双栈冲突避让门禁` (待建)  
> **涉及核心组件**：`PluginPortManager`、`ResidentExecutionStrategy`、`PluginStateMachine`、`IPGlobalProperties`、`PluginsController`、`PluginCard.js`

---

## 一、用户关注三大核心问题与实测根因定位

### 1.1 问题 1 根因深度剖析：为什么 `demo-js-webhook` 界面显示未运行，宿主机端口却被其占用？

#### 实测证据链条
在宿主机 `10.126.1.70:45612` 上执行进程与容器树现场分析，捕获到完整事实：
```bash
# 宿主机端口占用情况
LISTEN 0 511 *:18001 *:* users:(("MainThread",pid=495355,fd=21))

# 495355 进程 cgroup 与父进程链
UID PID PPID CMD
root 494091 494071 ./LinuxWebTool.WebHost
root 495355 494091 node /app/data/plugins/demo-js-webhook/index.js
cgroup: 0::/system.slice/docker-e46f1c04836f...scope (容器 linuxwebtool)
```
通过内部管理 API 对两套容器实例进行实测查询：
```json
// 生产容器 linuxwebtool (宿主机端口 :5270，Docker 模式: --net=host)
{ "id": "demo-js-webhook", "state": 2 (Running), "processId": 495355, "allocatedPort": 18001 }

// 开发容器 linuxwebtool-dev (宿主机映射端口 :5271，Docker 模式: Bridge 隔离网络)
{ "id": "demo-js-webhook", "state": 0 (Stopped), "processId": null, "allocatedPort": null },
{ "id": "socks5-proxy",    "state": 2 (Running), "processId": 761503, "allocatedPort": 18001 }
```

#### 根因推导与总结
1. **双实例环境与网络命名空间隔离导致的认知差**：
   - 宿主机上运行了生产版本 `linuxwebtool`（`:5270`，采用 `--net=host` 宿主机直连网络）。该实例中启动了 `demo-js-webhook`，直接监听了物理机网卡的 `0.0.0.0:18001`。
   - 用户当前在浏览器中查看和调试的是开发环境 `linuxwebtool-dev`（`:5271`，采用 Docker Bridge 网络）。在开发容器内部，`demo-js-webhook` 确实没有启动（卡片显示灰白色“已停止”），因而造成“插件没运行却霸占端口”的直观疑惑。
2. **默认模板预置端口碰撞（Port Collisions in Defaults）**：
   - 查看系统预设代码 `PluginManager.Defaults.Samples.cs` 与 `PluginManager.Defaults.cs`：
     - `demo-js-webhook` 默认写死 `Port = 18001`；
     - `socks5-proxy` 默认也写死 `Port = 18001`；
     - 两个不同的插件都默认声明了同一端口 `18001`。当多个插件陆续启动时，必然出现端口争抢。
3. **架构隐患：进程字典仅保存在内存，缺少持久化与孤儿清理机制**：
   - 当前 `ResidentExecutionStrategy.cs` 使用内存字典 `ConcurrentDictionary<string, Process> _processes` 维护子进程。
   - 一旦容器内主程序发生非正常重启或被 `docker kill`，主进程消失，但 Node.js / Python / Go 等子进程在 Linux 中可能会被 `systemd` 或 `containerd-shim` 领养成为**孤儿常驻进程**（Orphaned Process）。
   - 新的主程序启动后，内存字典为空，既探测不到旧进程的 PID，也无法对其发送 SIGTERM/SIGKILL，导致旧进程长期霸占 18001 端口且界面无感知。

---

## 二、业界主流方案与现有库调研对比

### 2.1 端口检测与避让方案（Conflict Detection & Avoidance）

| 方案 / 项目 | 核心检测机制 | 冲突避让策略 | 优点 | 缺点 | 借鉴与评估 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **Vite / Next.js (`detect-port`, `portfinder`)** | 使用 Node.js `net.Server.listen` 尝试绑定目标端口；如果捕获 `EADDRINUSE`，则通过递增步长探测下一可用端口。 | 自动避让模式（递增到 5174...）；支持 `strictPort: true` 严格拒绝模式。 | 开发者体验极佳，终端提示明确，零心智负担。 | 存在微小的 TOCTOU（Time-of-check to time-of-use）竞争窗口。 | **核心借鉴点**：默认避让并突出告警，提供严格模式开关；UI 上标识“预选端口 vs 实际运行端口”。 |
| **Kubernetes (NodePort 分配器)** | 基于 Bitmap 位图在内核端口池（30000-32767）中进行原子分配和占用标记。 | 若用户声明的 NodePort 被占用直接拒绝；若未声明则原子分配池中未使用的随机端口。 | 强一致性，高并发无冲突。 | 依赖集中式状态存储（etcd），设计较重。 | **核心借鉴点**：系统维护全局分配池，避免仅靠即时 socket 探测。 |
| **Nginx / Caddy 反向代理** | 多实例热切换（Graceful Reload）；主进程监听 Master Socket，将 fd 传递给新 Worker 进程。 | 新旧进程平滑切换，旧进程处理完现有连接后退出。 | 零停机热更新。 | 需控制子进程架构与 Unix Domain Socket 句柄继承。 | **微服务插件网关借鉴**：HTTP 类插件本身经过网关反代（`mode: "path"`），端口改变只需要同步网关上游，外部客户端调用路由不变！ |

### 2.2 .NET 生态与系统底层检测机制对比

1. **`System.Net.NetworkInformation.IPGlobalProperties.GetActiveTcpListeners()`**：
   - **原理**：直接读取操作系统内核网络状态表（Linux 下读取 `/proc/net/tcp` 与 `/proc/net/tcp6`；Windows 下调用 `GetExtendedTcpTable`）。
   - **优点**：无需实际创建和销毁 Socket，直接瞬时获取全系统当前正在监听的全部端口列表（包括跨命名空间/跨进程绑定）；性能极高。
2. **`Socket.Bind(ExclusiveAddressUse = true)` 独占探测**：
   - **原理**：创建原生 Socket，开启 `ExclusiveAddressUse`，并尝试绑定 `IPAddress.Any`（0.0.0.0）与 `IPAddress.IPv6Any`（::）。
   - **优点**：真实校验当前进程是否有权独占该端口，避免了 Loopback 探测无法捕获 `0.0.0.0` 绑定的弊端。
3. **两级组合探测策略（Composite Detector）**：
   - 第 1 级：查询 `IPGlobalProperties` 表，毫秒级快速剔除已知占用；
   - 第 2 级：针对候选端口执行双栈（IPv4/IPv6）`Socket.Bind` 校验；
   - 双重验证确保检测准确率 100%。

---

## 三、当前系统可复用基础设施分析

1. **已有的端口管理器 (`PluginPortManager.cs`)**：
   - 已定义 `MinPort (18000)` 与 `MaxPort (18999)` 端口池；
   - 现存 `AllocatePort(pluginId, preferredPort)` 结构可直接升级为两级组合探测与租约锁（Reservation Lock）。
2. **已有的多语言运行时适配器 (`IRuntimeAdapter`)**：
   - `NodeRuntimeAdapter`, `PythonRuntimeAdapter`, `GoRuntimeAdapter`, `DotNetRuntimeAdapter` 均支持环境变量 `PORT` / `SOCKS5_PORT` / `ASPNETCORE_URLS` 与命令行占位符 `{port}`，具备无缝端口热重载的基础。
3. **已有的反向代理网关 (`/plugins/{id}/*`)**：
   - 对于 `mode: "path"` 的 HTTP 插件，前端与外部客户端一律通过反向代理网关访问，后端口变动对外部请求透明，极低迁移成本。
4. **已有的状态机与环形缓冲 (`PluginStateMachine`, `PluginProcessRingBuffer`)**：
   - 拥有完善的生命周期通知、状态流转（`Starting`, `Running`, `Crashed`, `Degraded`）及 CrashLoopBackoff 熔断机制，可直接接纳端口切换事件。

---

## 四、头脑风暴：可行性、逻辑漏洞与性能架构设计

### 4.1 核心可行性与能力设想

#### 功能 1：长时 HTTP 服务快速切换端口（Hot Port Switching）
- **操作交互**：
  - 在插件卡片与详情面板中，端口徽章 `:18001` 支持一键点击，呼出“快速切换端口”轻量弹出窗；
  - 弹窗输入目标端口，实时进行**单步可用性预检**（绿勾：可用；红叉：已占用并标出占用者）；
  - 提供“自动避让推荐”按钮（一键填入系统推荐的下一个完全空闲端口）；
  - 点击“应用并热重载”，系统自动：
    1. 校验目标端口；
    2. 更新 `plugin.json` 中的 `network.port` 配置；
    3. 触发优雅停机（Graceful Stop）；
    4. 携带新端口重新拉起子进程；
    5. 更新网关上游，完成平滑切换。

#### 功能 2：端口冲突检测与自动避让（Conflict Detection & Self-Healing Avoidance）
- **策略控制（配置化）**：
  - 在 `plugin.json` 的 `network` 配置中扩充 `portConflictPolicy` 字段：
    - `avoid`（默认，自动避让）：目标端口被占用时，系统自动在端口池中选取最近的未占用端口启动，并在启动日志和状态机中标记 `PortAvoided`，卡片展示警告徽章（如 `18002 (原18001已被占用，自动避让)`）；
    - `strict`（严格报错）：若目标端口被占用，直接终止启动并抛出友好异常提示，避免默默漂移导致硬编码调用失败。
- **孤儿清理自愈（Orphan Reaper）**：
  - 在启动前，读取插件运行锁状态（`.pid`），若端口被占用的进程恰好是本插件上次遗留的孤儿进程，系统自动执行精准回收释放（`killProcessTree`），然后直接复用该端口！

### 4.2 潜在缺陷与设计模式优化

```
┌────────────────────────────────────────────────────────────────────────┐
│                        Plugin Architecture                             │
│                                                                        │
│   ┌────────────────────┐          ┌────────────────────────────────┐   │
│   │   PluginsController│ ───────> │         IPluginManager         │   │
│   └────────────────────┘          └────────────────┬───────────────┘   │
│                                                    │                   │
│                                           ┌────────┴────────┐          │
│                                           ▼                 ▼          │
│                              ┌─────────────────┐  ┌────────────────┐   │
│                              │PluginPortManager│  │ PluginState-   │   │
│                              └────────┬────────┘  │ Machine (FSM)  │   │
│                                       │           └────────────────┘   │
│           ┌───────────────────────────┴────────────────────────┐       │
│           ▼                                                    ▼       │
│  【策略模式 Strategy】                               【适配器模式 Adapter】   │
│  IPortConflictStrategy                               IPortProbeAdapter │
│  ├─ AutoAvoidanceStrategy                            ├─ SocketProbe    │
│  └─ StrictFailStrategy                               └─ SysTableProbe  │
└────────────────────────────────────────────────────────────────────────┘
```

1. **适配器模式（Adapter Pattern）解耦端口探测**：
   - 定义 `IPortProbeAdapter`，实现跨平台、跨环境的端口探活性：
     - `FastSocketProbeAdapter`：基于双栈 `Socket.Bind(ExclusiveAddressUse = true)` 验证；
     - `ProcNetTcpProbeAdapter`：针对 Linux `/proc/net/tcp` 的直读探测；
     - `CompositePortProbeAdapter`：串联探测保障。
2. **策略模式（Strategy Pattern）解耦冲突解决行为**：
   - 定义 `IPortConflictStrategy`：
     - `AutoAvoidanceConflictStrategy`：避让策略；
     - `StrictFailConflictStrategy`：严格策略。
3. **状态机模式（FSM Pattern）增强**：
   - 在 `PluginStateMachine` 中补充运行元数据：`OriginalPort`、`ActualPort`、`IsAvoided`、`ConflictReason`；
   - 避免状态断层。

---

## 五、具体实施落地规划

### 阶段一：端口管理器重构与孤儿清理自愈（后端核心）
1. 重构 [`PluginPortManager.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.Infrastructure/Features/Plugins/Supervisor/PluginPortManager.cs)：
   - 引入双栈 `IPAddress.Any` 与 `IPAddress.IPv6Any` 独占绑定探测；
   - 引入 `CheckPortStatus(int port)` 方法，返回 `(bool IsAvailable, string? OccupiedBy)`；
   - 支持动态避让 `ResolvePortWithAvoidance(string pluginId, int requestedPort, PortConflictPolicy policy)`。
2. 增加启动前的**孤儿进程探测与回收**：
   - 检查持久化 PID 文件，如果该端口被历史遗留的当前插件进程占用，执行优雅清理与释放。

### 阶段二：快速切换端口 API 与控制器开放
1. 在 [`PluginsController.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.WebHost/Features/Plugins/Routes/PluginsController.cs) 增加以下管理端点：
   - `GET /api/plugins/{id}/Port/Check?port=18005`：探测目标端口是否可用与系统建议；
   - `POST /api/plugins/{id}/Port/Switch`：接收 `{ newPort: 18005, restartIfRunning: true }`，原子保存配置并重启。

### 阶段三：前端插件卡片与快速切换弹窗升级
1. 在 [`PluginCard.js`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.WebHost/wwwroot/app/components/plugins/PluginCard.js) 端口标签旁添加可交互的换端图标/点击响应；
2. 新增 `PluginPortSwitchModal.js` 组件，提供一键换端、可用性预检与自动避让推荐；
3. 当发生端口避让时，卡片以显著琥珀色（Amber）徽章提示：`18002 (已避让原18001)`。

### 阶段四：架构守护门禁与自动化验证
1. 在 `tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs` 中增加架构守护门禁：
   - `LinuxArch050_插件端口冲突检测与自愈避让门禁`；
2. 编写单元测试验证：
   - 端口已被占用时自动避让分配下一空闲端口；
   - 端口释放后可再次被复用；
   - 严格模式下冲突正确报错；
   - 孤儿进程回收机制。
