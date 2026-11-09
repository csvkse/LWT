# 原生工具服务化：修改范围、示例与验收标准

日期：2026-11-01。配套[实施计划](native-tool-service-implementation-plan.md)与[插件平台设计](plugin-platform-feasibility-and-design.md)。本文件列的是待实现范围，不表示所列代码、API、示例或测试已经完成。

用户约束：不兼容旧插件；继续支持各语言 runtime，但直接升级单一清单、参数与结果契约。全程内联，不使用子代理。本次只改 docs，保留工作区已有代码修改。

## 1. 当前基线与实施边界

本轮已观察到未提交的 Native=6、Arguments/ArgumentList、Readiness、ExtensionHookRequest/Response、NativeRuntimeAdapter、PluginSecurityGuard 及采集/日志相关修改。这些只能说明存在代码草稿，未逐项验证其注册、参数透传、运行、安全边界和测试。

实施 N0 时以最新源码为准：能复用的直接核验并补齐；不要从本文重新复制一个同名 adapter、再增加另一套 Hook 协议或覆盖并行工作。历史“25 项测试通过”不作为本轮最新实现的通过证据。

范围包含通用原生工具注册/绑定、常驻托管、命令动作、可选管理接口、操作/产物、对应管理 UI 与示例。MCP/Tunnel、PTY/GUI、驱动安装和强 OS 隔离不在本轮产品实施范围。

## 2. 计划修改的现有文件

以下均为仓库相对路径。与插件基础计划重叠的部分只实现一次；未涉及的无关修改保持原状。

| 文件 | 对应工作 |
|---|---|
| `src/LinuxWebTool.Contracts/Features/Workers/Contracts/WorkerModels.cs` | 收敛 Native/argv/Control/Readiness/程序引用；不添加旧参数回退 |
| `src/LinuxWebTool.Contracts/Features/Workers/Contracts/WorkerOperationModels.cs` | 创建/更新/注册 Native 服务请求；输入不能覆盖受信进程规格 |
| `src/LinuxWebTool.Contracts/Features/Services/Contracts/ScriptExtensionModels.cs` | 动作 executor、typed input/result、命令定义引用；统一现有 Hook 草稿，不双轨解析 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/Adapters/IWorkerRuntimeAdapter.cs` | 单一结构化 StartSpec/argv，与动作 Runner 共享底层而非复用错误的重启语义 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/Adapters/WorkerRuntimeAdapters.cs` | 当前 Native 类的完善/实测；若拆成 NativeRuntimeAdapter.cs，必须移除原定义 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.cs` | Native 注册、创建请求持久化、argv、启动错误资源收尾；生命周期拆小 partial |
| `src/LinuxWebTool.Infrastructure/Features/Workers/Supervisor/WorkerStateMachine.cs` | runId/停止意图/退避；一次性命令不能复用此自动重启流程 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/Supervisor/WorkerProcessRingBuffer.cs` | 核验/完善现有字节与单行上限，不重复实现已有草稿 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkerJsonContext.cs` | 新 Worker 字段/DTO 源生成 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Imports.cs` | Native 包入口、平台、程序来源、执行权限、staging 与配额 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Lifecycle.cs` | 升级 drain、固定 package lease、数据与代码分离 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Code.cs` | Native binary 不被文本读取/覆盖或 DebugRun 启动第二个 owner |
| `src/LinuxWebTool.Infrastructure/Features/Services/Adapters/Extensions/ScriptExtensionManager.cs` | 按清单 executor 分发 Hook/Command/HTTP Control，不根据 stdout 猜测 |
| `src/LinuxWebTool.Infrastructure/Features/Services/Adapters/Extensions/ScriptExtensionManager.Repo.cs` | 命令示例包/程序引用的安装校验；不运行扫描中发现的新二进制 |
| `src/LinuxWebTool.Infrastructure/Features/Services/Adapters/Extensions/ScriptExtensionManager.Lifecycle.cs` | 更新时固定在途工具版本、拒绝/结算冲突动作，不回滚业务产物与状态 |
| `src/LinuxWebTool.Infrastructure/Features/Services/Adapters/Extensions/ServiceJsonContext.cs` | 新动作和解析器配置源生成 |
| `src/LinuxWebTool.Infrastructure/Features/Shell/Adapters/ShellExecutor.cs` | 复用/抽取已有受限输出与进程等待；直接原生命令不再转成 bash/cmd 字符串 |
| `src/LinuxWebTool.Infrastructure/Shared/Support/PluginSecurityGuard.cs` | 核验已有 guard；包路径/系统程序授权/操作产物边界分清，不能因同一 helper 名称而视为安全已证明 |
| `src/LinuxWebTool.Infrastructure/Shared/Persistence/DbSetup.cs` | 独立 plugin_operation/产物元数据，不把插件 ID 伪装成 CommandId |
| `src/LinuxWebTool.WebHost/Features/Workers/Routes/WorkersController.cs` | Native 创建/注册与状态，复用现有管理入口 |
| `src/LinuxWebTool.WebHost/Features/Services/Routes/ServicesController.cs` | 原生命令 action accepted/202 与统一结果；不提供任意命令执行 endpoint |
| `src/LinuxWebTool.WebHost/MinimalApi/EndpointsMapper.g.cs` | 新增操作 API 与 Controller 注册、RequireAuthorization，保持方法/路由一致 |
| `src/LinuxWebTool.WebHost/Composition/ServiceCollectionExtensions.cs` | Resolver/Runner/Executor/Coordinator/Store/Control DI，保持 owner 单一 |
| `src/LinuxWebTool.WebHost/Composition/AppJsonSerializerContext.cs` | 新 DTO 全部显式注册 |
| `src/LinuxWebTool.WebHost/Shared/Middleware/ApiKeyMiddleware.cs` | 显式声明新操作路由粗粒度权限；用例再按存储的 kind/id/action 与归属精确判定 |

## 3. 新增组件的文件归属

| 新增文件/目录 | 单一职责 |
|---|---|
| `src/LinuxWebTool.Contracts/Features/Plugins/Contracts/NativeToolModels.cs` | NativeProgramSpec、NativeCommandDefinition、token 绑定、结果/进度/产物配置；必要时按类型责任拆分到 <300 行 |
| `src/LinuxWebTool.Contracts/Features/Plugins/Contracts/PluginOperationModels.cs` | Invocation、Accepted、OperationDto、ActionResult、状态/错误/产物引用 |
| `src/LinuxWebTool.Contracts/Features/Plugins/Contracts/IPluginToolServices.cs` | Resolver/Runner/Executor/Coordinator/Store/Artifact/Control 的 BCL 接口；过大时按接口拆分 |
| `src/LinuxWebTool.Application/Features/Plugins/CommandBindingResolver.cs` | Schema、批准 token 与 typed 参数绑定、配置代次和文件访问规则 |
| `src/LinuxWebTool.Application/Features/Plugins/NativeCommandExecutor.cs` | Runner/进度/结果/产物组合，退出与业务成功判定 |
| `src/LinuxWebTool.Application/Features/Plugins/PluginOperationCoordinator.cs` | 权限、队列、操作状态、幂等、取消、恢复与配额；不持锁等待外部执行 |
| `src/LinuxWebTool.Infrastructure/Features/Plugins/Adapters/NativeProgramResolver.cs` | package/system 真实路径、文件身份与平台解析；不执行任意 --version 来猜可信程序 |
| `src/LinuxWebTool.Infrastructure/Features/Plugins/Adapters/NativeProcessRunner.cs` | 直接 executable/argv/stdin、自有进程树、受限输出和取消；抽取现有实现时只有一个底层 owner |
| `src/LinuxWebTool.Infrastructure/Features/Plugins/Adapters/ResidentControlClient.cs` | loopback HTTP 管理协议、独立 token、handshake/runId、超时/未知结果 |
| `src/LinuxWebTool.Infrastructure/Features/Plugins/Adapters/CommandParsers.cs` | 有限注册的 text/json/artifact 与进度解析器；按职责可拆文件，不接受任意表达式/无限时 regex |
| `src/LinuxWebTool.Infrastructure/Features/Plugins/Storage/PluginOperationStore.cs` | 有限 SQLite 操作/恢复记录与固定绑定 |
| `src/LinuxWebTool.Infrastructure/Features/Plugins/Storage/PluginArtifactStore.cs` | artifactId、受限目录、下载 lease、大小/保留清理 |
| `src/LinuxWebTool.Infrastructure/Features/Plugins/Adapters/PluginJsonContext.cs` | Infrastructure 所需源生成 JSON |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.ProcessLifecycle.cs` | Workers runId/退出事件/停止与 Control 协调，避免扩充现有大文件 |
| `src/LinuxWebTool.WebHost/Features/Plugins/Routes/PluginOperationsController.cs` | 操作查询/取消/产物下载；所有授权由共享用例执行 |

新的 Plugins feature 是内部共享执行能力，不新增侧边栏平台；Workers 和 Extensions 的功能归属不变。Contracts 零第三方依赖、Application 不依赖 Infrastructure、WebHost mapper/权限/JSON 同步；不放宽架构门禁。

### 明确的 API 契约

| API | 行为 |
|---|---|
| 既有 Worker 创建/Start/Stop/Restart/Logs | 按 Native Resident 定义工作；无 control 时不尝试业务命令 |
| `POST /api/Services/Extensions/Action`，executor=command | 校验清单动作并受控 Submit，返回 202 + accepted/operationId/statusUrl；不等待长工具完成 |
| `GET /api/PluginOperations/{operationId}` | 返回固定操作状态/进度/受限结果/产物引用，检查调用归属与当前读取权限 |
| `POST /api/PluginOperations/{operationId}/Cancel` | queued 确认未执行；running 返回实际取消/未知状态，不仅取消浏览器等待 |
| `GET /api/PluginOperations/{operationId}/Artifacts/{artifactId}` | 按 metadata 定位并流式下载，拒绝调用者提供路径；重新鉴权并持有清理 lease |

新 route 的模块检查不能简单设置“AllowServices 或 AllowWorkers 即可访问所有操作”。粗粒度检查之后必须读取操作的真实 plugin kind/id/action/owner 再精确授权；未知 ID 不泄露其他人的任务详情。管理 Control token 不是公共 API 身份。

## 4. 前端文件范围

修改 `src/LinuxWebTool.WebHost/wwwroot/app/config.js`、`views/WorkersView.js`、`components/services/ExtensionsSubmodule.js`、`ExtensionActionModal.js`。新增以下组件（相对 `src/LinuxWebTool.WebHost/wwwroot/app/`）：

- `components/workers/NativeWorkerDeployModal.js`：程序来源、argv/env/端口、可选 Control；不自动导入/运行未知程序。
- `components/services/NativeCommandEditor.js`：Schema、固定参数绑定、结果/进度/产物、期限；常见工具无需生成脚本。
- `components/services/PluginOperationPanel.js`：queued/running/终态、进度、取消、artifact 下载；轮询有间隔、卸载取消、错误可见。
- `composables/usePluginOperations.js`：统一请求/busy/超时/轮询/状态更新，组件不直接 fetch 或访问秘密存储。

保持现有函数/扩展入口，不新建独立导航。UI 必须区分“服务进程运行”“业务就绪”“操作已接收”“业务执行成功”；无接口程序不显示不可用的管理按钮。

## 5. 项目示例升级矩阵

| 示例/源码位置 | 修改与行为验收 |
|---|---|
| `extensions/sample-healthcheck/manifest.json`、`hooks/status.sh`、`hooks/status.ps1` | 新清单+单次 JSON Hook；请求/响应 operationId 一致，诊断在 stderr |
| `extensions/ssh-access-guard/manifest.json` 与其 `hooks/` | 全动作 typed ip/参数、统一结果和权限；真实协议测试，防火墙命令以 stub 替代 |
| `extensions/caddy-server-manager/manifest.json`、`hooks/start.js`、`stop.js`、`restart.js`、`status.js` | Node 正确 interpreter/stdin、外部 owner、结果与错误；不改真实系统服务 |
| `src/LinuxWebTool.Infrastructure/Features/Services/Adapters/Extensions/ScriptExtensionManager.Defaults.cs` | 内置生成的三个包同步上述新契约，不能只升级磁盘样例 |
| `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Defaults.cs`：demo-js-webhook、demo-py-api、demo-dotnet-service | 新清单/启动/配置；至少一个 Service 示例演示明确 health/control，业务与管理鉴权分离 |
| 同文件：demo-sh-daemon、demo-ps-daemon | 无 HTTP 常驻守护、日志、退出/停止约定，证明普通 Worker 仍可托管 |
| 新 `workers/examples/native-resident/manifest.json`、`README.md` | 使用打包后的 fixture 直接托管，含 argv/env，Control 为空；不默认安装缺 binary 的示例 |
| 新 `extensions/native-command-demo/manifest.json`、`README.md` | package binary 的参数/文本/JSON/文件输出动作，测试可配置退出码、进度和取消，不写 shell 包装 |
| 新 `workers/examples/native-managed/manifest.json`、`README.md` | 同一原生 fixture 的 HTTP 管理模式，演示 handshake/health/invoke/shutdown，独立 token |
| 新 `tests/fixtures/native-tool-fixture/main.go`、`go.mod` | Go 标准库 fixture：命令参数、进度、输出、产物、子进程、常驻/HTTP 模式；CI/开发预编译，无业务第三方依赖 |

Native 三个示例通过匹配架构的打包过程提供 executable；不提交秘密/制品、不在 NAS 运行时下载工具链。新示例不默认加入五个内置 Worker 的启动集，避免不需要的常驻内存。程序来源为 system 的真实 FFmpeg/压缩工具仅在用户明确登记后测试；fixture 通过不能被描述成实际工具已经验证。

不设计旧包导入、参数别名、旧结果容错、版本迁移 reader。已有开发数据不因示例源码升级被自动删除/覆盖；重新安装示例是明确的本地操作。

## 6. 测试与构建范围

| 测试文件 | 重点 |
|---|---|
| `tests/LinuxWebTool.UnitTests/NativeWorkerAdapterTests.cs` | 固定目标/平台/argv/env、无编译器启动、启动失败清理 |
| `tests/LinuxWebTool.UnitTests/NativeCommandBindingTests.cs` | typed token、路径/选项/模板拒绝、stdin、权限与配置代次 |
| `tests/LinuxWebTool.UnitTests/NativeCommandExecutorTests.cs` | 退出码、stdout 职责、结果/进度/产物和洪泛 |
| `tests/LinuxWebTool.UnitTests/PluginOperationCoordinatorTests.cs` | 排队/幂等/取消/恢复/不重放、lease 固定目标 |
| `tests/LinuxWebTool.UnitTests/PluginArtifactStoreTests.cs` | 大小、路径/链接、归属、下载/清理竞争 |
| `tests/LinuxWebTool.UnitTests/ResidentControlClientTests.cs` | optional、token、runId、HTTP 发送歧义与 shutdown |
| `tests/LinuxWebTool.UnitTests/WorkersManagerTests.cs`（扩展） | 常驻状态/停止/旧事件/退避与单一 owner |
| `tests/LinuxWebTool.UnitTests/ExtensionsAndWorkersCodeAndZipTests.cs`、`ServicesHubTests.cs`（升级） | 新 Hook/command 契约、示例、包导入/更新，不保留旧接口断言 |
| `tests/LinuxWebTool.IntegrationTests/NativeToolServiceFunctionalTests.cs` | API→真实预编译 fixture→查询/取消/产物→鉴权闭环 |
| `tests/frontend/native-tool-services.test.mjs` | 配置向导、提交/状态/取消、无 Control 按钮、错误和下载行为 |

新增 `scripts/package-native-tool-examples.ps1`、`scripts/verify-native-tool-services.ps1`；产物输出 `artifacts/native-tool-services/`。CI 仅补必要 fixture 构建/测试，不发布、不更改系统服务。复用或扩展现有 workflow 的对应测试步骤；不因本功能重写主 Docker/AOT 发布。

未来执行的验证顺序：契约/binding 单测 → runner/command/operation/control → 真实进程集成 → 前端/架构门禁 → .NET 构建与受影响 AOT → 目标 NAS 资源验证。示例 fixture 可在其目录用 `go build` 构建，然后传给验证脚本；Go toolchain 锁在实际通过的构建配置，NAS 不需安装。

当前没有执行以上新测试。已有代码边开发边变化时，所有测试证据必须与最终工作树对应，不引用历史一次通过替代当前集成。

## 7. 验收矩阵

| 编号 | 操作 | 必须观察的结果 |
|---|---|---|
| NS01 | 启动无 HTTP 的 Native Daemon | 真实进程、argv/env/cwd、日志和停止可观察；Readiness=Unsupported，不伪称业务 Ready |
| NS02 | 启动自带 HTTP 的原生 Service | 端口注入/业务转发正确；管理 token/JWT 不混入业务请求 |
| NS03 | 缺 binary、错平台/架构、无执行权限、缺共享依赖 | 明确启动失败且无资源泄漏，不要求编译器，不默认管理员提权 |
| NS04 | programRef 指向未批准 system 文件、包外/链接；PATH 同名替换 | 拒绝或要求重新批准；调用者不能通过请求改程序、argv/cwd/env |
| NS05 | 一次性命令接收空格/引号/Unicode/布尔/数值/前导选项/@file | 按声明精确编码；危险选项/路径被拒绝；无 shell expansion |
| NS06 | 原始 stdout 是文本/JSON/二进制，而 Hook 输出混日志 | command 按指定适配器解析/存文件；Hook 明确 ProtocolError；普通 Worker 日志不误解析 |
| NS07 | 退出码 1、允许的特殊退出码、0 但坏 JSON/缺产物、100% 后失败 | 按 acceptedExitCodes+结果/产物联合判定；进度不改变终态，失败不假成功 |
| NS08 | 快命令/长命令调用，HTTP 请求断开后查询 | 202/operationId、真实状态和结果可取；断开不自动取消或重复执行 |
| NS09 | queued 取消、running 取消、超时、子进程持续写输出 | 未 dispatch 不执行；已执行准确说明取消/未知副作用，清理自有进程树；不套用 Worker 自动重启 |
| NS10 | 幂等键重复同参/异参、宿主异常重启 | 同参复用原操作、异参冲突；未知/未执行记录明确，不自动重放业务 |
| NS11 | 超量并发/队列/任务数、磁盘满、stdout/stderr 洪泛 | 配额错误可见，缓冲/磁盘有界，不死锁；其他插件不被拖垮 |
| NS12 | 下载产物时猜 ID/跨调用者访问/路径穿越/链接/同时清理 | 精确鉴权、仅受限文件，下载 lease 防清理；不泄露任意宿主文件或无限 Base64 |
| NS13 | Control 为空、握手错误/旧 runId/错 token、端口被复用 | 无 Control 程序仍可托管；声明接口失败不 Ready，不改投其他服务 |
| NS14 | invoke 断连、shutdown 不响应、停止/重启竞争 | 不自动重放写命令；15 秒预算内确认结束或给准确强杀/未知结果；旧事件不污染新 run |
| NS15 | 动作在更新/禁用/删除过程中排队或执行 | 未分发的撤销生效，已执行固定代码代次；状态/secret/产物不随代码回滚，不擅删现有数据 |
| NS16 | 匿名/普通 API Key/MCP/工作流操作调用、取消、产物下载 | 所有入口执行同一 caller+kind/id/action 规则；ID 或模块开关不构成越权授权 |
| NS17 | 三个原有扩展包/生成模板、五个函数模板运行 | 单一新 Schema/协议；各语言实际通过，不遗留旧字段/日志结果/别名 fallback |
| NS18 | 三类 Native 示例包在目标平台安装运行 | 常驻、command、managed 各实际验证；缺制品不能按 skip 宣称完成 |
| NS19 | 桌面/窄屏向导、状态、取消、未知结果、下载 | 事件真实触发、busy/error 可见，状态文案准确；秘密不持久化到浏览器 |
| NS20 | 同 NAS 基线/封装对照、并发 1/4/8 与 24h | 记录宿主增量和全进程树 PSS/cgroup、CPU、FD/进程数、p50/p95；无持续资源增长，未测数据不预填 |

NS20 沿用基础方案的候选目标：缓存列表本地 p95 <100 ms、空闲新增 CPU <1 个逻辑核的 1%、停止在配置预算内结算。一次性命令额外延迟按同工具直连对照报告，不承诺所有 CLI 都满足同一个毫秒值；业务处理时间、编译器、第三方 runtime 和产物存储需单独列出。

## 8. 本次文档验证边界

本次检查文档链接、格式、计划/验收覆盖和既有文件定位，不启动 fixture、不运行工具业务、不写数据库、不修改产品源码、不清理任何现有数据。正式验收需给出实际平台、版本、命令、执行数与结果，不能将本文的计划视为完成记录。
