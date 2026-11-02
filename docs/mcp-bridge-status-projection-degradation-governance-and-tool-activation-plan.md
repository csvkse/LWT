# MCP Bridge 状态投影、服务降级治理与工具激活生命周期研发方案

## 1. 问题现场与用户痛点调研

### 1.1 现场现象
用户在 `http://10.126.1.70:5271/plugins/mcp-bridge` 控制台进行操作时反馈两个问题：
1. **为什么 应用生效配置 (Apply) 始终提示 服务降级？**
   - 界面弹出警告：`⚠️ 配置已成功生效！但有 4 个服务降级: github-mcp (exec: "npx": not found), weather-sse (127.0.0.1:8000: connection refused), fetch (exec: "uvx": not found), filesystem (exec: "npx": not found)`；
   - 每次点击 Apply 均重复提示，无法直观消除。
2. **为什么 已激活工具数始终 0 个？**
   - 界面顶部运行状态卡片中明确显示：
     `已配置第三方服务数: 6 个`
     `已激活工具数: 0 个`
   - 即使真实工具已配置甚至部分服务已被激活，该指标恒为 0。

### 1.2 根因分析 (Root Cause Analysis)

#### 根因 1：`status.get` 状态投影严重缺失（展示层致命断层）
- **Go 控制面**：`internal/admin/dto.go` 中定义的 `StatusDTO` 结构体中缺少 `ActiveToolCount` 字段；`internal/admin/handler.go` 中的 `actionStatusGet` 仅读取了 `backendCount`，未从当前激活的快照（`g.registry.Current().Tools`）中统计已暴露工具数量；
- **Web 控制台**：前端 `index.html` 中的 `renderStatus(s)` 试图从 `s.activeToolCount` 读取数值并赋给 `#activeToolCountVal`。由于返回的 JSON 中无此字段（`undefined`），页面强制回退显示为 `0`；
- **时序掩盖**：点击 `config.apply` 时，响应体虽然返回了正确的 `activeToolCount: 3`，但紧接着调用的 `refreshStatus()` 重新拉取了缺字段的 `status.get`，瞬间将页面上的 `3` 重新冲刷为 `0`。

#### 根因 2：工具激活的显式审核生命周期与 UI 交互断层（管控层功能缺失）
- **安全模型规范**：根据 MCP Bridge 架构设计（`internal/catalog/policy.go`），出于企业级安全防护（Explicit Deny-All 最小特权原则），已纳管的后端服务不会默认将其内部所有工具裸露暴露至网关门面，必须经过 `Discover -> Select Tools -> Apply` 显式审核授权落盘（`state.Selections`）；
- **前端交互缺失**：Webview 控制台目前仅提供了“测试、探测、启停、删除”，**完全没有“工具管理 / 工具授权勾选”的入口与模态框**！用户无法在前端直观看到服务包含哪些工具，更无法进行勾选或一键全选，导致工具选择集（`selections`）始终为空。

#### 根因 3：环境不支持的服务持续处于启用状态且缺乏一键治理能力
- 用户通过“导入标准 MCP 配置”导入了行业常见的配置（如包含 `npx @modelcontextprotocol/server-github`、`uvx mcp-server-fetch` 等）；
- Alpine Linux 容器内仅预装了 Node.js 原生运行体，并未预置全局 npm/npx 或 Python uv/uvx，且未运行本地 8000 端口的 SSE 服务；
- 这 4 个服务导入后默认 `enabled: true`。每次 Apply 时网关必然进行探活，探测失败即记录为 `degradedServers`；
- 前端表格中没有清晰标识服务的健康/降级状态，也没有提供“一键禁用不可用服务”或前置环境预检，导致用户误以为系统陷入了不可自愈的异常循环。

---

## 2. 业界主流方案调研与对比

| 架构维度 | 业界主流实践 (K8s / Envoy / FastMCP / Claude Desktop) | 本项目原有实现 | 本次改造增强方案 |
| :--- | :--- | :--- | :--- |
| **状态投影 (Status Projection)** | Spec 与 Status 严格隔离；Status 必须包含观测世代、健康实例数、激活端点数 (`activeEndpointsCount`) | `StatusDTO` 仅包含 `draftRevision` 和 `backendCount`，遗漏了 `activeToolCount` | 在 `StatusDTO` 与 C# `McpBridgeStatusDto` 补充 `activeToolCount`，从 `g.registry.Current()` 实时动态投影 |
| **工具暴露授权 (Tool Governance)** | 最小特权白名单或智能全选；提供可视化工具列表勾选抽屉与 Schema 预览 | 仅有后端 `tools.select` API，Web 前端完全缺失对应 UI 入口 | 增加“🛠 工具管理”模态框，直观呈现工具清单、描述与参数 Schema，支持一键全选/反选与精准授权 |
| **服务降级治理 (Degradation)** | 区分 `Disabled`、`Degraded`、`Healthy`；支持前置环境校验 (`lookPath`) 与一键隔离/熔断 | 仅在 Apply 时以 Toast 显示降级文本，表格内无降级徽章，无法一键禁用 | 表格实时呈现健康度徽章；Toast 警告中直接提供“一键禁用降级服务”快捷动作 |
| **导入体验 (Onboarding)** | 导入后自动触发环境可用性预检与工具初探，健康服务支持默认安全暴露 | 导入后服务处于无工具选中状态，且不可用服务被全量启用 | 导入时支持预检选项，智能激活健康工具，对缺失环境的服务标记提示 |

---

## 3. 具体实施方案

### 3.1 补充后端状态投影 (`ActiveToolCount`)
1. **Go 微服务**：
   - 在 `internal/admin/dto.go` 中为 `StatusDTO` 新增 `ActiveToolCount int `json:"activeToolCount"``；
   - 在 `internal/admin/handler.go` 中，在 `actionStatusGet` 内调用 `h.rt.Registry().Current()` 获取已激活快照，将 `len(currentSnap.Tools)` 赋予 `dto.ActiveToolCount`；
   - 同步在 `handler_test.go` 中增加单测断言。
2. **C# 契约与控制器**：
   - 在 `McpBridgeModels.cs` 的 `McpBridgeStatusDto` 中新增 `public int ActiveToolCount { get; init; }`；
   - 确保 Native AOT 序列化源生成器支持。

### 3.2 增加前端“🛠 工具管理 (Select Tools)”模态框
1. **UI 入口**：在服务列表的操作栏中，增加“🛠 工具”按钮；
2. **交互流程**：
   - 点击后呼出 `#toolSelectModal`，调用 `tools.list?id={id}` 获取后端所有工具及其当前勾选状态；
   - 列表展示：工具原始名称、暴露名称、描述、参数 JSON Schema 折叠预览、勾选复选框；
   - 提供快捷操作：“☑️ 全选”、“⬜️ 全不选”；
   - 点击“保存勾选”后调用 `tools.select` 将选择集落入草稿；
   - 成功后刷新状态并高亮提示“变更已保存至草稿，请点击右上角【🚀 应用生效配置】使工具正式上线对外提供服务”。

### 3.3 增强降级服务治理与状态展示
1. **服务表格状态徽章增强**：
   - 根据最近一次 Apply 的结果或探活状态，在表格中直观标识：`🟢 正常 (N 工具)`、`⚠️ 降级 (原因提示)`、`⏸ 已禁用`；
2. **一键禁用不可用服务**：
   - 当 Apply 返回 `degradedServers` 时，在警告框旁提供快捷按钮：“⚡️ 一键禁用这 N 个降级服务”；
   - 点击后并发/批量调用 `servers.toggle` 将其 `enabled` 置为 `false`，再次 Apply 时系统即为 100% 全绿健康状态。

---

## 4. 架构门禁规则 (`LinuxArch054`)

在 `tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs` 中增加门禁测试：
1. Go `StatusDTO` 必须包含 `ActiveToolCount` 强类型定义；
2. Go `handler.go` `actionStatusGet` 必须从 `Registry` 获取当前激活工具数量；
3. Webview 控制台 `index.html` 必须具备 `#toolSelectModal`、`openToolSelectModal`、`submitToolSelection`；
4. Webview 控制台 `index.html` 必须具备一键禁用降级服务快捷能力（`disableDegradedServers`）；
5. C# 契约 `McpBridgeStatusDto` 必须包含 `ActiveToolCount`。
