# MCP Bridge 配置持久化保证与脚本轻量扩展状态机生命周期治理调研与方案

## 1. 问题背景与根本原因深度调研

### 1.1 MCP Bridge 网关控制台“保存配置不会持久化”现象与认知差
用户在 MCP Bridge 网关控制台面板配置 OpenAI Tunnel 的 `TunnelID`、`PreShared Key`、`网络出口代理 (ProxyURL)` 后点击【💾 保存配置】，产生“配置未持久化”或“刷新后丢失/未生效”的强烈体感。深度源码分析发现以下四个根本原因：

1. **两阶段提交流程（Draft -> Apply）与用户心智模型脱节**：
   - MCP Bridge 采用类 GitOps 的两阶段配置模型：用户点击【💾 保存配置】仅触发 `tunnel.save`（即 `Store.SaveDraft`），配置仅被写入私有状态文件 `bridge-state.json` 的 `draftRevision`。
   - 运行中的 Tunnel 进程**没有重新连接，出站代理环境变量（`HTTP_PROXY / HTTPS_PROXY / ALL_PROXY`）也未刷新**，界面右侧状态依然显示 `⏳ 建立中...` 或 `未连接`。
   - 界面仅在另一卡片弱提示“草稿未应用变更：有待生效变更”，用户未意识到必须手动点击【🚀 应用生效配置 (Apply)】。
2. **Apply 生效事务遗漏了 Tunnel 控制器的热重载（Missing Tunnel Reload in Apply）**：
   - 即使用户手动点击了【🚀 应用生效配置 (Apply)】，`Gateway.Apply` 只刷新了第三方后端 MCP 工具注册表并发布 Snapshot，**从未调用 `tunnel.Controller` 或 `watchdog.Supervisor` 进行配置重载**。
   - 正在运行的 Tunnel 客户端依然持有旧凭证与旧代理，直到整个容器/进程完全重启才可能读取。
3. **状态投影（`status.get`）与前端回显覆盖缺陷**：
   - 用户保存配置后或重新打开 Webview 面板时，前端调用 `status.get` 获取状态并调用 `renderStatus(s)` 回填。
   - 在后端 `actionStatusGet` 中：
     ```go
     tunStatus := h.rt.Tunnel().Status()
     effectiveProxy := tunStatus.EffectiveProxy
     if effectiveProxy == "" && err == nil && st.Tunnel.ProxyURL != "" {
         effectiveProxy = st.Tunnel.ProxyURL
     }
     ```
     当 Tunnel 启动失败或未完成首次连接时，`tunStatus.EffectiveProxy` 为空字符串或运行时旧值；
   - 前端 `renderStatus` 中：`proxyInput.value = s.effectiveProxy || ''`。
   - 导致每次刷新或重新打开控制台，页面回填时用空值覆盖了用户刚刚在 Draft 中保存的代理地址，给用户造成“根本没有持久化”的错觉。
4. **缺少“保存并立即生效 (Save & Apply)”原子事务**：
   - 用户配置隧道场景下，99.9% 期望“保存即生效”。缺乏一键保存并生效的直通路径。

---

### 1.2 脚本轻量扩展“不论启用禁用在状态搜索中都是已停止”根本原因
用户在插件中心状态快捷搜索下拉框中选择【⏹️ 已停止】时，不论脚本扩展（如 Bash/PowerShell 一次性脚本、原生命令演示等）当前是启用还是禁用，均全部出现在【已停止】列表中；而在【🟢 运行中】时均不出现。

1. **语义混淆与粗暴折叠（Stopped vs Disabled Coalescing）**：
   - 在前端 `pluginFilterStrategies.js` 中：
     ```javascript
     case 'stopped':
       return plugin.state === 0 || plugin.state === 6;
     ```
     后端 `PluginState.Stopped = 0`，`PluginState.Disabled = 6`。
     过滤逻辑将 `0`（未运行/就绪）与 `6`（显式禁用）粗暴地归为同一个 `stopped` 分支。
2. **运行画像（Profile）模型的生命周期差异**：
   - **常驻微服务与守护（`Resident`, Profile = 0）**：属于 Long-Running Daemon，有常驻 PID。正常工作时为 `Running (2)`，人工关闭时为 `Stopped (0)`。
   - **脚本轻量扩展（`OneShot / Script`, Profile = 1）**：属于 On-Demand 单次按需任务，**没有常驻后台进程**。
     当它处于“已启用 (Enabled)”时，其真实业务状态是随时准备响应执行的 **“待命中 / 就绪” (Ready / Standby)**；
     当它被显式停用时，其真实业务状态是 **“已禁用” (Disabled)**。
3. **UI 过滤选项缺失**：
   - 状态下拉框仅有：`🟢 运行中`, `⚠️ 异常/降级`, `🧩 缺少依赖`, `⏹️ 已停止`。
   - 缺少 `⚡ 待命/就绪 (Ready)` 与 `🚫 已禁用 (Disabled)` 独立维度，使得脚本扩展无法准确检索。

---

### 1.3 临界断点下工具栏排版折行错位（用户截图 1 排版缺陷）
- 在 `PluginsView.js` 中，控制栏采用 `xl:flex-row justify-between`，右侧筛选栏为 `flex-wrap sm:flex-nowrap`。
- 当视口宽度处于 1280px ~ 1450px 之间（如带有侧边栏的笔记本屏幕），右侧区域无法容纳全部控件，`[ 全部状态 ]` 下拉框被单独挤压折行到右上角第一行，下方是 `[ 全部分类 ]`、搜索框和按钮，左侧导航 Tab 下方形成大面积突兀空白，布局断裂。

---

## 2. 业界主流方案对比与借鉴

| 系统 / 工具 | 一次性按需任务与常驻守护状态区分 | 配置生效模型（Draft vs Apply） | 控制台出站代理与隧道持久化 |
| :--- | :--- | :--- | :--- |
| **Kubernetes** | 严格区分 `DaemonSet/Deployment` (Running/CrashLoop) 与 `Job/CronJob` (`Pending/Ready/Complete`)，绝不会把待命 Job 归类为 Failed/Stopped Pod | 声明式 Spec -> Controller 自动对齐 Status；提供 `kubectl apply` 单阶段直通与 Dry-run 草稿模式 | ConfigMap/Secret 挂载后通过 Inotify 或 Pod 重启热加载环境变量与 Proxy |
| **Systemd** | 区分 `Type=simple/forking`（Active (running) / Inactive (dead)）与 `Type=oneshot`（Active (exited) / Standby） | `systemctl edit` 写入配置后需 `systemctl daemon-reload`，但管理界面通常提供“Save & Reload”原子动作 | Unit 文件中配置 `Environment="HTTP_PROXY=..."`，Reload 时重启 ExecStart |
| **OpenWrt LuCI** | 区分系统守护进程与触发式脚本包（Installed/Enabled vs Disabled） | 经典两阶段：“Save”（保存到 uci 草稿）与 **“Save & Apply”**（默认主按钮），一键落盘并重启服务 | 代理与 VPN 客户端保存时自动重新建立 Socket 隧道，状态页面优先回显 UCI Spec 配置 |
| **Caddy / Envoy** | 动态 API 纳管，区分 HTTP 监听器与按需插件 | 提供 `/load`（全量原子切换）与 `/config/`（针对性更新），更新后毫秒级热平滑重启 Worker | 动态下发上游 Proxy 配置，立即关闭旧连接池并以新代理重建 |

---

## 3. 现有基础设施与可复用能力确认

1. **配置存储 (`plugins/mcp-bridge/internal/state/store.go`)**：
   - 已具备 `SaveDraft` 与 `Commit` 的原子落盘能力（`writeAtomic` 写入 `bridge-state.json`，原子重命名保证断电一致性）。
   - 可直接复用 `Commit` 事务，在 `saveTunnel` 携带 `applyNow=true` 时执行一步到位。
2. **隧道控制器 (`plugins/mcp-bridge/internal/tunnel/controller.go`)**：
   - 具备 `Start(ctx, tunnelCfg)` 与 `Stop(ctx)`。
   - 新增 `Reload(ctx, tunnelCfg)` 组合方法：在互斥锁保护下优雅断开旧长轮询、重设出站代理环境变量、重设 `userPaused=false` 并拉起新隧道客户端。
3. **前端状态机与检索策略库 (`pluginFilterStrategies.js` & `pluginStateMachine.js`)**：
   - 已建立基于 Strategy 模式的复合过滤器管道（`CompositePluginFilter`、`ProfileFilterStrategy`、`CategoryFilterStrategy`、`KeywordFilterStrategy`、`StateFilterStrategy`）。
   - `PluginStateMachine.getStateMeta` 中已具备脚本扩展映射为 `已就绪` 的展示能力，可直接复用并扩展到 `StateFilterStrategy`。

---

## 4. 架构设计与具体解决方案

### 4.1 方案一：MCP Bridge 配置“保存并立即生效 (Save & Apply)”闭环
1. **API DTO 升级**：
   - `SaveTunnelRequestDTO` 增加 `applyNow: bool`（默认为 true）；
   - `StatusDTO` 显式增加 `proxyUrl: string`（配置项）与 `effectiveProxy: string`（运行时生效值），彻底解耦用户意图与底层运行时状态。
2. **Handler 事务改造 (`actionTunnelSave`)**：
   - 若 `applyNow == true`：保存 Draft 成功后立即触发 Commit，并调用 `h.rt.Tunnel().Reload(ctx, candidate.Tunnel)`，热生效出站代理与连接凭据。
   - 返回 `MutationResultDTO{Success: true, Applied: true, Revision: rev}`。
3. **Web 控制台交互优化 (`index.html`)**：
   - 主按钮升级为【💾 保存并立即生效 (Save & Apply)】，副按钮保留【📝 仅存草稿】；
   - `renderStatus` 中回填代理输入框时优先使用 `s.proxyUrl`，彻底杜绝刷新页面代理配置被抹空的缺陷；
   - 在【🚀 应用生效配置 (Apply)】动作中同步执行 `h.rt.Tunnel().Reload(ctx, stateObj.Tunnel)`，实现 MCP 工具变更与隧道重载的双重生效。

### 4.2 方案二：脚本轻量扩展生命周期状态机与过滤策略正名
1. **`StateFilterStrategy` 精准语义解耦**：
   - `running`: `plugin.state === 2`；
   - `ready`: `plugin.profile === 1 && (plugin.state === 0 || plugin.state === 2)`（脚本已启用待命中）；
   - `stopped`: `plugin.profile === 0 && plugin.state === 0`（仅匹配常驻微服务的人工停止）；
   - `disabled`: `plugin.state === 6`（显式禁用的扩展）；
   - `degraded`: `plugin.state === 3 || 4 || 5`；
   - `missing`: `plugin.state === 7 || 8 || isMissingRuntime || isMissingDependencies`。
2. **权重排序策略 (`getPluginStateWeight`) 调优**：
   - 运行中（1）-> 启动中（2）-> **脚本就绪待命（3）** -> 异常降级（4）-> 缺失依赖（5）-> 常驻已停止（6）-> 已禁用（7）。
3. **状态下拉框增加明确选项**：
   - `<option value="all">全部状态</option>`
   - `<option value="running">🟢 运行中</option>`
   - `<option value="ready">⚡ 待命/就绪</option>`
   - `<option value="stopped">⏹️ 已停止</option>`
   - `<option value="disabled">🚫 已禁用</option>`
   - `<option value="degraded">⚠️ 异常/降级</option>`
   - `<option value="missing">🧩 缺少依赖</option>`

### 4.3 方案三：插件中心工具栏响应式流式布局治理
1. **防止单个 Select 折行成为孤岛**：
   - 将 `全部状态` 与 `全部分类` 放入紧凑的 Flex 控件组，并设置 `shrink-0`；
   - 在 2XL（1536px）以下采用自适应双行排版：
     - 第一行：导航 Tab（全部/常驻/脚本）+ 右侧核心操作（导入/新建/刷新）；
     - 第二行：状态过滤 + 分类过滤 + 搜索框，平铺自适应。
   - 在 2XL 及以上宽屏环境统一单行对齐，彻底根除孤立折行。

---

## 5. 门禁与防回归保障设计

1. **前端单元测试 (`tests/frontend/plugin-filter-and-state-machine.test.mjs`)**：
   - 验证 `StateFilterStrategy` 在 `ready` 状态下仅筛选出已启用的 OneShot 插件；
   - 验证 `StateFilterStrategy` 在 `stopped` 状态下排除已启用的 OneShot 插件；
   - 验证 `StateFilterStrategy` 在 `disabled` 状态下能够精准筛选出状态为 6 的扩展。
2. **后端 Go 单元测试 (`plugins/mcp-bridge/internal/admin/handler_test.go`)**：
   - 验证 `actionTunnelSave` 传入 `applyNow=true` 时，原子完成落盘、Commit 并触发状态提升；
   - 验证 `actionStatusGet` 正确返回配置的 `ProxyURL`，避免回显覆盖。
3. **C# 架构门禁 (`tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs`)**：
   - 维持单文件行数低于 700 行（`LinuxArch026`）；
   - 维持全部 216 项架构断言通过。
