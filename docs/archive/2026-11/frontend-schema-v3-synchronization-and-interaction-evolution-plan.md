# 前端 Schema v3.1 全面同步演进与微服务动态发现调用交互方案

> **文件标识**：`docs/frontend-schema-v3-synchronization-and-interaction-evolution-plan.md`  
> **状态**：方案制定与架构调研阶段（已获授权：由于未正式发布，完全无需负担向下兼容）  
> **核心主题**：前端多维元数据感知、微服务动态发现调用面板、保存即热编译状态机、Archetype 新建向导与设计模式优化

---

## 一、 头脑风暴与痛点深度分析 (Brainstorming & Pain Points)

在系统后端完成 **Schema v3.1 通用多维架构**（生命周期 `PluginLifecycle`、协议 `InterfaceProtocol`、架构形态 `PluginArchetype`、目录规范 `PluginLayoutSpec`、构建规范 `PluginBuildSpec`、服务接口 `PluginInterfaceSpec`）以及动态服务发现（`GET /api/Plugins/Discovery`）与透明互调网关（`POST /api/Plugins/Invoke`）之后，审视现有前端系统（Vue 3 原生 ESM 架构），暴露出以下 **5 大核心痛点与体验割裂**：

```
+---------------------------------------------------------------------------------------------------+
|                                前端现状与 Schema v3.1 契约的脱节问题                                |
+---------------------------------------------------------------------------------------------------+
| 1. 卡片元数据信息严重缺失   | 仅展示 Profile 与 Runtime，缺失 Lifecycle (解释型/预编译/源码构建)     |
|                            | 与 Protocol (HTTP/UDS/WasmAbi/StdioJsonRpc) 一等公民标识。           |
+----------------------------+----------------------------------------------------------------------+
| 2. 动态发现能力未可视化     | 后端提供了强大的服务发现拓扑，前端却无微服务操作树列表，用户无法直观   |
|                            | 浏览系统当前对外开放的所有微服务契约与 Operations。                  |
+----------------------------+----------------------------------------------------------------------+
| 3. 透明调用网关未提供交互   | 现有前端仅能执行预先写死的几个 action 按钮；缺乏一个通用的调试与调用抽屉 |
|                            | (Invocation Drawer)，无法对任意发现的服务输入参数并实时查看透明调用结果。|
+----------------------------+----------------------------------------------------------------------+
| 4. 编辑与构建缺乏热联动     | 编辑弹窗中虽然增加了“编译构建”按钮，但没有与“保存修改”联动；未实现工业级 |
|                            | IDE 的“保存即热编译 (Auto-Compile on Save)”与构建状态机。              |
+----------------------------+----------------------------------------------------------------------+
| 5. 新建插件向导停留在旧版   | 新建弹窗仍是单调的 Profile/Runtime 表单，未引导用户选择现代化 Archetype |
|                            | 模板，无法自动初始化符合 Schema v3.1 的标准规范目录与预置清单。      |
+---------------------------------------------------------------------------------------------------+
```

---

## 二、 业界主流方案对比与借鉴 (Industry Benchmark)

| 平台 / 规范 | 核心实现与优势 | 对 LWT 前端的借鉴价值 |
| :--- | :--- | :--- |
| **Dapr Dashboard** | 可视化呈现所有 Dapr Components、Actors 与 Service Invocation 端点，支持直接在 UI 上构造请求并发起互调测试。 | **服务发现与通用互调面板**：直接在前端卡片或抽屉中展示导出的 Services 与 Operations，提供透明调用调试控制台。 |
| **Swagger / OpenAPI UI & Scalar** | 自动解析服务契约，针对每个 Operation 生成交互式调用表单，实时呈现 HTTP Status Code、Latency (ms)、Payload 与 Header。 | **动态参数表单与响应呈现**：统一包装 `POST /api/Plugins/Invoke` 响应结构，展示耗时、协议、Payload 格式化语法高亮。 |
| **VS Code / Cloud Studio Web** | 当检测到工程具有构建配置（如 `tasks.json` 或 `build` 规范）时，文件保存自动触发增量编译管道，状态栏提供实时进度与退出码反馈。 | **保存即编译 (Auto-Compile on Save) 状态机**：在 `PluginCodeModal.js` 中将保存与构建管道结合，实现真正的代码免重启即时验证。 |
| **Vercel / Cloudflare Workers Dashboard** | 新建项目时引导用户选择架构模板（Serverless Node、Edge Wasm、Static Site、Go Native），自动生成预置配置与标准目录结构。 | **Archetype 可视化新建向导**：彻底重构 `PluginCreateModal.js`，按架构原型模板一键生成标准代码与清单。 |

---

## 三、 设计模式在前端的落地优化 (Design Patterns Optimization)

### 1. 适配器策略模式 (Adapter Strategy Pattern)
- **多维过滤策略扩展 (`pluginFilterStrategies.js`)**：
  在现有 `ProfileFilterStrategy` 与 `CategoryFilterStrategy` 的基础上，增加：
  - `LifecycleFilterStrategy`：支持按“纯解释型免编译 (Interpreted)”、“预编译二进制 (PrebuiltBinary)”、“源码热构建 (SourceCompiled)”进行高内聚过滤；
  - `ProtocolFilterStrategy`：支持按“🌐 HTTP REST”、“🔌 UDS 本地套接字”、“⚡ Wasm ABI 内存导出”进行过滤。
- **动态调用请求与结果适配器 (`PluginInvocationAdapter`)**：
  根据插件的 `protocol`（如 WasmAbi vs Http vs CliArgs），前端自动适配入参形态：
  - `WasmAbi`：自动适配为 `{ pluginId, actionOrPath: op, payload: textParam }`；
  - `Http`：自动适配为 JSON Body，并支持添加自定义 Request Headers；
  - 响应结果适配器：根据 `contentType` 自动在纯文本、JSON 格式化视图、表格视图之间切换渲染。

### 2. 状态机模式 (Finite State Machine - FSM)
- **构建与热更状态机 (`PluginBuildStateMachine`)**：
  ```
  +--------+   Save (Ctrl+S)   +---------------+  Command Exec  +-----------------+
  |  Idle  | ----------------> |  SavingCode   | -------------> |   Compiling     |
  +--------+                   +---------------+                +-----------------+
      ^                                                                  |
      |                     ExitCode == 0                                | ExitCode != 0
      +--------------------------------------------------+               |
      |                 (HotReload Ready)                |               v
      |                                                  |      +-----------------+
      +--------------------------------------------------+----- |  BuildFailed    |
                                                                | (Expand Console)|
                                                                +-----------------+
  ```
  - 当触发保存时，状态机将编辑弹窗与卡片置为 `Compiling` 状态，禁用并发破坏性操作；
  - 若编译成功，自动刷新文件树中的产物（如 `.wasm` 或 `bin/` 文件），提示编译耗时并恢复 `Ready`；
  - 若编译失败，状态机自动阻断后续热更、展开控制台并定位到标准错误行。

---

## 四、 具体功能改造方案与 UI 视觉规范

### 1. 插件卡片多维元数据徽标重构 (`PluginCard.js`)
在卡片头部与状态栏，补充展示 Schema v3.1 的核心维度徽标：
- **生命周期徽标 (Lifecycle Badge)**：
  - `Interpreted`：`[⚡ 解释型免编译]`（Cyan/青色柔光徽章）；
  - `PrebuiltBinary`：`[📦 预编译二进制]`（Purple/紫色徽章）；
  - `SourceCompiled`：`[🔨 源码热构建]`（Amber/琥珀色徽章，带构建提示）。
- **通信协议与端点徽标 (Protocol Badge)**：
  - `Http`：`[🌐 HTTP : {Port}]`（绿色徽章，支持点击直接访问或复制）；
  - `UnixSocket`：`[🔌 UDS 本地套接字]`（蓝灰徽章）；
  - `WasmAbi`：`[⚡ WASM 导出符号]`（黄绿色徽章）。

### 2. 微服务动态发现与透明调用抽屉 (`PluginServiceInvokeDrawer.js`)
- **功能定位**：作为主界面或卡片右上角的全新交互组件，提供“微服务拓扑与动态调用中心”；
- **核心视图结构**：
  1. **服务树导航区**：左侧展示通过 `GET /api/Plugins/Discovery` 获取到的服务树（如 `calculator -> fib, calc, echo`；`api -> hello, status`）；
  2. **请求构造区**：右侧展示当前选中 Operation 的参数输入框（支持纯文本或 JSON 语法高亮）；
  3. **一键调用与度量响应区**：
     - 点击“🚀 执行调用 (Invoke)”，直接向 `POST /api/Plugins/Invoke` 发送统一请求；
     - 右侧下方卡片实时展示：HTTP 状态码（`200 OK` / `503 Unavailable`）、调用延迟（`{durationMs} ms`）、响应格式（`application/json`）以及格式化美化后的响应内容；
     - 提供一键复制结果、历史调用回溯等实用功能。

### 3. 代码编辑器“保存即热编译”全自动闭环 (`PluginCodeModal.js`)
- **智能策略感知**：
  - 若当前插件是 `SourceCompiled`，且 `plugin.json` 中配置了 `build.autoCompileOnSave: true`；
  - 用户按下 `Ctrl + S` 或点击“💾 保存修改”时，前端在调用 `/Files/Save` 成功后，**自动静默串行触发 `/Build` 接口**；
  - 编译耗时实时叠加入保存提示（例如：`保存并编译成功！耗时: 12ms`）；
  - 若编译报错，自动展开 `PluginDebugConsole`，将编译错误直接呈现在开发者眼前，彻底实现秒级即时反馈（Sub-second Feedback Loop）。

### 4. 基于 Archetype 的全新插件创建向导 (`PluginCreateModal.js`)
彻底废弃旧版模糊表单，重构为 **4 大主流架构模版卡片化选择**：
1. **⚡ Wasm 极速微函数模板 (Wasm Micro-Function)**：
   - 自动生成 `archetype: { profile: "Wasm", lifecycle: "SourceCompiled", protocol: "WasmAbi" }`；
   - 预置 `src/main.wat` 或 `src/Program.cs`，预置 `bflat` 或 `wat2wasm` 构建命令。
2. **🚀 Serverless 动态轻函数模板 (Serverless Function)**：
   - 自动生成 `archetype: { profile: "Serverless", lifecycle: "Interpreted", protocol: "Http" }`；
   - 预置轻量 Node.js / Python 代码，支持按需冷启动调度。
3. **🧩 声明式源码微服务模板 (Go / C# Micro-Service)**：
   - 自动生成 `archetype: { profile: "Resident", lifecycle: "SourceCompiled", protocol: "Http" }`；
   - 预置标准目录结构 (`src/`, `bin/`, `ui/`) 与 `go build` 构建规则。
4. **🛠️ 解释型运维扩展脚本模板 (Python / Bash OneShot)**：
   - 自动生成 `archetype: { profile: "OneShot", lifecycle: "Interpreted", protocol: "CliArgs" }`；
   - 即写即跑，零构建负担。

---

## 五、 性能优化与前端架构门禁防御

1. **逻辑视口高度限制与虚拟滚动防御 (`FE-CONTAINER-HEIGHT-LIMIT`)**：
   - 服务发现树与插件卡片网格严格保持 `max-h-[calc(100vh-...)]` 与 `overflow-y-auto`，绝对杜绝页面整体产生双重滚动条；
2. **ESM 符号导入与全量门禁保障**：
   - 所有新建子组件（如 `PluginServiceInvokeDrawer.js`）统一采用原生 ESM 模块，在 `frontend-gate.cjs` 中注册并受门禁守护；
   - 必须通过 `plugin-filter-and-state-machine.test.mjs` 测试套件，覆盖新增过滤策略的单元验证。
