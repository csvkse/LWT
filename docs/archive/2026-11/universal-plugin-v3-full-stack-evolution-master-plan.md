# LinuxWebTool 统一插件生态、正交分类拓扑与全栈交互完整优化方案 (Master Plan)

> **文档标识**：`docs/universal-plugin-v3-full-stack-evolution-master-plan.md`  
> **制定时间**：2026-11-08  
> **战略基调**：**未正式发版，完全免除历史兼容包袱，坚决清零技术债务，全栈对齐工业级标准**  
> **方案渊源**：融合并升华 `frontend-schema-v3-synchronization-and-interaction-evolution-plan.md` 与 `existing-plugins-schema-v3-migration-and-directory-standardization-plan.md`，彻底贯通后端契约、前端视图交互与 21 个扩展生态。

---

## 目录索引 (Table of Contents)

1. [战略背景与重构动机](#一-战略背景与重构动机)
2. [第一篇：正交多维分类学与工作负载拓扑](#二-第一篇正交多维分类学与工作负载拓扑)
3. [第二篇：全量 21 扩展升级与物理目录标准化](#三-第二篇全量-21-扩展升级与物理目录标准化)
4. [第三篇：前端全栈交互与设计模式革新](#四-第三篇前端全栈交互与设计模式革新)
5. [第四篇：性能保障与架构门禁防御体系](#五-第四篇性能保障与架构门禁防御体系)
6. [第五篇：端到端实施路线图与验收矩阵](#六-第五篇端到端实施路线图与验收矩阵)

---

## 一、 战略背景与重构动机

随着后端 **Schema v3.1 通用多维架构**（生命周期 `PluginLifecycle`、协议 `InterfaceProtocol`、架构形态 `PluginArchetype`、目录规范 `PluginLayoutSpec`、构建规范 `PluginBuildSpec`、服务接口 `PluginInterfaceSpec`）以及动态服务发现与通用互调透明网关的全面落地，审视当前整个系统，暴露出“三大核心断层”：

```
                    +-------------------------------------------------------+
                    |           Schema v3.1 全栈完整优化架构拓扑             |
                    +-------------------------------------------------------+
                                                |
            +-----------------------------------+-----------------------------------+
            |                                   |                                   |
            v                                   v                                   v
+-------------------------+         +-------------------------+         +-------------------------+
|  正交工作负载分类体系   |         |   21 个插件生态标准化   |         |   前端交互与状态机闭环  |
|                         |         |                         |         |                         |
| • 🌐 常驻微服务 (12)    |         | • 统一 Schema v3.1 清单 |         | • Lifecycle/Protocol 徽标|
| • ⚡ 弹性微函数 (4)     |         | • 物理目录规整 (src/bin)|         | • 保存即编译 FSM 状态机 |
| • 🛠️ 运维指令 (5)       |         | • 声明式 build.command  |         | • 微服务透明互调抽屉    |
| • 多维切面组合过滤器    |         | • Services 显式接口导出 |         | • Archetype 可视化向导  |
+-------------------------+         +-------------------------+         +-------------------------+
            |                                   |                                   |
            +-----------------+                 |                 +-----------------+
                              |                 |                 |
                              v                 v                 v
          +-----------------------------------------------------------------------+
          |                       宿主内核：统一发现、调度与透明互调              |
          |           GET /api/Plugins/Discovery  |  POST /api/Plugins/Invoke     |
          +-----------------------------------------------------------------------+
```

### 为什么必须现在彻底重构？
1. **最佳历史窗口期**：系统尚未正式对外发布，没有任何历史包袱，修改无需考虑兼容性负担，是一次性消灭模糊隐式推断的绝佳时机；
2. **正交性解耦要求**：消灭将“底层技术栈 (Wasm)”与“生命周期 (Resident)”并列的二元混淆分类；
3. **闭环工业级体验**：使开发者在 Web 控制台上享受类似现代 IDE 的“写代码 -> 自动热编译 -> 动态发现接口 -> 一键透明调用”极致流畅体验。

---

## 二、 第一篇：正交多维分类学与工作负载拓扑

### 1. 原有分类的三大致命缺陷
原有顶栏分类：`🧩 全部 (21)`、`⚡ 常驻微服务与守护 (12)`、`🔌 脚本轻量扩展 (5)`、`🚀 动态按需 FaaS (1)`、`⚡ Wasm 微函数 (3)`。
- **正交性破坏**：将生命周期（Resident）、执行时效（OneShot）、调度模式（Serverless）、底层字节码沙箱（Wasm）生硬平铺；
- **命名与认知冲突**：把非脚本的命令行工具叫成“脚本”；两个分类共用 `⚡` 图标；
- **二元混淆**：Wasm 微函数和 Serverless 本质都是“按需无状态函数计算”，却被人为撕裂。

### 2. 全新三大顶级工作负载分类 (Top-Level Archetypes)
遵循云原生（K8s / AWS / Cloudflare）计算机科学本质，重构为三大互斥且穷尽（MECE）的计算范式：

| 范式类别 | 图标与名称 | 对应插件数量 | 核心定义与内涵 | 包含插件清单 |
| :--- | :--- | :--- | :--- | :--- |
| **范式 A** | `🌐 常驻微服务与网关` | **12 个** | 长时运行、持有网络端口/套接字、由 Supervisor 守护、提供持续 API 服务的微服务。 | `mcp-bridge`, `lxc-manager`, `socks5-proxy`, `socks5-forwarder`, `demo-dotnet-service`, `chatgpt-channel`, `demo-py-api`, `demo-js-webhook`, `demo-sh-daemon`, `demo-ps-daemon`, `native-managed`, `native-resident` |
| **范式 B** | `⚡ 弹性微函数与计算` | **4 个** | **【归并原 FaaS 与 Wasm】**：无状态、纳秒级/毫秒级冷启动、按需拉起（Scale-to-Zero）、沙箱内存安全隔离。 | `demo-nativeaot-wasm`, `demo-dotnet-wasm`, `demo-wasm-function`, `demo-serverless-api` |
| **范式 C** | `🛠️ 运维指令与单次任务` | **5 个** | **【正名化，去除脚本偏见】**：有限生命周期、随调随走、执行系统运维指令或健康巡检的工具。 | `caddy-manager`, `caddy-server-manager`, `ssh-access-guard`, `sample-healthcheck`, `native-command-demo` |

### 3. 次级多维正交切面过滤器 (Secondary Slice Filters)
在顶栏下方提供细粒度正交组合过滤器：
- **生命周期切面 (Lifecycle)**：`全部` | `⚡ 解释型脚本` | `📦 预编译二进制` | `🔨 源码热构建`
- **通信协议切面 (Protocol)**：`全部` | `🌐 HTTP REST` | `🔌 UDS 本地套接字` | `⚡ Wasm ABI` | `⌨️ POSIX 命令行`
- **业务领域切面 (Category)**：`全部` | `网络穿透` | `系统运维` | `开发算力` | `安全审计` | `Web 服务`
- **健康运行状态 (State)**：`全部` | `运行中` | `待命就绪` | `异常降级` | `依赖缺失` | `已停止`

---

## 三、 第二篇：全量 21 扩展升级与物理目录标准化

### 1. 全量 21 个插件 Schema v3.1 映射大表

| 插件 ID | 插件名称 | Archetype 形态 | Lifecycle 生命周期 | Protocol 协议 | 入口规范 | 声明式构建命令 (`build.command`) |
| :--- | :--- | :--- | :--- | :--- | :--- | :--- |
| `mcp-bridge` | MCP 智能安全网关 | 常驻微服务 | SourceCompiled | Http | `mcp-bridge` | `CGO_ENABLED=0 go build -ldflags "-s -w" -o mcp-bridge ./cmd/mcp-bridge` |
| `lxc-manager` | Incus/LXC 容器管理器 | 常驻微服务 | SourceCompiled | Http | `lxc-manager` | `CGO_ENABLED=0 go build -ldflags "-s -w" -o lxc-manager ./cmd/lxc-manager` |
| `socks5-proxy` | SOCKS5 代理网关 | 常驻微服务 | SourceCompiled | Http | `socks5-proxy` | `CGO_ENABLED=0 go build -ldflags "-s -w" -o socks5-proxy ./cmd/socks5-proxy` |
| `socks5-forwarder`| 流量分发转发器 | 常驻微服务 | SourceCompiled | Http | `socks5-forwarder` | `CGO_ENABLED=0 go build -ldflags "-s -w" -o socks5-forwarder ./cmd/socks5-forwarder` |
| `demo-dotnet-service`| C# 现代微服务 | 常驻微服务 | SourceCompiled | Http | `bin/publish/demo-dotnet-service.dll`| `dotnet publish -c Release -o bin/publish` |
| `demo-nativeaot-wasm`| C# NativeAOT 极速微函数 | 弹性微函数 | SourceCompiled | WasmAbi | `demo-nativeaot-wasm.wasm` | `bflat build --arch wasm --os wasi src/Program.cs -o demo-nativeaot-wasm.wasm` |
| `demo-dotnet-wasm`| C# WASI 微函数 | 弹性微函数 | SourceCompiled | WasmAbi | `demo-dotnet-wasm.wasm` | `dotnet publish -c Release /p:TargetArchitecture=wasm` |
| `demo-wasm-function`| WAT 原生微函数 | 弹性微函数 | SourceCompiled | WasmAbi | `main.wasm` | `wat2wasm function.wat -o main.wasm` |
| `demo-serverless-api`| Serverless 动态函数 | 弹性微函数 | Interpreted | Http | `index.js` | *无需构建 (Interpreted No-Op)* |
| `demo-js-webhook` | Node.js Webhook | 常驻微服务 | Interpreted | Http | `index.js` | *无需构建 (Interpreted No-Op)* |
| `chatgpt-channel` | ChatGPT 消息通道 | 常驻微服务 | Interpreted | Http | `worker.py` | *无需构建 (Interpreted No-Op)* |
| `demo-py-api` | Python 轻量 API | 常驻微服务 | Interpreted | Http | `main.py` | *无需构建 (Interpreted No-Op)* |
| `demo-sh-daemon` | Bash 系统常驻监控 | 常驻微服务 | Interpreted | Http | `daemon.sh` | *无需构建 (Interpreted No-Op)* |
| `demo-ps-daemon` | PowerShell 资源守护 | 常驻微服务 | Interpreted | Http | `daemon.ps1` | *无需构建 (Interpreted No-Op)* |
| `caddy-manager` | Caddy 原生代理网关 | 运维指令 | PrebuiltBinary | CliArgs | `caddy` | *无需构建 (PrebuiltBinary No-Op)* |
| `caddy-server-manager`| Caddy 脚本化控制器 | 运维指令 | Interpreted | CliArgs | `hooks/status.js`| *无需构建 (Interpreted No-Op)* |
| `ssh-access-guard` | SSH 访问审计守卫 | 运维指令 | Interpreted | CliArgs | `hooks/records.sh`| *无需构建 (Interpreted No-Op)* |
| `sample-healthcheck`| 宿主健康检查探针 | 运维指令 | Interpreted | CliArgs | `hooks/check.sh` | *无需构建 (Interpreted No-Op)* |
| `native-command-demo`| 原生命令封装示例 | 运维指令 | PrebuiltBinary | CliArgs | `/bin/ls` | *无需构建 (PrebuiltBinary No-Op)* |
| `native-managed` | 原生受控常驻微服务 | 常驻微服务 | PrebuiltBinary | Http | `bin/native-service`| *无需构建 (PrebuiltBinary No-Op)* |
| `native-resident` | 原生常驻守护进程 | 常驻微服务 | PrebuiltBinary | Http | `bin/resident-app` | *无需构建 (PrebuiltBinary No-Op)* |

### 2. 物理目录结构标准化规约 (Layout Specification)
彻底消除源码散落与临时文件污染，各插件执行如下标准布局：
```
plugins/<plugin-id>/
├── plugin.json                 # 核心唯一描述清单 (Schema v3.1 规范)
├── README.md                   # 插件说明文档
├── src/                        # 纯源码文件 (Go / C# / Python / JS / Shell / WAT)
├── bin/                        # 编译生成产物 (ELF / DLL / WASM)
├── storage/                    # 持久化独占存储目录 (SQLite, KV DB)
├── run/                        # 运行时目录 (PID 文件, UDS Socket 套接字)
├── ui/                         # 专属前端 Webview 页面
└── hooks/                      # 运维钩子脚本
```

---

## 四、 第三篇：前端全栈交互与设计模式革新

### 1. 设计模式的工程落地
- **适配器策略模式 (`pluginFilterStrategies.js`)**：
  将多维过滤抽象为独立策略类（`WorkloadArchetypeFilterStrategy`, `LifecycleFilterStrategy`, `ProtocolFilterStrategy`, `CategoryFilterStrategy`, `KeywordFilterStrategy`, `StateFilterStrategy`），统一接入 `CompositePluginFilter`，毫秒级流式计算（< 0.5ms）；
- **状态机模式 (`PluginBuildStateMachine` & `PluginFilterStateMachine`)**：
  - 构建状态机：`Idle -> Saving -> Compiling -> Succeeded / Failed -> HotReloadReady`；
  - 过滤状态机：保证多条件切换时不会发生并发状态竞态。

### 2. 核心前端交互组件升级
1. **卡片元数据多维徽标呈现 (`PluginCard.js`)**：
   - 在卡片顶部不仅展示 Archetype，还清晰呈现：
     - `[⚡ 解释型]` (Cyan) / `[📦 预编译]` (Purple) / `[🔨 源码构建]` (Amber)
     - `[🌐 HTTP:18001]` / `[🔌 UDS]` / `[⚡ WasmAbi]`
2. **代码编辑器“保存即热编译”全自动闭环 (`PluginCodeModal.js`)**：
   - 当 `build.autoCompileOnSave: true` 时，按 `Ctrl+S` 或点击保存后，自动串行触发构建管线；
   - 耗时叠加提示（如 `保存并构建成功！耗时: 15ms`）；若失败自动展开 `PluginDebugConsole` 呈现错误行。
3. **微服务动态发现与透明调用抽屉 (`PluginServiceInvokeDrawer.js`)**：
   - 点击卡片或顶栏的“⚡ 服务调用”，直接调出通用透明互调抽屉；
   - 自动拉取 `GET /api/Plugins/Discovery` 列出的所有服务与 operations；
   - 输入参数，点击执行，直接向 `POST /api/Plugins/Invoke` 发送请求；
   - 呈现状态码、耗时 (ms)、响应格式化 JSON / 文本，实现微服务零延迟在线调试。
4. **基于 Archetype 的可视化新建向导 (`PluginCreateModal.js`)**：
   - 引导用户选择 4 大主流架构模版（Wasm 微函数、按需 FaaS、Go/C# 源码微服务、运维脚本），自动生成标准目录和清单。

---

## 五、 第四篇：性能保障与架构门禁防御体系

| 门禁机制 | 门禁规则与限制 | 防御目的 |
| :--- | :--- | :--- |
| **前端视口高度门禁** | `FE-CONTAINER-HEIGHT-LIMIT` | 严格限制列表与抽屉容器高度，严禁产生外溢双滚动条 |
| **前端非视图行数门禁** | `FE-FILE-SIZE-LIMIT` (< 400 行) | 保持单一职责，大组件拆分为独立子模块 |
| **C# 契约单文件门禁** | `LinuxArch026` (< 300 行) | 防止契约层出现巨石上帝类（God Class） |
| **Minimal API 端点映射** | `LinuxArch012` | 确保所有 Controller 端点同步在 `EndpointsMapper.g.cs` 映射 |
| **全仓安全凭据门禁** | `security-gate.cjs --full` | 毫秒级阻断任何 Token、密码、私钥硬编码泄漏 |

---

## 六、 第五篇：端到端实施路线图与验收矩阵

```
  [里程碑 1: 扩展生态全量升级]
    ├── 执行升级脚本升级全部 21 个 plugin.json 至 Schema v3.1
    └── 规范化各插件目录结构与 entrypoint 映射
           │
           v
  [里程碑 2: 前端策略与正交分类重构]
    ├── 在 pluginFilterStrategies.js 中实现三大范式与正交切面策略
    └── 在 PluginsView.js 中重构顶栏 Tab (常驻 12, 微函数 4, 运维 5)
           │
           v
  [里程碑 3: 前端交互与状态机闭环]
    ├── PluginCard.js 渲染 Lifecycle 与 Protocol 徽标
    ├── PluginCodeModal.js 实现保存即热编译 FSM
    └── 实现 PluginServiceInvokeDrawer.js 透明互调抽屉
           │
           v
  [里程碑 4: 全套门禁回归与远程 Dev 部署]
    ├── 422 单元测试 + 228 架构测试 + 前端门禁 8 项 + 安全门禁 100% 绿灯
    └── scripts/deploy-remote.ps1 -Mode Dev 极速部署
           │
           v
  [里程碑 5: Chrome DevTools MCP 真实验收]
    ├── 验证三大主分类 Tab 切换与数量统计
    ├── 验证微服务透明调用抽屉与保存即热编译
    └── 确认全流程 Console Errors: 0
```
