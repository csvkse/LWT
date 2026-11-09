# 插件基础设施：当前问题、可复用能力与验证清单

日期：2026-11-01。范围：LinuxWebTool 当前工作树的 Workers、Script Extensions、Shell、路径/凭据/持久化与相关测试。方法：内联只读源码审查，未修改实现、未执行插件安装更新、未读取实际秘密。

快照说明：A01–A12 描述初次审查时的实现。补充原生工具服务化计划时，工作树已出现 Native/argv/Hook/安全与有界采集等未提交草稿，尚未完成最新状态复验；A11 等不能再被当成“当前完全没有 Native 代码”的断言。后续按行为验证进度，不重复创建已有类。

用户已确认项目未正式发布，不需要旧插件兼容。下列当前缺陷仍是有效证据；修复方案直接采用一套新契约，升级项目内置的三个扩展和五个函数示例，不增加 legacy reader、输出降级或目录迁移代码。

[设计总览](plugin-platform-feasibility-and-design.md) · [现有库与行业参考](plugin-platform-libraries-and-industry.md)

新增正式范围：[原生工具服务化实施计划](native-tool-service-implementation-plan.md)与[NS01–NS20 验收矩阵](native-tool-service-scope-and-acceptance.md)，覆盖常驻、一次性 command、可选管理接口；这里的历史测试结果不证明新增范围已完成。

源码根为当前 `E:\WorkProject\CSharp\PrivateProject\Web\WebToolForLinux`。表内位置均相对于该根；行号取本次源码，可能随实现变更移动。以下分为源码确认的机制、需要专项复现的竞争风险、当前未提供的能力，不能统称为已复现漏洞。

## 1. 当前问题与影响

| 编号/优先级 | 问题与证据 | 影响与修复边界 |
|---|---|---|
| A01 高 | `WorkersManager.Imports.cs:56` 用 manifest.Id 构造目标；`ScriptExtensionManager.Repo.cs:272` 的 ZIP 安装目标缺少统一归一化/包含检查，`:284` 可删除旧目录；`ScriptExtensionManager.cs:255` 等用动作名定位 hook | ZIP entries 的防穿越不能保护 manifest ID 与动作路径。统一验证身份、每个最终目标和当前插件根；不是未经认证远程利用的实测结论 |
| A02 高 | `WorkersManager.cs:135` 先 Kill 再 NotifyStopped；`:315` 的 Exited 处理按 ID 移除，`:341` 延迟后直接重启 | 有停止/重启竞争窗口，旧回调可能影响新进程。按实例串行、停止意图、runId 与可取消退避修复；尚未做确定性并发复现 |
| A03 高 | `ScriptExtensionManager.cs:142–153` 忽略 config_reload 的返回结果；`:171` 使用 stdout.success 覆盖退出码判断 | 保存成功可能被当成应用成功；失败进程可能被展示为业务成功。统一 JSON 结果协议；非 JSON Hook 直接报协议错误，不保留猜测式降级 |
| A04 高 | `ScriptExtensionManager.cs:334–342` 普通模式只转发 ip；`:310` host 模式枚举所有参数值；`:303` 将 hook 文本交给 Shell ScriptText，后者使用 Bash | 任意自定义字段丢失、不同 target 参数语义不一致，Node/Python host hook 执行器不匹配。需要声明式参数映射和正确 interpreter，不仅改几处引号 |
| A05 中 | `ScriptExtensionManager.cs:569` 返回 ExtensionManifest 时没有完整赋值 IsEnabled/Capabilities/Target；DTO 在 `ScriptExtensionModels.cs:59` 定义这些属性 | 清单声明与实例观察可能不一致；先补解析/验证测试，不借机重构全部契约 |
| A06 高 | `ScriptExtensionManager.Repo.cs:274–288` 覆盖导入主要只保留 config.json；`Lifecycle.cs:271` 热更新直接覆盖目录，后续主要验证 manifest 可解析 | 自建数据库、状态、禁用标记可能受影响；复制中断或可解析但不可运行的新版本缺少完整激活事务。当前已有备份不等于没有风险 |
| A07 中 | `WorkersManager.cs:291` 存活十秒即可标记 Running，`:354` DTO 也以存活投影；Workers 热更新以短暂存活校验 | 没有进程与业务 readiness 的明确区别；增加可选探测，普通无接口程序仍可正常运行 |
| A08 高（资源） | `ShellExecutor.cs:228–229` ReadToEndAsync 完整收集，`:253` 才 Truncate；`WorkerProcessRingBuffer.cs:16` 限行数而非字节；扩展日志 `ScriptExtensionManager.cs:206` 整文件读取 | 输出洪泛仍可消耗内存；不能把 MaxOutputBytes 宣称为采集期上界。增量有界读取、单行上限与日志 tail |
| A09 中（性能） | `ScriptExtensionManager.cs:58/75/100` 列表串行探测每个扩展的依赖和 status；`:430–442` ProbeCommand 超时返回但无显式 kill/await 清理 | 列表耗时随插件数和慢 hook 叠加；探测退出/输出排空需专项测试。缓存/单飞/有界探测，不在每次 GET 重新启动所有命令 |
| A10 中 | `ScriptExtensionManager.cs:149/590` 配置落盘/读取；实例 CurrentConfig 返回配置；`WorkersManager.cs:263` 添加变量而未显式建立最小继承环境 | password 输入框不是只写秘密；未来第三方插件需要 secretRef、导出脱敏和最小环境。当前管理员能看到配置不等于匿名泄露 |
| A11 能力缺口 | `WorkerModels.cs:6` 只有六种 runtime；`IWorkerRuntimeAdapter.cs:9` 主要使用字符串 Arguments | 没有 Native、通用 argv 或标准控制接口；独立完成 Native 小切片，不需要先引入所有 RPC 框架 |
| A12 能力缺口 | `ScriptExtensionModels.cs` 为简单字典参数与简化能力；两个 Manager 各自处理状态/配置/包 | 缺少明确 manifest/host API 版本、action Schema、operationId、错误分类和持久能力 grant。直接升级清单、示例与测试，不设计双版本读取 |

关键路径可直接打开：[Worker 启停实现](../src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.cs)、[扩展动作/解析](../src/LinuxWebTool.Infrastructure/Features/Services/Adapters/Extensions/ScriptExtensionManager.cs)、[扩展导入](../src/LinuxWebTool.Infrastructure/Features/Services/Adapters/Extensions/ScriptExtensionManager.Repo.cs)、[Shell 输出采集](../src/LinuxWebTool.Infrastructure/Features/Shell/Adapters/ShellExecutor.cs)。

## 2. 已有可复用能力与限制

| 设施 | 本地位置 | 结论 |
|---|---|---|
| Worker runtime adapters | `src/LinuxWebTool.Infrastructure/Features/Workers/Adapters/IWorkerRuntimeAdapter.cs`、`WorkerRuntimeAdapters.cs` | 已有策略/适配器基础；Native 可追加，六种语言继续支持，启动参数统一新契约，不保留字符串参数兼容路径 |
| Worker FSM / CrashLoop | `src/LinuxWebTool.Infrastructure/Features/Workers/Supervisor/WorkerStateMachine.cs:10` | 已有 1/3/10/30 秒退避和熔断；要补停止/runId，不再平行造一套互相冲突的状态源 |
| Worker 日志/端口 | `Supervisor/WorkerProcessRingBuffer.cs`、`WorkerPortManager.cs`（相对 Workers） | 已有基础；端口可用性探测不是持久租约，日志行数上限不是内存上限 |
| Shell 执行/超时/信号量 | `src/LinuxWebTool.Infrastructure/Features/Shell/Adapters/ShellExecutor.cs:25/203` | 可借鉴启动失败、超时、取消和结果结构；先修采集上界。私有实现需小范围抽取，不能跨域复制 |
| 路径分量与 Windows 路径检查 | `src/LinuxWebTool.Infrastructure/Shared/Support/FileBrowserPath.cs:7/64/67` | Normalize/IsWithin/HasReparsePoint 可复用；不等于所有 OS 安装路径已安全 |
| API 路径授权 | `src/LinuxWebTool.Infrastructure/Shared/Support/PathScopeGuard.cs:49` | 具有 API Key 空限制全放行语义，且当前链接检查集中在最终目标；不能直接充当安装/删除的 fail-closed Guard |
| AES-GCM | `src/LinuxWebTool.Infrastructure/Features/RemoteHost/Security/HostCredentialProtector.cs:27` | 可借鉴加密封装，领域位置不适合直接变成所有插件依赖；主密钥恢复/损坏处理需单独审查，不能盲目复制 |
| 统一执行与记录 | `src/LinuxWebTool.Infrastructure/Features/Scheduling/Adapters/UnifiedExecutionEngine.cs`、`Shared/Persistence/ExecutionStore.cs` | 有策略与执行审计经验；现有记录偏命令/工作流，不能把插件 ID 填入 CommandId 假装契约相同 |
| 认证与模块授权 | `src/LinuxWebTool.WebHost/Shared/Middleware/ApiKeyMiddleware.cs:141` | 已有 AllowServices/AllowWorkers 与管理 JWT；缺少统一插件/action 粒度治理 |
| 配置/动作 UI | `wwwroot/app/components/services/ExtensionActionModal.js`、`ExtensionConfigModal.js`（相对 WebHost） | 可复用表单、busy/error、动态动作入口；要支持 Schema、只写秘密与真实应用状态 |
| Worker UI/管理 API | `wwwroot/app/views/WorkersView.js`、`Features/Workers/Routes/WorkersController.cs`（相对 WebHost） | 现有入口足够，不需要新建独立顶级“插件平台”才能完善能力 |
| AOT/分层门禁 | `docs/dev/architecture-gates.md`、`Composition/AppJsonSerializerContext.cs`（后者相对 WebHost） | 新 DTO 与 mapper/权限同步、禁止反射 JSON；新第三方库需真实 publish 验证，不扩门禁基线 |

路径工具的复用要区分词法检查、逐级链接解析和文件操作时间窗口。安装 staging 由宿主独占、禁止非受信链接是重要前提；这些工具不能防止同 OS 账户恶意进程的所有竞态，也不能替代操作系统隔离。

## 3. 当前第三方依赖核验

依据 `Directory.Packages.props` 与服务注册代码：

| 声明依赖 | 声明版本 | 本功能可复用方向 |
|---|---|---|
| Polly.Core | 8.7.0 | 已有韧性原语；只用于有明确资格的重试、探测、退避 |
| Microsoft.Data.Sqlite | 10.0.12 | 插件配置 revision、授权/操作记录；不必另起数据库服务 |
| Dapper / Dapper.AOT | 2.1.35 / 1.0.52 | 小型存储适配，遵守现有 AOT 约束 |
| Yarp.ReverseProxy | 2.3.0 | 业务 HTTP 代理；不能替代 plugin lifecycle/action protocol |
| Quartz.Extensions.Hosting | 3.18.2 | 已有调度基础；不应为每个 health tick 生成持久 cron job |
| HttpClient / Channels / SemaphoreSlim / System.Text.Json | .NET 框架 | 首选基础设施；HTTP 连接池、有界队列、实例串行化、源生成 DTO |

未发现当前项目声明 CliWrap、Stateless、StreamJsonRpc、JsonSchema.Net、Extism 或通用进程外插件 SDK。它们是待评估候选，不是已经可直接调用的本地设施。版本表是工作树声明，不是安全扫描或全部生产部署证明。

先前 PATH 观察中 dotnet/node/python 可找到，go/rustc/cargo/tunnel-client 未找到；不等于全盘未安装，也不代表 NAS 环境。本轮没有为研究安装任何工具或依赖。

## 4. 建议修改归属与顺序

| 范围 | 对应文件/目录 | 必要变更 |
|---|---|---|
| 修现有行为 | `WorkersManager.cs`、`ScriptExtensionManager.cs`、两者 Repo/Imports/Lifecycle partial | ID/路径、参数、结果、stop/runId、探测和输出采集；不先做全目录搬迁 |
| 契约 | `Contracts/Features/Workers`、`Contracts/Features/Services` | Native=6、结构化 argv、完整字段解析和单一新清单；替换旧的弱参数/结果定义 |
| 公共执行边界 | `Contracts/Shared/Contracts`、`Infrastructure/Shared` | 只提取确有跨域复用的受限 launch/ownership/output 接口；不是把业务状态放 Shared |
| 业务编排 | `Application/Features/Workers`、`Application/Features/Services` | lifecycle/action/installation 用例；不让 Controller 直接做文件事务 |
| 安装/控制适配 | Infrastructure 对应 feature 下的小型 adapters | staging、revision、可选 HTTP、秘密存储；不写一个万能执行器或双格式 reader |
| Web 接入 | 现有 Controller、`EndpointsMapper.g.cs`、DI、JSON context、ApiKeyMiddleware | 每项 API 与权限/序列化同步，真实错误映射 |
| 前端 | Workers 与 services 既有视图/组件、`app/config.js` | 实际状态、应用失败、能力授权、Native 与可选控制；保持单一请求入口 |

示例范围已核实：`extensions/` 下有 sample-healthcheck、ssh-access-guard、caddy-server-manager，`ScriptExtensionManager.Defaults.cs` 生成同三项；`WorkersManager.Defaults.cs` 生成五个 demo。它们都在新契约的同步升级范围，不能只改宿主而保留旧协议示例。

这里只给实现边界，不自动授权改动清单中的全部文件。详细工作量应按 P0–P4 拆成可回归切片；允许直接改变未发布接口，但每片必须升级对应示例和测试。

## 5. 最小回归与专项验收

| 验收项 | 可观察标准 |
|---|---|
| V01 目录边界 | 非法 ID/绝对路径/相邻前缀/动作逃逸/链接被拒绝，无授权根外写入或删除 |
| V02 生命周期竞争 | 停止后不重新拉起；旧 run 的 exit/延迟任务不清除新实例；异常停止返回可确认结果 |
| V03 参数协议 | 自定义非 ip 字段、空格/引号/Unicode/布尔/数值精确送达；host/container 语义一致，无位置参数降级 |
| V04 结果可靠 | 非零退出码不能被 success:true 覆盖；reload 失败显示已保存未应用；坏 status 不假绿 |
| V05 清单解析 | IsEnabled/Capabilities/Target/Options 等已声明字段完整往返，未知/不支持版本明确拒绝 |
| V06 包与更新 | staging 失败不破坏现有版本；数据库/秘密/状态保留；运行失败触发真实回退或明确错误 |
| V07 输出上界 | 无限输出/单条超长行/超大日志下，保留内存有界、管道不死锁、协议超限明确 |
| V08 探测成本 | 多插件慢 status 不阻塞所有列表；超时命令实际结束、并发/缓存生效、无探测进程持续积累 |
| V09 权限/秘密 | 未批准能力在 API/MCP/工作流均拒绝；秘密不回显/导出/进日志或传入其他插件环境 |
| V10 Native | 不安装编译器即可运行匹配平台程序；argv/env/日志/停止真实验证，六种现有 runtime 按新契约验证 |
| V11 可选 Control | 无控制接口普通程序仍正常托管；managed 插件握手身份、取消、shutdown 与端口复用检查通过 |
| V12 资源/平台 | NAS 稳定性、Windows 路径与权限、Linux 进程所有权有实际证据，不拿单 OS 测试代替跨平台支持 |

建议沿用 `tests/LinuxWebTool.UnitTests/WorkersManagerTests.cs`、`ExtensionsAndWorkersCodeAndZipTests.cs`、`ServicesHubTests.cs`，新增聚焦 race/process/protocol fixtures；前端用既有模板执行和事件测试，不只检查源码文本。

新增 V13：三个扩展仓库包与内置生成模板、五个 Worker 首次生成清单全部符合新 Schema，按对应语言实际执行协议 fixture；旧格式明确报错，项目中没有 legacy adapter/别名解析/旧目录回退分支。源代码/示例升级不授权自动覆盖开发机器上的现有数据。

原生工具新增验收：命令 argv 绑定、原始 stdout 与 Hook 协议区分、acceptedExitCodes、进度/产物校验、202/操作状态/取消、恢复不重放、产物下载权限、optional Control 与目标平台真实制品，统一由 NS01–NS20 承接，避免在不同文档维护两套相反标准。

本会话上一轮针对以上三个测试类执行结果为 **25 passed / 0 failed / 0 skipped**。当前研究没有代码变化，因此不重复运行；该结果只证明当前覆盖通过，不能替代 V01–V13，也不能要求为保留旧接口而保留旧断言。专项测试、安全复现、Native/AOT 发布及压力数据尚未取得，不能预填“全部通过”。
