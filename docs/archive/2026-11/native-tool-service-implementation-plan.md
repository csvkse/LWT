# 原生工具服务化实施计划

> 执行方式：使用 `superpowers:executing-plans` 在当前对话内联实施与自审，不使用子代理。本文件是实施前计划；当前请求只修改 docs，不实施产品代码、不安装依赖或发布包。

**目标：** 让函数服务直接托管常驻原生程序，让扩展动作直接包装一次性命令，并为需要主动控制的常驻程序提供可选管理接口。

**架构：** 保持 .NET 宿主，复用 Workers、Extensions、现有认证/JSON/日志/存储设施。Native 启动、Native Command 动作与 HTTP Control 是三个独立职责，汇入统一权限、执行结果、进程所有权与配额，不建设第三个插件平台。

**技术栈：** .NET 10、Process/ArgumentList、HttpClient、System.Text.Json 源生成、Channels/SemaphoreSlim、现有 SQLite/Dapper；测试使用预编译原生 fixture。框架/库选择沿用[调研比较](plugin-platform-libraries-and-industry.md)，不默认新增 RPC 或 Actor 框架。

**设计依据：** [插件平台设计](plugin-platform-feasibility-and-design.md)、[当前审计](plugin-platform-current-state-audit.md)、[文件范围与验收矩阵](native-tool-service-scope-and-acceptance.md)。本计划补充原生工具服务化，Go MCP Bridge 仍是未来使用者，不在本计划中实现 Tunnel。

日期：2026-11-01。已确认约束：单机/NAS、低占用、未正式发布、不兼容旧插件，同步升级项目示例。

## 1. 首版范围与边界

| 入口 | 配置后能做什么 | 不自动获得的能力 |
|---|---|---|
| Native Resident | 直接启动已注册二进制，传 argv/env/端口，观察日志/退出，停止/重启/守护 | 任意业务命令、数据库健康、优雅停止协议 |
| Native Command Action | 输入 Schema → 固定 argv/stdin 绑定 → 执行工具 → 结果/进度/产物适配 → operationId | 自动理解任意工具的 CLI 语义、交互式提示、exactly-once |
| Managed Resident | 在前者基础上启用 handshake/health/invoke/shutdown HTTP 契约 | 无接口程序不会被自动变成可控制程序 |

普通 REST/业务 HTTP 转发与管理通道分开。自带 HTTP 的原生工具可直接经现有 Service 入口访问，其业务鉴权仍需配置；不能默认把宿主 JWT、API Key 或控制 token 转给业务端点。

不包含：GUI/桌面自动化、PTY、任意驱动/设备权限、工具市场/自动下载安装、进程内 DLL 加载、强 OS 沙箱或多人分布式调度。带 `requiresTty=true` 的定义明确拒绝；没有可用交互通道的工具应在期限内失败，不无限等待输入。

## 2. 全局实施约束

- 只有一套清单与调用契约。删除旧参数串/别名/混合 stdout 降级设计；不新增 legacy adapter、旧目录读取或迁移代码。继续支持现有语言不等于兼容旧插件格式。
- 保留当前工作区已有代码修改。实施前检查最新差异与测试，Native=6、Arguments/ArgumentList、ExtensionHookRequest/Response、安全 guard 或有界采集若已存在，应验证/完善，不重复创建竞争实现。
- `execution.model=resident` 归 Workers；`actions[].executor=command|hook|http-control` 是确定的动作执行方式，不靠探测 stdout 选择路径。
- 程序来源为 package 或 system：前者位于当前包批准目录；后者是管理员登记的绝对物理路径。调用请求不能指定 executable、任意 argv/cwd/env，PATH 搜索不能悄悄改投同名程序。
- 全部启动使用 executable + ArgumentList，不拼接 shell 命令。输入绑定按 token 进行；路径参数还需防选项/@response-file 注入，只有目标工具明确支持时才插入 `--`。
- 常驻实例使用 runId，动作使用 operationId，代码使用 packageGeneration，配置使用 revision；端口/PID 都不能代替身份。旧回调、更新和 queued action 不改投目标。
- 每插件状态变更串行；不得持锁等待进程、HTTP 或 drain。停止意图先于 kill，待重启任务可取消。一次性命令没有 AutoRestart 语义。
- 默认动作期限 30 秒，可在宿主许可内显式放宽，初始上限 86,400 秒；排队最多 2 秒、全局并发 4、每插件写动作串行、等待队列 16。不要用短 HTTP 请求寿命决定长工具任务寿命。
- Hook 必须输出协议 JSON；原生命令不要求实现该协议；普通 Worker stdout 只是日志。命令 acceptedExitCodes 默认 `[0]`，非默认值须在动作定义中明确。
- stdout/stderr 在采集阶段有界。协议/结构化结果默认上限 256 KiB，日志单行 4 KiB/每实例缓存 1 MiB，仍排空管道；超大 JSON 报错，不能截断后伪解析。
- 进度与成功判定分开；100% 不等于成功。结果解析/产物校验失败即失败，即使工具退出 0；超时或取消可能留有副作用，不自动重试。
- 产物默认仅在宿主分配的 operation 目录生成，以 artifactId 下载。下载重新检查调用者/插件/操作权限，拒绝目录穿越、链接和任意本机路径。
- 无秘密进入清单、日志、命令行、返回 DTO 或普通示例文件；使用 secretRef 与最小子进程环境。普通 argv 不用于承载密码。
- 不修改源代码来满足本次文档任务，不执行 git add/commit/push，不自动覆盖开发机已存在示例数据。

## 3. 数据流与统一契约

```text
常驻：Worker 定义 → 批准的 ProgramSpec → Native StartSpec → 进程 owner/runId
                                     ↳ 日志/端口/退出/可选 health

一次性：动作请求 → 权限/Schema/配额 → 记录 operationId → 队列
       → 固定 program/argv/stdin → 原生进程 → 进度/退出/结果/产物
       → 查询/取消/下载 API

管理：宿主用例 → 固定 loopback + 每实例 token + runId
       → handshake / health / invoke / drain/shutdown
```

建议新增/收敛为以下 BCL 契约，名称是产品接口，不是第三方 SDK API：

| 类型/接口 | 内容或签名 |
|---|---|
| `NativeProgramSpec` | source、programRef、packageGeneration、受信 executable、结构化 argv、cwd、最小 env、stdinMode、targetPlatform；仅宿主解析后产生 |
| `NativeCommandDefinition` | commandId、programRef、InputSchema、ArgumentBindings、StdinBinding、AcceptedExitCodes、ResultParser、ProgressParser、ArtifactRules、TimeoutSeconds、Mutates |
| `CommandInvocation` | pluginIdentity、action、typed JsonElement input、expectedRevision、可选 IdempotencyKey；不接受原始 RunSpec |
| `ICommandBindingResolver.ResolveAsync(definition,invocation,ct)` | `ValueTask<NativeProgramSpec>`，绑定程序、路径和参数，Schema 不通过不启动进程 |
| `INativeProcessRunner.RunAsync(spec,observer,ct)` | `Task<NativeProcessResult>`，结果含 started/exitCode/timedOut/cancelled/effectUncertain 与受限输出引用 |
| `INativeCommandExecutor.ExecuteAsync(context,definition,invocation,ct)` | `Task<PluginActionResult>`，组合 runner/结果适配，供操作协调器调用 |
| `IPluginOperationCoordinator.SubmitAsync(invocation,ct)` | `ValueTask<OperationAccepted>`，提交成功即返回服务生成的 operationId |
| `GetAsync(operationId,caller,ct)` / `CancelAsync(operationId,caller,ct)` | `ValueTask<PluginOperationDto>`；必须检查调用归属，不能仅凭 ID 授权 |
| `IResidentControlClient.HandshakeAsync/HealthAsync/InvokeAsync/ShutdownAsync` | 消费固定实例绑定，不消费浏览器传入的任意 target URL |

`PluginActionResult` 区分 accepted/succeeded/failed/cancelled/timedOut/unknown，保留结构化 data、静态 errorCode、可安全展示的 message 和 artifactRefs。Hook 的现有响应草稿要收敛到这一语义；不保留两套相互矛盾的 success bool / status 优先级。

### 参数与结果约定

一次性命令清单绑定示意（不是当前已可导入的 manifest）：

```json
{
  "executor": "command",
  "programRef": "approved-encoder",
  "arguments": [
    {"literal":"-i"},
    {"inputPath":"source"},
    {"literal":"-progress"},
    {"literal":"pipe:1"},
    {"artifactPath":"output.mp4"}
  ],
  "stdinMode": "closed",
  "acceptedExitCodes": [0],
  "progressParser": "ffmpeg-key-value",
  "resultParser": "artifacts",
  "timeoutSeconds": 3600
}
```

模板由 literal/inputScalar/inputPath/artifactPath 等已知 token 构成，不执行表达式、shell expansion、任意反射或调用者模板。布尔/数值按固定文化格式编码；可选参数由显式条件组表示，不靠空字符串猜含义。声明 allowlist 参数范围，不允许请求任意附加 `-exec` 等工具选项。

结果解析首版提供 bounded-text、json、artifacts；进度提供 none、json-lines、ffmpeg-key-value。复杂业务可编写统一 JSON Hook，不能在主宿主中不断添加工具名称特判。工具输出为二进制时写受限文件，不强行 UTF-8 解码或塞入 Base64 响应。

### 操作、恢复与取消

操作状态为 queued → starting → running → succeeded/failed/cancelled/timedOut/unknown。提交不能被当作完成；同一幂等键只在相同调用者、插件、动作、配置代次与输入摘要内复用，参数不同返回冲突。

SQLite 操作记录独立于现有 command/workflow ID，包含调用归属、固定代码代次、期限、状态、输出引用和 effectUncertain。启动时未确认的 starting/running 标记 unknown；未执行的 queued 标记 interrupted/notExecuted；均不自动再次执行业务。未知操作记录不能被普通 TTL 清理当成已结算，不能仅凭 PID 消失或产物存在推断成功。

初始保留建议：已结算记录 7 天/最多 10,000 项，产物总预算 512 MiB、单项 256 MiB、保留 24 小时；配额耗尽明确拒绝新产物/任务，不删除运行中、被下载或仍未知的记录。清理仅作用于确认属于对应 operation 的目录，不递归删除用户指定输出目录。预算是待测默认值，不是已测资源保证。

取消 queued 操作保证不启动进程；running 操作停止 admission 并按支持能力协作关闭/清理自有进程树。只取消 HTTP 等待不等于取消任务；响应与 UI 区分请求取消、执行取消和效果未知。父任务停止时对自有子进程收尾，不能停止不归宿主所有的外部系统服务。

## 4. 审查重点

1. 参数被当成 shell/工具选项：N2/N3 对空格、Unicode、引号、前导 `-`、`@file` 与恶意模板做行为测试。
2. 一次性失败被常驻守护器重跑：N4/N8 测默认不重试、取消/崩溃恢复与幂等键冲突。
3. 原始工具 stdout 被误当 Hook JSON：N3/N7 测三种输出职责，退出码和结果/产物双重判定。
4. 管理端口或程序路径复用导致改投：N1/N5 测 runId、binding revision、实例 token 和已批准程序身份。
5. 产物/输出/资源越界：N4/N6/N8 测任意下载路径、链接、输出洪泛、过期清理与在途文件 lease。

## 5. 实施任务

全部任务按内联方式执行。每个行为变更先写最小测试并确认按预期失败，再实施、验证和自审；已有代码已满足行为时运行现有测试并记录证据，不为制造失败重复添加测试。各任务文件详见配套范围表。

### N0：核验现有修改并统一清单/动作类型

**文件：** Workers/Services Contracts、JSON contexts、安全 guard；补聚焦契约测试。消费现有 ManifestVersion/HostApiVersion/Native/argv 草稿，产出第 3 节公共定义。

- [ ] 核对工作区最新差异与适用规则，记录哪些能力只是声明、哪些已接入/已测试；保护其他修改，不整文件覆盖。
- [ ] 直接统一 resident 与 command action 定义、programRef、Control 可选值、typed input/result；未知字段/执行器/版本报错，没有旧清单 fallback。
- [ ] 测普通 Worker、Managed Worker、原生命令、Hook 各一份合法清单及互斥非法字段；动作调用不能携带 executable/argv/cwd 覆盖。
- [ ] 运行契约与 JSON 源生成测试，确认所有公共 DTO 可在当前 AOT 约束下序列化，不仅通过字符串匹配。

### N1：完善 Native 启动与固定程序绑定

**文件：** 当前 NativeRuntimeAdapter 所在 `WorkerRuntimeAdapters.cs`、`IWorkerRuntimeAdapter.cs`、WorkersManager；新增/复用 `NativeProgramResolver`，不创建第二个 Native 类。产出 NativeProgramSpec/StartSpec。

- [ ] 测 package 入口边界、system program 显式批准、缺失文件/权限/平台不匹配、无 Go 编译器仍可启动已编译程序。
- [ ] 使用 ArgumentList，校验程序身份与配置代次；每个程序逐实例检查，不用通用 runtime cache 宣称任意 binary 可执行。
- [ ] 测入口含空格/Unicode、argv/env/cwd 精确、PATH 同名程序不改投；验证失败释放端口/进程资源并返回可消费错误。
- [ ] 跑 Native/Worker 聚焦测试，实际 fixture 启动后验证输出和退出，不把 spec 生成成功当成托管通过。

### N2：常驻程序托管与可观察生命周期

**文件：** WorkersManager 生命周期 partial、WorkerStateMachine、进程 owner/log/port；消费 N1 启动绑定。

- [ ] 写真实长驻 fixture，测试 Service/Daemon、stdout/stderr、端口、崩溃退避、人工停止和旧 run 回调竞争。
- [ ] 明确 processRunning/readinessUnsupported；无管理接口程序也能托管。停止先置意图、取消退避，再等待/清理当前 run。
- [ ] 用 runId/owner 避免旧事件移除新实例；不能持锁等待 drain 或 Exit。默认不要求程序打开 HTTP，也不强制给无请求 daemon 分端口。
- [ ] 测重复启动/停止幂等、无孤儿进程、当前实例退出事实与 UI 一致；宿主强制关闭时保留未确认状态。

### N3：一次性命令参数、stdin、进度与结果适配

**文件：** 新增 NativeCommandExecutor、CommandBindingResolver、结果/进度 parsers，复用受限进程执行器；消费 N0/N1。

- [ ] 测非 JSON 原始 stdout、JSON、stderr、指定退出码 0/非零、坏 JSON、0 退出但产物缺失；acceptedExitCodes 外的退出不能被文本 success 覆盖。
- [ ] 将输入 Schema 与固定 token 绑定到 argv，不执行 shell；stdin 按 closed/text/json/file 明确选择，不继承终端输入。
- [ ] closed 模式重定向 stdin 并立即关闭其写端以产生 EOF；text/json 有界写入并关闭写端，file 从批准文件流式传入，不将大文件整体读入内存。标准输入写入与 stdout/stderr 排空并发进行并受同一取消/期限约束。
- [ ] 实现 bounded-text/json/artifacts 与 none/json-lines/ffmpeg-key-value 适配；原始 CLI 不要求实现 Hook 协议，Hook 不接受混合 stdout 日志。
- [ ] 测输出洪泛/单行超长/二进制、进度乱序和 100% 后非零退出；解析器不阻塞排空，超限返回明确状态。

### N4：异步操作协调、取消和产物存储

**文件：** PluginOperationCoordinator、PluginOperationStore/SQLite schema、ArtifactStore；消费 NativeCommandExecutor。

- [ ] 先测 202/operationId 后查询、队列满/排队超时、幂等键同参复用/异参冲突、queued 取消不启动、running 取消标注副作用。
- [ ] 持久化有限状态与固定 package/config binding；常驻 restart 策略不进入命令动作路径，业务动作默认不重试。
- [ ] 实现操作归属和产物 ID、受限下载/lease/清理；禁止从请求传原始绝对路径读取文件。
- [ ] 测启动后断连、宿主恢复、未知结果、磁盘满/保留配额、更新时在途操作固定目标；重启不重放未确认命令。

### N5：可选管理接口

**文件：** ResidentControlClient、管理能力 DTO/校验、Worker 生命周期协作点。

- [ ] 测 control=null 普通程序仍正常；声明 Control 但握手不匹配不能显示业务 Ready。
- [ ] 实现 loopback 固定地址、每实例 token、runId/协议验证、health/invoke/shutdown；未声明的方法清晰返回 UnsupportedCapability。
- [ ] 管理请求不使用业务代理转发宿主 JWT；HTTP 失败区分未发送与可能已执行，不自动重放写 invoke。
- [ ] 测端口复用、过期 runId、错误 token、shutdown 忽略/超时和响应先 flush 后停止；总停止预算 15 秒、drain 10 秒。

### N6：服务化向导、API 与操作查询

**文件：** 既有 Workers/Services Controller 与前端入口、新 PluginOperationsController、mapper/权限/JSON/DI。

- [ ] 原生常驻向导配置程序来源/argv/env/端口和可选 Control；原生命令向导配置动作 Schema/参数绑定/超时/解析器/产物；不展示“自动识别任意工具”承诺。
- [ ] 原生命令调用返回 202 与统一 accepted DTO；新增操作 GET/Cancel/Artifacts API，按 caller+plugin+action 授权，不仅校验登录或 ID 存在。
- [ ] 前端有提交/执行中/完成/错误/取消/未知状态、进度和产物下载；短 HTTP timeout 不把仍运行的任务显示成已取消。
- [ ] 跑真实模板/事件与 API 测试，桌面/窄屏验证；所有 API 路径集中 config.js，组件不直接 fetch/localStorage。

### N7：同步升级所有项目示例

**文件：** 三个 extensions 包/各语言 hooks、两个 Defaults.cs、五个函数示例模板；新增三类 Native 可安装示例与 fixture，详见配套表。

- [ ] 三个现有扩展统一 stdin JSON/stdout JSON；参数含类型、操作 ID、权限和错误码，日志在 stderr；仓库文件与默认生成模板行为一致。
- [ ] 五个 Worker 模板统一清单/argv/状态/配置约定，服务样例可明确示范 health/control；Bash/PowerShell daemon 保持无 HTTP 的普通托管示例。
- [ ] 增加 basic resident、command wrapper、managed resident 原生示例，构建目标平台 fixture 后打包；不在正常启动时现场编译/下载，也不自动安装缺二进制的假示例。
- [ ] 对应语言实际运行协议 fixture；SSH/Caddy 动作用 stub 工具测试，不真实改防火墙或启动用户系统服务。旧格式明确拒绝，不保留兼容测试分支。

### N8：安装/更新、配额与跨平台集成

**文件：** Imports/Repo/Lifecycle、包脚本、测试 fixtures 与 CI；复用批准的基础安装流水线，不另造 Native 安装器。

- [ ] 校验 package/system 来源、平台/入口/展开配额/权限；Native 入口不能被在线文本编辑器覆盖或 DebugRun 重复起 owner。
- [ ] 更新 drain 与 package lease；数据/secret/operation/产物不随代码回滚，异常中断有明确激活版本，不删除用户现有开发数据。
- [ ] 用 Go 标准库 fixture 预编译 Linux amd64/arm64、Windows amd64；开发/CI 构建，NAS 不安装工具链。集成验收需真实二进制，缺 fixture 明确未验收而非 skip 记成功。
- [ ] 在三个形态上验证取消/更新/权限/洪泛/崩溃；原生命令执行一次的事实、父子进程与文件状态均取证。

### N9：资源与最终验收

**文件：** 验收报告、使用说明与脚本；不预填通过。

- [ ] 在同 NAS 比较直接工具基线与封装后的提交/控制开销，分开记工具业务耗时、宿主增量与全进程树资源。
- [ ] 跑 24 小时稳定性、并发 1/4/8、输出/文件洪泛、慢命令、快速常驻退出、循环启停；记录 FD、子进程、PSS/cgroup、p50/p95 与配额拒绝。
- [ ] 按配套验收矩阵逐项给版本、平台、命令、执行数、结果和剩余限制；没有实际 Go/FFmpeg 等制品就不能声称已完成对应实连验证。
- [ ] 最小相关测试通过后做跨模块构建/架构/前端门禁及受影响 AOT 检查，最后检查差异；不自动提交、推送或部署。

## 6. 执行依赖与停止条件

顺序：N0→N1→N2→N3→N4→N5→N6→N7→N8→N9，全程内联；N2/N3 共享 Runner 时先固化接口，不各写一套原生进程生命周期。插件平台的路径/结果/输出/权限 P0 正确性是前置验收，不以新增向导绕过。

若发现程序目标不受控、权限可被 API/MCP 绕过、命令被自动重复、产物越界、Stop 伪成功或 current run 无法确认，先修到可验证再进入下一任务。PTY/GUI/特殊 OS 权限超出首版时清楚报告，不自动升权或改执行策略。

本计划不要求给任意二进制现场生成业务协议。通用宿主负责受控执行，工具特有参数与结果通过清单适配或显式 JSON Hook 完成；复杂语义不能由语言或 Native 枚举自动推导。
