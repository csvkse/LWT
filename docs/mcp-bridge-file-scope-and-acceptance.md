# MCP Bridge 修改文件范围与验收标准

> 2026-11-01 修订：按[渠道常驻服务复核](chatgpt-channel-service-reassessment.md)复用通用 Native/Control，取消重复 adapter/hook/token 仓库。直接升级项目示例，不兼容旧插件；旧 MCP HTTP+SSE 是第三方网络互通，仍保留。

日期：2026-11-01。配套[详细实施计划](mcp-bridge-implementation-plan.md)。以下区分已有底座与拟新增代码；**本次仅修改文档，工作区底座已有新代码但未在本次构建或测试，不代表已完成集成**。

## 1. 文件范围与代码边界

路径相对于当前 LinuxWebTool 仓库根。Go 源码位于独立 `workers/mcp-bridge`，不加入 .NET solution；C# 新模块按既有 Features 目录与分层落位。新代码遵守现有单文件门禁，不把 Bridge 管理逻辑塞进 WorkersView 或 WorkersManager 大文件。

### G1：新增 Go 模块（相对 `workers/mcp-bridge/`）

| 文件 | 任务 | 责任 |
|---|---|---|
| `go.mod`、`go.sum`、`SDK-COMPATIBILITY.md` | T0 | SDK/toolchain 锁定、协议与生命周期保证记录 |
| `cmd/mcp-bridge/main.go` | T6 | CLI、signal/context、组合根；无业务路由实现 |
| `internal/config/model.go`、`validate.go` | T1 | DTO、限额、目标与选择校验 |
| `internal/state/store.go`、`secrets.go`、`owner.go` | T1 | 有版本存储、秘密、进程所有权 |
| `internal/backend/session.go`、`manager.go` | T2 | 统一 client 接口、隔离 session/lease/配额 |
| `internal/backend/stdio.go`、`http.go`、`sse.go` | T2 | 三种 transport 的官方 SDK 适配 |
| `internal/catalog/snapshot.go`、`policy.go`、`names.go` | T3 | 不可变目录、显式 allowlist、稳定工具名 |
| `internal/gateway/gateway.go`、`facade.go`、`admission.go` | T4 | tools/list/call、Apply、执行授权线性化点 |
| `internal/tunnel/controller.go`、`recovery.go` | T5 | 官方 Tunnel SDK 生命周期与恢复边界 |
| `internal/runtime/runtime.go`、`health.go` | T5 | 进程生命周期、分层健康、停止结算 |
| `internal/admin/handler.go`、`dto.go`、`audit.go`、`jobs.go` | T6 | 通用 Control 协议、脱敏 DTO、有界审计和短期发现任务 |
| `internal/testkit/mcp_fixture.go`、`controlplane_fixture.go`、`stdiofixture/main.go` | T0/T2 | 确定性的 MCP/control-plane/真实子进程 fixtures |
| 对应包内 `*_test.go`，含 `internal/tunnel/sdk_contract_test.go`、`internal/admin/handler_test.go` | T0–T6 | 行为、并发、失败路径、协议契约测试 |
| `packaging/worker.json`、`packaging/bridge.example.json`、`README.md`、`ACCEPTANCE.md` | T10/T11 | 无秘密的包模板、安装与验收记录 |

这是责任拆分，不要求每个两三行 helper 单独建文件。后续拆分应保持同包内聚；不能把未知功能提前生成空框架。

### W1：修改现有 Worker 契约与运行时

| 现有文件 | 修改内容 |
|---|---|
| `src/LinuxWebTool.Contracts/Features/Workers/Contracts/WorkerModels.cs` | 核验已有 Native=6；新 manifest、argv、AutoStart 与 Control 声明按通用底座统一 |
| `src/LinuxWebTool.Contracts/Features/Workers/Contracts/WorkerOperationModels.cs` | 创建/更新请求传递上述配置，避免编辑后静默丢失；若行数超限按现有类型责任拆出新文件 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/Adapters/IWorkerRuntimeAdapter.cs` | WorkerStartSpec 采用单一 ArgumentList；语言适配器同步升级，不保留字符串 fallback |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.cs` | 核验已有 Native 注册、argv、runId；通用 Control 启停交接下沉生命周期责任范围，不嵌 Bridge 业务 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkerJsonContext.cs` | 需要的 Worker 新类型源生成注册 |

复用：`src/LinuxWebTool.Infrastructure/Features/Workers/Adapters/WorkerRuntimeAdapters.cs` 中已有 NativeRuntimeAdapter，以及 Plugins/Adapters/NativeProgramResolver.cs。核验包内固定入口、regular file/reparse、平台、执行权限和 argv，不新增第二个 NativeRuntimeAdapter.cs，不将任意 binary name 拼成 shell 指令。

### W2：通用 Worker/Control 生命周期缺口

- 复用 `src/LinuxWebTool.Contracts/Features/Plugins/Contracts/NativeToolModels.cs` 的 ResidentControlSpec 与 `IPluginToolServices.cs` 的 IResidentControlClient，不新增平行 IWorkerLifecycleHook。
- 加固 `src/LinuxWebTool.Infrastructure/Features/Plugins/Adapters/ResidentControlClient.cs`：受信回环目标、非空当前 token、身份握手、限额、超时/取消/错误结构化。
- 在现有 WorkersManager 生命周期职责内接通当前实例身份、显式 AutoStart、readiness、shutdown/drain；必要时提取 partial，不能只为 Bridge 新建第二套 supervisor。
- 现有 `WorkersManager.Lifecycle.cs` 补 Native 更新与当前进程结算，语言 Worker 按新契约回归。
- `WorkerStateMachine.cs` 只在确有必要时增加停止/代次相关入口；Tunnel/Backend 的细粒度状态属于 Go，不塞进全局 WorkerState 枚举。

宿主掌握 `(workerId,runId,pid,processStartTime,boundPort)`，统一 runId 在本地与协议中的编码；调用者只对当前实例操作。Bridge 实现通用 handshake/health/invoke/shutdown，握手核验 protocolVersion=1、workerId、runId。准备失败清理本次进程，不影响其他 Worker。

启动前由通用底座生成 runId、端口和专用 token，启动后完成握手；不新增 Bridge 专用 Provisioner/token 仓库。Bridge 独占后端 stdio；退出时子进程回收由 Bridge 主动执行，宿主进程树兜底需平台实测。

### W3：包导入、升级和在线编辑

| 现有文件 | 必要修改 |
|---|---|
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Imports.cs` | Native 包先 staging 校验再替换，入口/架构/hash/执行位；不把下载来的 binary 当文本脚本 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Lifecycle.cs` | 更新前 drain/等待，失败保留可恢复代码版本；不回滚配置/秘密/执行历史 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Code.cs` | Native binary 入口拒绝 GetCode/SaveCode/DebugRun 文本或重复启动路径；其他普通配置文件按已有规则访问，不能越界到 Bridge 状态根 |

Worker 删除仅涉及 `data/workers/mcp-bridge/` 代码包；Bridge 私有状态在另一个根中保留。界面相应说明，不能仍显示“已删除全部插件数据”。若要清理状态/凭据，另设明确范围与授权，不作为普通卸载副作用。

### B1：新增宿主 Bridge 管理模块

| 新文件 | 职责 |
|---|---|
| `src/LinuxWebTool.Contracts/Features/McpBridge/Contracts/McpBridgeModels.cs` | 状态、版本、服务/工具/selection DTO；按 <300 行拆分 |
| `src/LinuxWebTool.Contracts/Features/McpBridge/Contracts/McpBridgeRequests.cs` | secret action、draft mutation、Apply 请求，不以 null/空串混淆删除和保留 |
| `src/LinuxWebTool.Contracts/Features/McpBridge/Contracts/IMcpBridgeOrchestrator.cs` | 宿主用例接口 |
| `src/LinuxWebTool.Application/Features/McpBridge/McpBridgeOrchestrator.cs` | Worker 是否可访问、固定目标、请求编排，不实现 MCP transport |
| `src/LinuxWebTool.Infrastructure/Features/McpBridge/Adapters/McpBridgeControlActions.cs` | 类型化请求到固定 Control action 的轻量映射；消费 IResidentControlClient，不另建 HTTP/生命周期框架 |
| `src/LinuxWebTool.Infrastructure/Features/McpBridge/Adapters/McpBridgeJsonContext.cs` | Infrastructure 请求/响应 AOT JSON 上下文 |
| `src/LinuxWebTool.WebHost/Features/McpBridge/Routes/McpBridgeController.cs` | `/api/McpBridge` 管理员入口，方法/DTO 与计划 API 表同步 |

拟定 IMcpBridgeOrchestrator 按操作定义：`GetStatusAsync(ct)`、`SaveTunnelAsync(request,ct)`、`StartTunnelAsync(ct)`、`StopTunnelAsync(ct)`、`GetServersAsync(ct)`、`SaveServerAsync(id,request,ct)`、`DeleteServerAsync(id,expectedRevision,ct)`、`ProbeAsync(id,ct)`、`DiscoverAsync(id,ct)`、`GetToolsAsync(id,cursor,ct)`、`SaveSelectionAsync(id,request,ct)`、`ApplyAsync(request,ct)`、`GetAuditAsync(cursor,limit,ct)`。慢管理动作返回句柄，补固定查询/取消方法；不把任意 action 字符串作为公开 API。

返回 DTO 分别为 `McpBridgeStatusDto`、`McpBridgeMutationResult`、`McpBridgeOperationResult`、`McpBridgeServersResult`、`McpBridgeProbeResult`、`McpBridgeDiscoveryResult`、`McpBridgeToolsPage`、`McpBridgeApplyResult`、`McpBridgeAuditPage`，统一 `ValueTask<T>`。契约不暴露 HttpContext/HttpResponseMessage/Go SDK 类型。错误码统一映射，禁止把第三方原始 error body 直接当管理页面消息；Control 结果须保留 typed data，不能只返回成功文案。

必须同步修改现有文件：

- `src/LinuxWebTool.WebHost/Composition/ServiceCollectionExtensions.cs`：类型化编排与 Control 映射 DI；复用通用 client，保持 WorkersManager singleton 与 hosted service 同实例。
- `src/LinuxWebTool.WebHost/Composition/AppJsonSerializerContext.cs`：WebHost DTO 注册。
- `src/LinuxWebTool.WebHost/MinimalApi/EndpointsMapper.g.cs`：Controller 注册、方法与路径映射、RequireAuthorization。
- `src/LinuxWebTool.WebHost/Shared/Middleware/ApiKeyMiddleware.cs`：管理员专属路由显式拒绝普通 API Key。

不新增主 SQLite 表、不修改现有内置 McpServerEngine/FrozenDictionary。自身服务可作为普通 HTTP 后端接入；Bridge 配置和工具审批仅在 Go 状态中维护一份权威。

### F1：函数服务内 UI

修改 `src/LinuxWebTool.WebHost/wwwroot/app/views/WorkersView.js`（Native 展示、Bridge 管理入口、删除提示）与 `src/LinuxWebTool.WebHost/wwwroot/app/config.js`（API 常量）。

新增（相对 `src/LinuxWebTool.WebHost/wwwroot/app/`）：

- `components/workers/McpBridgePanel.js`：面板、草稿/active revision、应用操作。
- `components/workers/McpServerEditor.js`：stdio/SSE/HTTP 表单和凭据动作。
- `components/workers/McpToolSelection.js`：搜索、选择/取消、待复核提示。
- `components/workers/McpBridgeStatus.js`：进程/Tunnel/后端分层状态。
- `composables/useMcpBridge.js`：请求、busy/error、刷新/卸载取消和冲突处理。

不新增 router/顶级导航，不在组件中直接 fetch/localStorage，不把后端参数、transport 细节放到 ChatGPT 工具使用流程。

### P1：构建、包与使用说明

新增 `scripts/package-mcp-bridge.ps1`、`scripts/verify-mcp-bridge.ps1`、`.github/workflows/mcp-bridge.yml`；制品仅输出 `artifacts/mcp-bridge/`。worker.json 模板使用 Native=6、Service、AutoRestart=false、AutoStart=false，以及不含秘密的参数/环境配置；打包时产出确切目标平台包，不在运行时选择不可信下载地址。

目前不改 `Directory.Packages.props`、`LinuxWebTool.slnx`、`Dockerfile` 或主应用发布 workflow；Go 模块只由新 CI/脚本构建。`.gitignore` 已排除 artifacts/data，不为此机械追加全局忽略项。NAS 只需已验证的架构二进制与其后端所需 runtime，不需要 Go 编译器。

## 2. 数据契约补充

Go 的 `Revision` 为单调 uint64；无记录从 0 开始，写成功增加 1。`Mutation` 是经过校验的单项草稿动作，`Candidate` 为完整待发布配置、选择、审批和解析后目录；不允许 Commit 接受任意未校验 map。

`ToolPage` 包含稳定顺序 tools、snapshotRevision 和受限 opaque cursor；`ToolResult` 保留支持的原始 JSON content/structuredContent/isError/_meta。`SecretMutation` 必须显式 keep/replace/clear，replace 空值报错。探测结果只含状态、脱敏诊断和目录统计，不含秘密与原始 response body。

`McpBridgeStatusDto` 至少包含 workerRunId、processAlive、adminReady、tunnelState、backendCounts、draftRevision、desiredRevision、activeRevision、recoveryRequired。未配置 Tunnel 的进程仍是 AdminReady，Tunnel 为 Unconfigured，不能出现全绿“ChatGPT 已连接”。

内部使用通用 `POST /control/shutdown`：认证后停止 admission、flush 管理响应，再开始 drain/进程退出；**不**作为公开 `/api/McpBridge` 配置接口或 MCP 工具。status 的 workerRunId 由宿主本次启动注入，客户端必须验证；每次运行 token+runId 双重防止旧端口改投。

审计只保留 timestamp、runId、configRevision、serverId、exportedToolName、耗时、状态/静态错误码、结果大小和 effectUncertain；不记录参数/结果正文。首版日志每行最多 4 KiB、环形缓存最多 1,000 行；持久审计总预算 16 MiB，滚动保留 2 份，每份 8 MiB。秘密、owner 和恢复历史不能随日志轮转删除。

## 3. 测试文件与命令范围

### 新增或扩展的 .NET 测试

| 文件 | 行为 |
|---|---|
| `tests/LinuxWebTool.UnitTests/NativeWorkerAdapterTests.cs` | argv/路径/平台/权限；新契约下语言运行时回归 |
| `tests/LinuxWebTool.UnitTests/ResidentControlClientTests.cs` | readiness、身份、停止顺序、响应限额、认证、超时/端口复用 |
| `tests/LinuxWebTool.UnitTests/WorkersManagerTests.cs`（扩展） | 代次竞争、取消退避、显式 AutoStart、普通 Worker 回归 |
| `tests/LinuxWebTool.UnitTests/McpBridgeOrchestratorTests.cs` | 不可用 Worker、固定目标、错误/版本映射 |
| `tests/LinuxWebTool.UnitTests/McpBridgeControlActionsTests.cs` | 固定 action 映射、typed data、禁止任意目标与管理绕过 |
| `tests/LinuxWebTool.UnitTests/ExtensionsAndWorkersCodeAndZipTests.cs`（扩展） | Native staging、binary 编辑拒绝、状态不随代码导出/回滚 |
| `tests/LinuxWebTool.IntegrationTests/McpBridgeFunctionalTests.cs` | 管理员 API、普通 API Key 拒绝、CRUD/discover/Apply；使用独立临时宿主和 fake Bridge |
| `tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs`（现有门禁） | 新模块路由、目录、分层、AOT/DI 检查；不放宽存量基线 |

新增 `tests/frontend/mcp-bridge-management.test.mjs` 与 `tests/frontend/workers-native-runtime.test.mjs`，基于现有 Vue 模板/VM 测试设施执行事件与请求；不以静态包含某个函数名代替组件行为。

验证由小到大，以下为**未来实施命令**，本次没有执行：

```text
# 工作目录 workers/mcp-bridge
go test ./internal/config ./internal/state -count=1
go test ./internal/backend ./internal/catalog ./internal/gateway -count=1
go test ./internal/tunnel ./internal/runtime ./internal/admin -count=1
go test -race ./...                         # 支持 race 的 CI 平台
go vet ./...

# 仓库根；按任务选择对应 filter，不默认每次全跑
dotnet test tests/LinuxWebTool.UnitTests/LinuxWebTool.UnitTests.csproj --filter "FullyQualifiedName~NativeWorkerAdapterTests|FullyQualifiedName~ResidentControlClientTests|FullyQualifiedName~McpBridge"
dotnet test tests/LinuxWebTool.IntegrationTests/LinuxWebTool.IntegrationTests.csproj --filter "FullyQualifiedName~McpBridgeFunctionalTests"
node --experimental-vm-modules --test tests/frontend/mcp-bridge-management.test.mjs tests/frontend/workers-native-runtime.test.mjs
node src/LinuxWebTool.WebHost/wwwroot/frontend-gate.cjs
dotnet test tests/LinuxWebTool.ArchitectureTests/LinuxWebTool.ArchitectureTests.csproj
```

最终因跨 Worker 生命周期/新管理模块，还需对应 .NET 构建、已有 Workers/ZIP 测试回归和受影响 AOT 检查。执行现有 `scripts/verify-aot.ps1` 前先阅读其参数/生成副作用，按平台选用；不在本文虚构已跑过的命令。外部实连必须与纯 loopback 测试分开标记，缺凭据不能将测试 skip 记为通过。

## 4. 可观察验收矩阵

| 编号 | 场景与动作 | 必须观察到的结果 | 对应任务 |
|---|---|---|---|
| A01 | 安装/启动 Bridge；升级后的 Node/Python/其他语言示例同时运行 | 无 Go 工具链仍可运行预编译包；新 manifest/argv 契约可用，无旧插件 fallback；管理页在 Tunnel 未配置时可用 | T0/T7/T10 |
| A02 | 配置授权 Tunnel ID/Key，启动后在 ChatGPT 发现并调用只读工具 | 真正取得控制面成功响应且实际结果到达 ChatGPT；仅 ready/保存成功不算端到端通过 | T0/T5/T10 |
| A03 | 分别添加 stdio、旧 SSE、Streamable HTTP 第三方 fixtures | 三种均实际完成发现与一次调用；只添加配置/返回空列表不算通过 | T2/T6 |
| A04 | 新服务 Discover；勾选一个工具后 Apply；随后全不选再 Apply | 初次不暴露；只暴露勾选项；全不选后列表为空且手写调用拒绝、后端执行计数不增加 | T3/T4/T6 |
| A05 | ChatGPT 缓存旧名称；取消勾选成功后再调用 | 缓存不形成授权；排队未 dispatch 的调用也拒绝，in-flight 明确标注不可回滚 | T4/T9 |
| A06 | 两后端均提供 search、Search，修改显示名称/长名/冲突名 | 精确稳定命名与路由；不同服务/大小写不互相覆盖 | T3/T4 |
| A07 | 修改 URL/command/凭据或远端 Schema/description | 新 generation/ReviewRequired；旧调用不改投，新工具/新语义不自动获批；仅属性顺序变化不假告警 | T1/T3/T4 |
| A08 | 两管理员页面同时保存/Apply，或进程在提交点中断 | 一方收到 409/冲突；状态为完整版本，draft/desired/active 可区分，无半配置授权 | T1/T6/T8/T9 |
| A09 | 同一主体两个前端 session 调用 stateful stdio；无稳定身份调用 | 不串会话；无身份默认独立调用，不能宣称支持跨调用状态；TTL 不杀活跃调用 | T2/T5 |
| A10 | 配置凭据，检查 UI/DTO/日志/子进程 env/Worker 编辑下载 | 不回显 secret，不传宿主 JWT/Tunnel Key 到后端；Bridge 状态根不进入普通 Worker 代码目录 | T1/T2/T6/T8/T10 |
| A11 | 匿名/普通 AllowWorkers API Key 调用 `/api/McpBridge`；从 MCP 尝试管理 | 管理 API 拒绝；普通 Worker 模块原权限不扩大；MCP 目录没有 admin 工具 | T6/T8 |
| A12 | 401/403、网络断开、后端慢、队列满、超大结果 | AuthFailed/Degraded/超时/配额错误明确；其他后端可用；无无限缓冲、无工具自动重放 | T2/T5 |
| A13 | 重复命令、dispatch 后断连、未知结果后重启 | 与 SDK 实证一致的执行次数；不把 unknown 写成失败无副作用；不确定停止需要恢复核对，不自动重放写操作 | T0/T5/T7 |
| A14 | 停止/重启/热更新 Worker，模拟旧 Exited 回调、PID/端口复用和双启动 | 旧 run 不删除新 run；drain/强杀有明确结果；无孤儿；第二 owner 拒绝；管理请求不改投 | T5/T7/T8/T10 |
| A15 | 新增/禁用/删除第三方远程服务 | 下次 Apply 后立即更新调用策略，不重启 Tunnel；远端实体继续运行，不被误关；必要时提示 ChatGPT 刷新工具 | T3/T6/T9 |
| A16 | Windows 路径/文件锁、Linux 无执行位、amd64/arm64 错包、无 runtime 的 stdio | 清晰失败；旧代码/状态不被覆盖；不要求默认管理员权限、不静默安装程序 | T1/T2/T7/T10 |
| A17 | 桌面/窄屏操作表单、勾选、失败重试、卸载 | 事件真实触发，busy/error 可见；无 secret 浏览器持久化；卸载明确保留私有状态 | T9/T10 |
| A18 | 相同 NAS 的性能/24h稳定性场景 | 有环境和版本证据、进程树总资源与延迟报告；满足下面试验门槛或明确报告未达标 | T11 |
| A19 | 对通用 Control 发空 token、错误 workerId/runId、非回环目标、超大响应、畸形 health | 明确拒绝或 Degraded；旧实例请求不改投；没有第二套 Bridge 生命周期/token 仓库 | T6/T7/T8 |
| A20 | 慢发现超过 Control 短超时，页面轮询后取消/重启 | 快速返回管理任务句柄；任务数/结果有界；取消与失效可见，不谎报跨重启恢复 | T6/T8/T9 |
| A21 | 模拟排队后剩余预算不足、进度持续、dispatch 后超时 | 不超绝对预算继续等待；进度不续期；未知结果不重放；常驻 Worker 可保持健康 | T0/T4/T5 |
| A22 | 本地 stdio 子进程输出协议/日志并派生子进程，正常与强制停止 Bridge | MCP stdout 只被 Go SDK 读取，日志不污染协议；没有双 supervisor；进程树按平台测试可回收 | T2/T7/T10 |
| A23 | 可选 Native 长任务 submit/get/cancel，重启宿主并重复幂等提交 | 先持久原子占位再启动；操作记录可恢复，未结算转 Unknown；不自动重放；不复制 Go 命令引擎 | 可选阶段 |
| A24 | 可选 Bridge 机器主体尝试越权插件/动作、其他 owner operation/产物；普通 CLI 加为 stdio MCP | 越权拒绝，不使用管理员总权限；产物独立授权；无 MCP 协议程序提示需包装，不假装发现成功 | 可选阶段 |

性能试验门槛沿用研究方案：Bridge 单独 idle ≤96 MiB、空闲 CPU 均值 <1 个逻辑核的 1%、本地热路径额外 p95 ≤10 ms、24 小时无持续内存/FD/子进程增长。这里的“额外”必须与同后端直连基线比较；同时报告 stdio 子进程在内的 PSS/cgroup 总量。超过门槛是待定位结果，不可缩小采样或排除后端来伪造达标。

验收记录必须填写操作系统/CPU/内存、SDK 和程序版本、后端清单、命令、执行测试数量、实际结果和残余限制。Synthetic fixture 的通过不能代替真实 ChatGPT 或实际 NAS；本次没有执行 A01–A24。A01–A22 为核心首版，A23–A24 为可选 Native 长任务门面；未实施写未纳入，不能记通过。

可选阶段文件范围：复用 `Plugins/PluginOperationCoordinator.cs`、`Plugins/Storage/PluginOperationStore.cs` 与 PluginOperationsController；用现有 SQLite/Dapper 增加持久仓储及重启结算，新增受限机器调用/Native Tools MCP 门面与 owner/动作/幂等集成测试。确切门面协议、文件划分在该阶段单独收敛，不把浏览器 JWT API 原样暴露给 Go；不在核心阶段预建空实现。

## 5. 不在修改范围内的内容

不修改参考 `webcodex-main`，不整套移植其 Server/Runner；不为本功能重构现有 MCP 引擎或全局 ApiKeyPermissionMatrix；不处理其他未授权问题。旧研究文档保留为证据，新实施以本计划的已选路线和范围为准。

不创建平台 Tunnel/远程仓库，不发送消息，不发布安装包或自动 git add/commit/push。未来只有收到对应执行/目标授权，才进行该外部操作。
