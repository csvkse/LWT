# Go 单进程 MCP Bridge 实施计划

> 2026-11-01 修订：按[渠道常驻服务复核](chatgpt-channel-service-reassessment.md)对齐通用 Native/Control 底座。直接升级宿主与内置示例，不实现旧插件 reader/adapter、字符串 argv fallback 或迁移分支；保留语言运行时能力。Go 单 Bridge 进程与两项官方 SDK 的选型保持。

> Native 托管前置能力按[原生工具服务化计划](native-tool-service-implementation-plan.md)统一实施，包含常驻、命令动作与可选 Control；本计划消费这些基础，不平行再造 Native adapter/任务协调器。

> 执行方式：使用 `superpowers:executing-plans` 在当前对话逐项内联实施与自审，不使用子代理。本文件是待审阅计划，不是已经实施的功能；当前请求仅授权整理文档，不执行依赖安装、产品编码、部署或 Git 写操作。

**目标：** 复用 LinuxWebTool 函数服务托管一个 Go 常驻插件，通过官方 Tunnel/MCP SDK 向 ChatGPT 暴露用户明确选中的本地及第三方远程 MCP 工具。

**架构：** 一个 Go Bridge 进程嵌入官方 Tunnel Client、前端 MCP Server 和后端 MCP Clients，数据面使用 in-memory transport。现有 Workers 负责根进程，Bridge 独占 stdio 子进程。宿主类型化管理员 API 复用通用 Resident Control client，不重复建设生命周期框架。Native 长任务通过单独服务边界复用宿主操作协调器，不直接调用 C# 对象。

**技术栈：** 现有 .NET 10 / Vue ESM / Workers；Go、`github.com/openai/tunnel-client`、`github.com/modelcontextprotocol/go-sdk`、Go 标准库；不向主应用增加 MCP SDK，不引入 Redis/K8s/额外数据库服务。

**设计依据：** [可行性方案](mcp-tunnel-plugin-feasibility.md)、[基础设施核验](mcp-tunnel-plugin-infrastructure.md)、[修改文件与验收矩阵](mcp-bridge-file-scope-and-acceptance.md)。本计划落实用户已选定的 Go 单进程路线；前文“先比较其他代理”的选型建议不再是实施前置条件。

日期：2026-11-01。目标环境：单机/NAS、一个个人 Tunnel/profile、优先低占用。Linux amd64/arm64 为首版交付平台，Windows amd64 用于开发验证；其他架构由明确需求再扩展。

## 1. 第三方 MCP 管理范围

**支持添加第三方服务并管理其接入。** 不限制服务供应商，但要求实现受支持 MCP transport，并且具备合法访问凭据与可达网络。

| 第三方服务类型 | 首版支持的管理 | 边界 |
|---|---|---|
| 本地 stdio MCP | 配置 executable/argv/cwd、最小环境、测试连接、发现/刷新工具、启停/重连、子进程退出清理 | 管理其本地进程；用户先安装所需程序/解释器，不静默下载执行包 |
| 本地或远程 Streamable HTTP MCP | 配置 URL、静态凭据/Headers、探测、启停接入、刷新工具、调用状态 | “停止”关闭本插件连接并停止暴露，不关闭对方 HTTP 服务器 |
| 旧 HTTP+SSE MCP | 同上；SDK 管理 SSE GET 与 message POST | 不把 SSE GET 当普通 JSON endpoint；校验后续 message 目标 |
| 无需认证 / 静态 API Key / Bearer / 自定义 Header | 只写新增/替换/清除，保存后不回显原值 | 不将 Tunnel Key、宿主 JWT 或管理 token 转发到第三方 |
| 需要 OAuth 的远程 MCP | 首版显示 AuthRequired/不支持的认证方式，不假装接通 | 浏览器登录、刷新令牌与 OAuth 授权代理另做后续功能 |

每项服务都有增删改、显式连接测试、工具搜索与分类勾选、禁用/启用接入、错误和健康状态。第三方自己的部署、版本升级、包市场、容器编排、REST→MCP 转换不属于首版。支持服务接入不表示承诺所有 resources/prompts/UI/sampling 等特性完全透传；首版契约是工具调用及其受支持结果。

管理员可显式配置已经安装的 node/python/npx/uvx 等命令及参数；Bridge 不自动生成安装步骤。npx/uvx 等命令本身可能联网拉包，连接测试前必须提示这一效果并由用户明确选择，不把执行安装包装成无副作用的“测试”。缺少 runtime 时只报告缺项。

## 2. 全局约束与确定值

- 单 Bridge 是单个 Go 进程；stdio 后端仍可以有自己的子进程，不把“单进程”宣传成所有第三方工具都无额外进程。
- Workers 保留 Node/Python/DotNet/Bash/PowerShell/Batch 的语言执行能力，Native=6 已有工作区实现，按新 manifest/结构化 argv 契约核验，不重复新增 adapter 或保留旧插件解析。
- Bridge 固定 Worker ID `mcp-bridge`，`Mode=Service`；独立状态根为 `data/mcp-bridge/mcp-bridge/`，不放进可在线编辑/导出的 Worker 代码目录。
- `worker.json` 不含明文 Tunnel/第三方密钥；控制面凭据只发 OpenAI，后端只得到自己的凭据。所有子进程按批准的最小环境启动。
- 新服务默认关闭、新工具默认未选，`selectedTools=[]` 为拒绝全部；不得沿用现有 ApiKeyPermissionMatrix 的空集全放行语义。
- 导出工具名稳定、大小写敏感、无冲突；产品兼容上限 64 字符。服务 ID 不由可修改显示名称派生，不按字符串拆分猜路由。
- 管理界面保存草稿与应用配置分开。应用成功后，新调用与尚未分发的调用执行最新撤销策略；已分发操作仅尽力取消，不宣称回滚。
- 控制面默认仅官方 SDK 的 OpenAI endpoint；测试通过依赖注入使用本地 stub，不在生产 UI 开放任意控制面 URL。
- 初始限额：全局并发 4、每服务 2、等待队列 16、排队 2 秒、发现 10 秒、普通调用 30 秒、单结果软限 1 MiB；总 deadline 必须扣除已消耗时间并预留回传预算。
- 后端会话总数上限 8、每服务上限 2、空闲 TTL 120 秒；活跃调用不参与空闲回收。stateful 后端默认隔离，不以 Tunnel ID 或共享 Key 推断聊天身份。
- 后端 URL 仅 http/https，拒绝 userinfo/fragment；远端默认 HTTPS，明文 HTTP 仅 loopback 或管理员明确允许的 NAS 私网目标。重定向关闭，DNS 每次新建连接重验目标；云 metadata/link-local/未批准地址拒绝。旧 SSE message URL 默认必须在同一批准 origin，跨 origin 需独立批准，不沿用原认证头。
- 目录初始限额：20 个服务、每服务 1,000 工具、发现最多 100 页、每 Schema 256 KiB、发现总字节 8 MiB、配置 envelope 4 MiB；允许批次部分发现失败，但不发布截断/未经复核的目录。100 个已暴露工具是提醒阈值而非硬平台限额。
- 首版默认不自动重试工具调用。网络重连/健康重试不能造成可能已执行的工具重放；普通日志不包含参数、结果正文、Headers 或凭据。
- Bridge manifest 显式 `AutoRestart=false`，运行期网络重试由 SDK/Bridge 管理。只有通过恢复语义验收后才考虑外层自动重启；语言适配器按新契约回归。
- `AutoStart` 是独立显式意图，安装后默认 false，用户可以开启。未配置 Tunnel 时仍可打开管理页。
- Contracts 只用 BCL；保持 AOT 源生成 JSON、模块目录/命名空间、Controller/mapper 同步及前端 API 统一入口，不扩充门禁基线。

上述容量值为首版实现和验收的明确起点；性能门槛见验收矩阵。未获得硬件数据前，不据这些值宣称 NAS 总内存消耗。

## 3. 关键实现契约

### 3.1 存储与配置版本

Go 源码根：`workers/mcp-bridge/`，本地 module 路径 `linuxwebtool.local/mcp-bridge`。不创建远程仓库。

状态根包含 `bridge-state.json`（配置、选择、审批、加密 secret envelope、revision）、`secrets.key`（独立主密钥）、`owner.lock`、运行/不确定性记录及受限审计。Control token 为宿主按 runId 发放的临时运行凭据，通过受限文件交接，不由 Go 再创建永久 admin.token。第一次初始化采用 exclusive create；已有加密状态却丢失主密钥时阻止启动，不悄悄生成新密钥。主密钥和数据必须配套备份。

宿主负责当前运行 token/runId 和固定状态路径，Go 管理第三方秘密；不把宿主全局 master.key 交给 Go。Unix 私有目录 0700、秘密文件 0600；Windows 创建于既有当前用户私有数据范围，并验证有效访问范围，不能把修改系统 ACL/管理员权限当默认前置条件。当前同 OS 账户可读文件的程序仍不具备强沙箱隔离。

`SaveDraft(expectedRevision, mutation)` 只保存草稿；`Apply(expectedRevision)` 对批准的目录做校验与发布。配置、secret 变更与批准状态写入同一个受限 envelope，避免多文件半提交；原子替换并同步后再发布内存快照。进程崩溃后从持久状态重建，并区分 draftRevision / desiredRevision / activeRevision，不能返回“成功”却仍使用混合版本。

服务端地址、命令或凭据变化生成新的 backendRevision；旧绑定可 drain，不得改投。Schema/描述变化暂停该工具的新暴露，待重新选择确认；仅 JSON 属性顺序等规范化变化不产生虚假更新。

### 3.2 Go 内部接口（产品自有接口，不冒充 SDK 签名）

| 包 | 接口契约 |
|---|---|
| `config` | `State`、`BackendSpec`、`Selection`、`SecretMutation`；`Validate(State) error`；secret action 为 keep/replace/clear |
| `state` | `Store.Read(ctx) (config.State,error)`；`Store.SaveDraft(ctx,expectedRevision,Mutation) (Revision,error)`；`Store.Commit(ctx,expectedRevision,Candidate) (Revision,error)` |
| `backend` | `Factory.Connect(ctx,BackendSpec) (Session,error)`；`Session.ListTools(ctx,cursor) (ToolPage,error)`；`Session.CallTool(ctx,name,args json.RawMessage) (ToolResult,error)`；`Session.Close(ctx) error` |
| `backend` | `Manager.Acquire(ctx,Binding,CallScope) (Lease,error)`；`Lease.Release()`；`CallScope` 只携带已验证的前端 session 或独立调用隔离范围 |
| `catalog` | `BuildCandidate(State,DiscoveredCatalog) (Snapshot,error)`；`Registry.Current() *Snapshot`；`Registry.Publish(*Snapshot)`；含 immutable Binding 与审批指纹 |
| `gateway` | `Gateway.List(ctx,cursor) (ToolPage,error)`；`Gateway.Call(ctx,exportedName,args,CallScope) (ToolResult,error)`；`Gateway.Apply(ctx,expectedRevision) (ApplyResult,error)` |
| `tunnel` | `Controller.Start(ctx,Binding) error`；`Controller.Stop(ctx) error`；`Controller.Status() Status`；生产实现只包官方 SDK |
| `admin` | `Handler` 只管理上述服务；constant-time token 比较、请求/响应大小限制、独立超时，不挂入 MCP Facade |

`ToolResult` 必须保留 supported content、structuredContent、isError 与 _meta，区分业务 tool error 和 JSON-RPC/protocol error；不把所有结果拼接成 text。`Binding` 固定 serverId/backendRevision/导出名/上游名/schemaDigest；Registry 更新不会改变已取得的 Binding。

前端 SDK 使用官方非泛型动态工具入口，不能为每个第三方 Schema 生成 Go 源码。目前官方 Server 提供动态工具注册、移除与 receiving middleware；P0 必须验证固定版本能通过 middleware/发布锁使工具发现与执行都读取一致快照，不依赖逐个 Remove/Add 自带原子性。[官方 Server 源码](https://github.com/modelcontextprotocol/go-sdk/blob/main/mcp/server.go)

### 3.3 会话与恢复

有可信前端 session 时按 session+backendRevision 隔离；无状态入口默认每调用隔离。只有用户明确标记可安全复用的无状态后端才进入连接池；相同授权并不自动允许共享可变进程状态。需要跨调用状态的第三方 MCP，首版明确不承诺跨 ChatGPT 调用连续性，不能用全局共享进程偷偷模拟。

一个 Tunnel 对应一个 owner；重复启动被锁拒绝。P0 明确官方 SDK 在重复命令、响应重试、干净/异常停止下的保证。若没有可验证的跨重启持久去重，首版在异常退出后进入 `RecoveryRequired`，保留记录且不自动恢复写执行；不能宣称 exactly-once，不提供删除状态文件的一键“修复”。恢复需明确核对未知任务，并采用已验证的 SDK 恢复流程；未具备该流程前保持阻止自动重启。

### 3.4 宿主管理 API

宿主入口 `/api/McpBridge`，管理员 JWT 才可用；ApiKeyMiddleware 显式拒绝普通 API Key。Bridge 内部使用通用 Resident Control：`/control/handshake`、`/control/health`、`/control/invoke/{action}`、`/control/shutdown`，仅 loopback、须当前 runId 与专用 token。固定 action allowlist 映射以下类型化 API；不另建 `/admin/v1` 协议，不接受用户提供目标地址。握手核验协议、Worker 和运行代次；通用客户端须先补齐验证与响应限额。

| 方法 / 相对路径 | 用途 |
|---|---|
| `GET /Status` | 进程、Tunnel、目录版本、各后端状态；不返回秘密 |
| `PUT /Tunnel` | 保存 Tunnel ID 与 secret mutation；运行中的绑定需先 drain/restart，不伪称保存即连通 |
| `POST /Tunnel/Start`、`POST /Tunnel/Stop` | 管理 Tunnel，不停止整个 Worker 管理页面 |
| `GET /Servers`、`PUT /Servers/{id}`、`DELETE /Servers/{id}` | 服务草稿列表、更新/新增、删除草稿 |
| `POST /Servers/{id}/Probe`、`POST /Servers/{id}/Discover` | 显式测试/发现；不自动启用或执行业务工具 |
| `GET /Servers/{id}/Tools`、`PUT /Servers/{id}/Selection` | 工具目录、显式选中集和复核指纹 |
| `POST /Apply` | 携带 expectedRevision 原子应用变更；启用/禁用属于服务草稿 enabled 字段 |
| `GET /Audit` | 有界、分页、脱敏事件 |

补充语义：不存在返回 404、输入不合法 400、版本冲突 409、配额满 429、未运行/不可用 503；远端业务错误与这些管理错误分开。页面明确区分“保存草稿”“应用成功”“连接成功”“ChatGPT 实际调用已验证”。

控制动作映射为 `status.get`、`tunnel.save/start/stop`、`servers.list/save/delete/probe/discover`、`tools.list/select`、`config.apply`、`audit.list`。慢发现返回短期管理任务句柄，增加固定查询/取消 action；不用长时间占用通用 Control 请求，不宣称该句柄跨重启存续。Native 命令的 operationId 另属宿主任务系统。

### 3.5 长时运行与可选原生任务

常驻是进程生命周期，不取消 Tunnel 单请求的绝对截止时间。普通第三方 tools/call 保留原同步语义，在剩余预算内结束；超时后结果未知时不默认重放。进度不延长平台预算，不能把所有 timeout 关掉。

原生长任务采用显式 submit/get/cancel 与产物引用；另设受限 Native Tools MCP/机器调用适配器，让 Go 经 HTTP/MCP 边界复用宿主 IPluginOperationCoordinator。当前 PluginOperationStore 是内存字典，先落实持久化、原子幂等占位、owner/动作授权与重启 Unknown，才承诺可恢复查询。此可选阶段不阻塞首版三 transport/过滤，不复制 Go 命令执行引擎，不假定远端 MCP Tasks 已端到端支持。

## 4. 审查重点

1. **全部取消勾选后仍放行**：T3/T4 必须有空集、手写隐藏工具名、旧缓存调用三个行为测试。
2. **服务同名或改 URL 后调用到错误目标**：T3/T4 测大小写、namespace 碰撞、地址替换与排队调用竞争。
3. **不同聊天串用 stateful stdio**：T2/T5 测两会话/无前端身份、并发隔离、lease 回收，不能只测单个 echo。
4. **秘密经 Worker 编辑器、日志或后端环境泄露**：T1/T6/T8 测配置返回、错误正文、stderr、子进程 env、普通 Worker 文件访问范围。
5. **停止/热更新/断网导致重复执行或孤儿进程**：T5/T7/T10 测 dispatch 后断连、宿主停止、异常退出、两个 owner、状态恢复。

以下任务均内联执行；每项先补可观察行为测试、确认按预期失败，再写最小实现并运行对应验证。生成文件/清单等低风险机械项不制造无意义失败测试。任务末尾记录差异与检查结果，不自动执行 git add/commit/push。

## 5. 任务拆分

### T0：锁定 SDK 与完成单进程兼容性验证

**文件：** 新建 `workers/mcp-bridge/go.mod`、`go.sum`、`SDK-COMPATIBILITY.md`、`internal/tunnel/sdk_contract_test.go`、`internal/testkit/mcp_fixture.go`、`internal/testkit/controlplane_fixture.go`。

**输入/输出：** 读取已选路线；产出可重复构建的固定 SDK 版本、所需 Go toolchain、协议支持矩阵与单进程实证。构建工具在开发/CI 环境安装，不要求 NAS 安装 Go。

- [ ] 选择官方正式 tag，查其 go.mod/toolchain 要求，锁入 go.mod/go.sum；记录 tag/commit/许可证，不使用 main/latest 或自行维护 Tunnel wire 协议。
- [ ] 建立全 loopback/synthetic credential 的 fixtures，先测 Tunnel SDK → in-memory MCP → tools/list/call 和结果回传；模拟 401/403、重复 request、回传失败。
- [ ] 验证动态 Schema、list/call middleware、目录更新与通知、旧/现代协议会话关联，以及 SDK shutdown 是否能观测真实结束。记录不能保证的行为。
- [ ] 运行 `go test ./internal/tunnel -run TestSDKContract -count=1`，确认执行计数与测试结果，不能以进程启动代替通过。
- [ ] 若单进程关键链路不可成立，先报告具体 SDK 障碍；不私自换成双进程或重开语言选型。实际 ChatGPT 凭据不用于自动测试。

### T1：配置、秘密存储与所有权

**文件：** 新建 `internal/config/{model,validate}.go`、`internal/state/{store,secrets,owner}.go` 及各自 `_test.go`。

**接口：** 产出第 3.2 节 config/state 契约，owner 记录包含进程身份与 runId，不仅比较 PID。

- [ ] 测 revision 冲突、不完整 JSON、路径逃逸、空 selection、只写 secret keep/replace/clear、首次创建与丢失密钥、重复 owner。
- [ ] 实现私有存储、AES-GCM secret envelope、原子提交、配置大小上限和 owner 锁；现有状态不可悄悄覆盖/重置。
- [ ] 测模拟中断后只能看到旧完整状态或新完整状态、不可读秘密时 fail closed、stdout/DTO 不出现 synthetic secret。
- [ ] 运行 `go test ./internal/config ./internal/state -count=1`；只用临时目录与假密钥。

### T2：三种第三方 MCP transport 适配与连接管理

**文件：** 新建 `internal/backend/{session,stdio,http,sse,manager}.go`、`*_test.go`、`internal/testkit/stdiofixture/main.go`。

**输入/输出：** 消费 BackendSpec；产出 Factory/Session/Manager 契约，连接失败返回明确状态而非空工具“成功”。

- [ ] 分别用实际 stdio 子进程、旧 SSE fixture、Streamable HTTP fixture 验证 list/call、分页、错误、超时、取消、认证头、structuredContent。
- [ ] 用官方 SDK adapters 实现 transport；显式传输类型，stdio command 与 argv 分开，禁止拼 shell；stdout 只由 MCP client 消费。
- [ ] 实现 URL/redirect/SSE message endpoint 目标校验、每后端凭据绑定、子进程最小 env、可执行文件/解释器缺失反馈；限制分页/Schema/结果大小。
- [ ] 测两个调用隔离、静态 Header 不串服务、超时不重放、stderr 不阻塞、退出/Close 无孤儿。
- [ ] 运行 `go test ./internal/backend -count=1`；测试必须证明三种 transport 都实际执行过。

### T3：工具目录、勾选策略与原子快照

**文件：** 新建 `internal/catalog/{snapshot,policy,names}.go` 及 `_test.go`。

**输入/输出：** 消费 config.State/DiscoveredCatalog；产出 Snapshot/Binding，供 T4 路由，不向后续任务暴露可变 map。

- [ ] 先测新工具默认拒绝、空集拒绝、服务 disabled、工具同名/大小写差异、超长名、重命名显示名称不改服务 ID、Schema 变更复核。
- [ ] 实现确定性排序、规范化指纹、持久稳定名称映射、draft/active 区分和有界目录；失败发现不覆盖上次批准指纹。
- [ ] 测 BuildCandidate 不修改当前快照，Publish 对观察者只呈现完整版本；旧目录 cursor 不跨版本拼页，返回明确重试发现错误。
- [ ] 运行 `go test ./internal/catalog -count=1`。

### T4：前端 MCP Facade 与执行授权

**文件：** 新建 `internal/gateway/{gateway,facade,admission}.go` 及 `_test.go`。

**接口：** 消费 Registry/Manager/Store；产出 Gateway.List/Call/Apply，供 Tunnel 与管理 API 共用。

- [ ] 测 tools/list 只有已批准工具；直接 tools/call 隐藏名称、已撤销名称、旧 backendRevision 均被拒绝，且 fixture 执行计数为零。
- [ ] 实现动态非泛型工具/Schema、精确 Binding 路由、发布事务与执行前检查，SDK list/call 与目录快照一致；不宣告未支持能力。
- [ ] 在已取得并发配额后、真正 dispatch 前重查撤销与 binding generation，定义该点为 admission 线性化点；之后视为 in-flight，撤销不承诺回滚。
- [ ] 测并发 Apply 与 queued call、Schema 失效、同名两后端均正确路由；保留结果类型/_meta 与协议错误，不降为 text。
- [ ] 运行 `go test ./internal/gateway -count=1`。

### T5：Tunnel 生命周期、限流和不确定执行

**文件：** 新建 `internal/tunnel/{controller,recovery}.go`、`internal/runtime/{runtime,health}.go` 及 `_test.go`。

**接口：** 消费 Gateway 与 Store；产出 Controller、Runtime.Run(ctx) error、Runtime.Stop(ctx) error 及 HealthStatus。

- [ ] 测网络退避可恢复、401/403 进入 AuthFailed、同一 owner 禁止重复 poll、配置变更不改变已分发目标。
- [ ] 包装 SDK，接入并发/队列/deadline 限制；健康分别表示 WorkerAlive、AdminReady、TunnelReady、BackendsReady/PartialReady。
- [ ] 测 30 秒普通调用预算、2 秒排队、取消、dispatch 后断连未知态、相同命令执行计数；根据 T0 结果落实跨重启保护，不补写不存在的 exactly-once 承诺。
- [ ] 停止先关 admission/poll，再 drain 与等待 SDK/后端结束；保留不确定状态，异常重启进入 RecoveryRequired；日志仅保留元数据。
- [ ] 运行 `go test ./internal/tunnel ./internal/runtime -count=1`；支持平台再运行 `go test -race ./...`。

### T6：Bridge 管理接口与可运行入口

**文件：** 新建 `internal/admin/{handler,dto,audit}.go`、`handler_test.go`、`cmd/mcp-bridge/main.go`。

**输入/输出：** 消费 Gateway、Controller 与 Store；产出通用 `/control/*` 和分层健康，CLI 为 `serve --state-dir ... --control-token-file ... --run-id ... --listen 127.0.0.1:<PORT>`。runId 不含秘密，token 文件受限且不进入包。

- [ ] 测匿名/错误 token 拒绝、header/body 限额、revision 冲突、秘密不回显、未配置 Tunnel 时管理 API 仍可用。
- [ ] 实现第 3.4 节操作及错误映射；拒绝把管理 API 注册为 MCP 工具，限制监听地址为 numeric loopback。
- [ ] 测握手协议/workerId/runId，旧 token 与跨代请求拒绝；发现的短期管理任务有界并可轮询，不冒充宿主长期 operation。
- [ ] 测新建第三方服务→Probe/Discover→选工具→Apply→禁用→Apply→调用拒绝完整行为；未点 Apply 不谎报已生效。
- [ ] 运行 `go test ./internal/admin ./cmd/mcp-bridge -count=1`，构建 `go build ./cmd/mcp-bridge`；只用于本地测试，不启动真实 Tunnel。

### T7：消费通用 Native/Control 托管与优雅停止

**文件：** 范围见配套文档 W1–W3；核验已有 WorkerRuntimeAdapters.cs 中 NativeRuntimeAdapter 和 Plugins/Adapters/ResidentControlClient.cs，不新建第二套同名适配器、Bridge 专用生命周期 hook 或通用协调器。

**接口：** 消费通用 Native 与 ResidentControlSpec/IResidentControlClient，宿主生成可信运行身份并交接；如果尚未接通，由通用底座任务完成，Bridge 只提供声明和协议实现。

- [ ] 用新 manifest 升级语言运行时和项目内置示例；Native 含空格/Unicode 路径、argv 原样、二进制不存在/架构不符/无执行权限均明确失败；不测旧插件格式兼容。
- [ ] 核验 Native=6、单一结构化 ArgumentList 与显式 AutoStart，不保留字符串 argv fallback；不需要 go/rust 编译器。
- [ ] Native 的通用 runtime 检查不查找 go/rust 编译器；二进制存在性/架构/执行权限必须逐 Worker 检查，不共用 runtime probe cache。BuildStartSpec 校验失败也进入受控错误结果，不能抛出到整个宿主启动循环。
- [ ] 通用 Control 托管负责固定路径/当前 token/身份握手与认证 shutdown；等待总预算 15 秒，Bridge drain 10 秒，超时回收受管进程树并标记不确定，不在响应送出前杀自身。
- [ ] 加固通用客户端：目标只来自受信当前实例；拒绝空 token/非回环目标；明确健康解析失败、响应上限与错误脱敏；统一宿主 long runId 与协议 string 的编码。
- [ ] 扩展已有 tests/fixtures/native-tool-fixture，先统一 fixture 的 `/control/invoke` 与客户端 `/control/invoke/{action}` 路由契约，再测真实 HTTP 请求，不能仅各自 mock 通过。
- [ ] 用运行代次/runId 防止旧 Exited/延迟检查清除新进程；停止意图先置位再结束进程，取消退避任务；普通 Worker 回归仍通过。
- [ ] 运行 NativeWorkerAdapterTests、ResidentControlClientTests、WorkersManagerTests 与生命周期集成测试；覆盖 AutoStart/disabled/AutoRestart、stdio 所有权及孤儿回收，不以 DI 注册代替验证。

### T8：宿主 BFF 与管理员权限

**文件：** 新增 McpBridge Contracts、Application Orchestrator、轻量 Control action 映射器和 WebHost Controller；消费通用 ResidentControlClient；修改 DI、JSON context、ApiKeyMiddleware、mapper；路径见 B1。

**接口：** `IMcpBridgeOrchestrator` 用类型化 DTO 表达第 3.4 节操作；轻量映射器调用 IResidentControlClient 的固定 action。可信 Worker 运行身份由通用底座提供；不新增独立 HTTP 生命周期或任意目标代理。

- [ ] 使用 fake admin endpoint 测状态/草稿/Apply/超时映射，普通 API Key 与未登录拒绝，管理员可用；不泄露宿主 JWT 或其他 secret。
- [ ] 实现 Controller→Application→固定 action 映射→通用 Control；消费底座的路径/token/runId，不复制 Bridge 业务配置到主 SQLite，不新建专用 token 仓库。
- [ ] 注册 DTO 源生成与 Controller/mapper；显式拒绝 `/api/McpBridge` 的普通 API Key，不能靠默认拒绝漏掉门禁声明。
- [ ] 测 Worker 停止返回可理解的 503，禁用时不得通过旧端口访问其他服务；校验 runId 和健康握手身份，PID/端口复用不改投。
- [ ] 运行 McpBridgeOrchestratorTests、McpBridgeControlActionsTests、McpBridgeFunctionalTests 和对应架构门禁。

### T9：函数服务内第三方 MCP 管理页面

**文件：** 新增 `McpBridgePanel.js`、`McpServerEditor.js`、`McpToolSelection.js`、`McpBridgeStatus.js`、`composables/useMcpBridge.js`；修改 WorkersView.js/config.js；见 F1。

**接口：** 只使用 api/client.js 的 http；endpoint 全放 config.js，复用现有 toast、确认对话和 Worker 刷新入口。不增加顶级导航或独立项目。

- [ ] 写模板/组件行为测试：切换三种 transport 表单、只写 secret、全选/全不选、刷新目录不选中新工具、保存草稿与应用反馈、状态未就绪。
- [ ] 在 `mcp-bridge` Worker 卡片提供“管理 MCP”入口，展示服务 CRUD、连接测试/发现、工具勾选、凭据动作、Tunnel 状态和审计；所有异步控件有 busy/error。
- [ ] 本地服务操作写清“停止子进程”，远端写清“停止接入”；OAuth 提示首版未支持，不提供假登录按钮。危险/通用执行工具标注授权范围。
- [ ] 测 409 冲突保留用户草稿并提示刷新；无任何 secret 值进入 localStorage、日志、返回表单或模板 HTML。
- [ ] 跑聚焦前端测试与 frontend-gate；桌面/窄屏实际检查表单、工具表、按钮和错误提示；不能只匹配源码字符串。

### T10：打包、安装、升级与集成验收

**文件：** 新建 worker.json 模板、受限默认 bridge 配置模板、README、SDK 兼容记录、打包/验收脚本及 Go CI；补充 imports/lifecycle/code 的 Native 包处理和测试，见 P1。

**输入/输出：** 消费 Go 二进制与宿主 Native 支持；产出 Linux amd64/arm64、Windows amd64 的独立 Worker 包及 SHA256SUMS，不提交运行秘密或编译产物。

- [ ] 测 ZIP 暂存校验后再安装：路径/链接/大小/架构/入口一致性、校验和、Unix 执行权限；失败不覆盖现有运行版本。SHA256 只证明完整性，不当作发布者身份认证。
- [ ] Native 入口不能被在线文本编辑器当源码读写；DebugRun 不另起第二个 Bridge owner。按新契约验证普通脚本编辑，不增加旧插件兼容逻辑。
- [ ] 保留 `data/mcp-bridge/` 的配置、secret 和不确定历史；Worker 卸载只移除代码包，清理秘密/状态属于后续明确操作，不默认递归删除。
- [ ] 跑真实二进制的宿主→Bridge→三后端全链路、取消/断网/重启/端口复用；实际 ChatGPT 验收作为显式人工外部步骤，仅用有授权的 Tunnel/只读工具。
- [ ] 补 Go 测试/构建 CI（不发布、不触发部署）；打包不替换主应用 Docker/AOT 发布流程，不因本功能重写现有发布体系。

### T11：资源、回归与交付记录

**文件：** 新建 `workers/mcp-bridge/ACCEPTANCE.md`，更新使用说明与实际验证记录；不预填通过结果。

- [ ] 在同一设备测 Bridge 本体和进程树总量：idle 15 分钟、三服务/100 工具、并发 1/4/8、异常连接、24 小时稳定负载。
- [ ] 采集 RSS/PSS 或 cgroup memory、CPU、p50/p95、FD/子进程、热路径额外耗时；资源门槛不满足时先定位后端/缓冲/会话，不直接换语言。
- [ ] 按配套 A01–A24 验收并填写平台、版本、命令、执行数、结果、未覆盖项；A23/A24 属可选 Native 长任务阶段，未做明确标记；ChatGPT 刷新和实际只读调用分别取证。
- [ ] 先最小相关测试，再做该跨模块功能所需的构建、架构/前端门禁和最终 AOT 验证；无关失败单独记录，不擅自修复。
- [ ] 检查最终差异与 Git 状态；不自动提交、推送或部署。交付可复现安装包、计划对应的证据和真实限制。

## 6. 执行顺序与停止条件

顺序：T0 与通用底座 T7 缺口核验 → T1 → T2 → T3 → T4 → T5 → T6 → T7 真实二进制联调 → T8 → T9 → T10 → T11。执行顺序均内联；T7 的代码存在但未验收不能标记前置完成。可选 Native 长任务门面在宿主持久化和机器动作授权验证后单列实现，不加入核心十二项的隐性范围。

硬停止条件：官方 SDK 无法支持选定单进程协议链路；无法保证撤销生效/目标不改投；实际需要跨调用 stateful 语义却没有可信身份；未知执行被自动重放；凭据泄露；Native 包无法确定执行目标。出现时报告证据和最小可行调整，不通过弱化测试或静默扩范围绕过。

首版不顺带实现 OAuth、包市场、全协议代理、分布式扩容或团队多租户。若第三方清单确实依赖这些能力，先将缺口单独列入下一阶段，不把服务添加成功标为功能全部可用。

## 7. 当前文档交付验证

本次只核对工作树入口、现有测试/门禁、官方 SDK 动态注册接口与文档完整性；上述勾选项、构建、fixtures、测试、安装包和性能验收均未执行。详细文件清单与可观察验收条件见[配套矩阵](mcp-bridge-file-scope-and-acceptance.md)。
