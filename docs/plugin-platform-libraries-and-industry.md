# 插件扩展能力：现有库、通信方式与业界方案比较

调研日期：2026-11-01。所有外部依据为项目所有者仓库、官方文档或维护者文档；未安装、编译或压测候选库，没有市场占有率/跨语言性能排行。

范围已按用户最新要求收敛：未发布项目直接升级单一契约和内置示例，不设计旧插件兼容、迁移或版本回退 reader。保留多种 runtime 和两种执行形态是产品能力，不是保留旧格式。

[设计方案](plugin-platform-feasibility-and-design.md) · [当前系统复用清单](plugin-platform-current-state-audit.md)

## 1. 库的采用建议

优先复用当前 .NET 基础与已声明依赖；只为缺少的能力引入小库。不能通过“加了状态机/RPC 库”替代进程所有权、权限和容量设计。

| 候选 | 已查证用途 | 本项目建议 | 必须核验的限制 |
|---|---|---|---|
| .NET Process / ArgumentList / Channels / SemaphoreSlim / HttpClient / System.Text.Json | 原生进程、参数、队列、串行化、HTTP、JSON 基础 | 首选；先改善现有封装 | 稳定的 OS tree ownership、采集字节上界与 AOT DTO，不由基础类型自动提供 |
| Polly.Core | retry、timeout 等韧性策略 | **已有 8.7.0**，只复用适当策略 | 不能对写动作盲重试；连接故障与未知执行分开 |
| CliWrap | .NET 子进程参数、管道、异步执行、强制/协作取消 | 可作为受限 ProcessLauncher 的候选 | 需要真实跨平台/进程树/AOT 测试；Buffered 模式不能解决无界输出 |
| Stateless | C# 显式状态、触发器、guard、异步状态动作 | 暂不必引入，现有 FSM 可先修 | 库不代替每实例串行和 runId；不能假定异步 API 自动线程安全 |
| StreamJsonRpc | .NET Stream/WebSocket/Pipe 上的 JSON-RPC、取消等机制 | 将来有 stdio 双向需求再评估 | .NET 主机能用不代表任意 Go/Python 程序自动兼容；framing/版本/AOT 必须协商验证 |
| JsonSchema.Net | JSON Schema 构建与校验 | 新清单与动态动作/表单 Schema 的候选 | 固定支持 dialect、缓存构建；限制 $ref、资源大小和解析时间；真实 AOT 发布验证 |
| gRPC / Protobuf | 类型化跨语言 RPC 与流式 API | 不作为首版全部插件的硬依赖 | 生成代码、版本演进、AOT 与资源开销都计入；不是仅换 transport 就获得插件 SDK |
| HashiCorp go-plugin | Go 主程序与进程外插件的 RPC 生命周期/协议基础 | 借鉴或供独立 Go 程序使用，暂不直接嵌入 C# 宿主 | 它是 Go 库，不是开箱即用的 .NET 插件框架 |
| Extism | WASM 插件运行时与多语言 SDK 生态 | 受限计算型插件的后续候选 | 任意 Native Go/系统运维脚本不能直接换后缀后运行；host functions/资源权限需另设计 |

库依据：[CliWrap](https://github.com/Tyrrrz/CliWrap)、[Stateless](https://github.com/dotnet-state-machine/stateless)、[StreamJsonRpc](https://github.com/microsoft/vs-streamjsonrpc)、[JsonSchema.Net](https://docs.json-everything.net/schema/basics/)、[Polly retry](https://www.pollydocs.org/strategies/retry.html)、[go-plugin](https://github.com/hashicorp/go-plugin)、[Extism](https://github.com/extism/extism)。

补充采用原则：

- CliWrap 的管道/取消接口值得试验，但输出保留和后代进程结束必须自行验证。使用它时仍设计 bounded sink，不依赖“异步读取”自动节省内存。
- Stateless 可以让状态表更易读；首版实例状态较少时，集中 transition 函数加单实例锁更简单，不为采用模式机械引入依赖。
- JsonSchema.Net 建议构建后重复校验，而不是每次调用重建。禁止自动联网获取任意远程引用，避免校验插件清单时触发隐式网络访问。
- StreamJsonRpc 仅在选择 JSON-RPC transport 后有价值；单次 stdin JSON 的 hook 协议不需要它，HTTP 业务接口也不必为了形式统一再包一层 RPC。

当前项目没有声明上述新候选；“可复用”指功能适配可能性，不是本机已还原或已满足 AOT。实施前锁定正式版本、toolchain/目标框架、许可证与依赖清单，再做最小发布/协议测试。本次不声称哪个版本安全无漏洞。

## 2. Native AOT 对插件形态的影响

当前主应用声明 AOT 兼容并使用源生成 JSON。Microsoft 说明 Native AOT 对动态程序集加载、运行时代码生成、裁剪等存在限制。因此不建议把“加载第三方 .NET DLL/反射发现类”作为默认扩展模型；进程外程序与序列化契约更贴合现有多语言目标。[Native AOT 限制](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)

这不等于所有反射或所有 gRPC/JSON Schema 用法都不可用，也不等于某个库标注支持 .NET 就已经 AOT 兼容。应对实际采用的 API、生成代码和目标平台执行发布/启动测试。

## 3. 通信方式对比：协议、transport 与执行形态分开

| 方式 | 适合场景 | 优点 | 真实成本与限制 |
|---|---|---|---|
| argv/env + exit/stdout | 普通后台程序 | 无需协议改造或常驻监听 | argv 不是安全秘密存储；不是 Hook 动作接口，不能控制任意业务功能 |
| 单次 stdin JSON / stdout JSON | 所有一次性 Hook | 易跨语言、避免 shell 拼接、无需端口 | 每次启动进程有冷启动成本；不是完整 JSON-RPC |
| 原生命令 argv/stdin + 结果适配 | 不改源码封装 CLI | 无需为通用参数/结果编写脚本；复用工具自身能力 | stdout 不要求 Hook JSON，需处理工具选项、退出码、进度、产物与取消 |
| loopback HTTP + JSON | 可管理常驻 Worker | 与现有 WebHost/HttpClient 容易集成、易观察 | 端口/身份/token/超时与连接复用；loopback 不等于无需认证 |
| stdio JSON-RPC | 无端口双向插件、语言服务式交互 | 复用单进程，method/id/error/notify 有规范 | frame 上限、并发关联、取消、死锁、stdout 协议独占 |
| Named Pipe / Unix Socket + JSON-RPC | 本机 IPC，避免 TCP 端口 | 可结合本机权限；无需远端网络 | 两平台地址/权限不同，同样需要 framing 与身份校验 |
| gRPC | 多语言生成契约、流式与较大 API 面 | 明确 service/type、成熟工具生态 | 工具链与版本管理增加；需同负载测量，不能先断言最低延迟/内存 |

**选择建议：** 所有项目 Hook 一次性升级为单次 JSON，常驻插件需要控制时首选 HTTP JSON；Native 适配器只负责启动。未来 stdio/gRPC 是可替换的 command adapter，不改变权限、结果与生命周期语义；不保留旧参数/混合 stdout 输出的兼容路径。

原生工具服务化的具体清单绑定、操作/产物语义和任务见[实施计划](native-tool-service-implementation-plan.md)。直接 command 执行是独立于 Hook 的明确模式，不是为接纳任意 stdout 而增加的旧协议 fallback。

不建议“所有插件统一成 HTTP 常驻服务”：简单动作会多一个长期进程/监听端口，也会把脚本轻量调用变成需要部署服务。反之，用每次启动脚本去承载所有 MCP 长连接也不适合。

## 4. 代表性业界方案

以下是与目标相关的成熟或有代表性的设计，不是产品采用率排名，也不表示应整体部署它们。

| 方案 | 一手资料说明的形态 | 可借鉴 | 不直接照搬 |
|---|---|---|---|
| HashiCorp go-plugin | 子进程 + RPC，插件实现与 host/client 分开 | 协议协商、接口明确、进程外失败边界 | C# 主机不能直接当 Go host 使用；RPC 本身不是 OS 沙箱 |
| Grafana backend plugins | 主服务器启动插件子进程，经 gRPC 调用，有 SDK/健康等能力 | 版本化 capabilities、health、实例配置与连接复用 | Grafana 的前端/数据模型/SDK 与本项目不同 |
| VS Code Extension Host | 明确的扩展宿主运行环境、扩展 API 与 UI 边界 | 插件贡献点、host API 兼容、不同执行位置 | 不是每个扩展都有一个 OS 进程，更不等于针对恶意扩展的完整沙箱 |
| systemd | 服务生命周期、notify readiness、停止超时与进程清理策略 | 就绪与存活分离、先协作停止再强制、单一 owner | NAS/容器不保证有 systemd；本任务不授权新增系统服务或改权限 |
| Supervisor | program 状态/重启策略、stopwaitsecs、group 停止 | 崩溃退避、外部可观察状态、进程组语义 | 不能把 HTTP/XML-RPC 管理进程等同业务动作协议；不必再起守护层 |
| PM2 | 应用进程管理、ready/停止消息与信号、kill timeout | 协作就绪/关闭、Windows 与 Unix 停止差异 | 不因存在 Node Worker 就为全部语言增加 PM2 依赖 |
| Extism | WASM 模块加载/函数调用/SDK | 对受限插件设计明确 host functions 与能力边界 | 当前需要宿主运维和任意原生程序，不能全部迁入受限 WASM |

资料：[go-plugin](https://github.com/hashicorp/go-plugin)、[Grafana backend system](https://grafana.com/developers/plugin-tools/key-concepts/backend-plugins/)、[VS Code Extension Host](https://code.visualstudio.com/api/advanced-topics/extension-host)、[VS Code 扩展能力边界](https://code.visualstudio.com/api/extension-capabilities/overview)、[systemd.service](https://www.freedesktop.org/software/systemd/man/latest/systemd.service.html)、[Supervisor 配置](https://docs.supervisord.org/configuration.html)、[PM2 优雅启停](https://pm2.keymetrics.io/docs/usage/signals-clean-restart/)、[Extism](https://github.com/extism/extism)。

对本项目最直接的组合是：**Grafana/HashiCorp 的“进程外 + 版本契约”思想，加 systemd/Supervisor/PM2 的“生命周期可观察且有界”思想**。实现仍保持 .NET 宿主和现有 Worker/Extension 入口，不为了借鉴架构引入另一套完整平台。

## 5. 性能与资源方面的判断

没有可靠证据表明把当前 JSON 改成 gRPC、把 C# FSM 换成库、或把全部脚本常驻就能降低 NAS 总占用。首要开销通常是 runtime 冷启动、插件子进程、探测频率、输出和在途任务数量，需要在当前项目实测。

应分别测量：

- **控制路径**：缓存列表、状态查询、序列化、权限检查与本地 IPC。
- **执行路径**：进程启动、后端工作、输出采集、取消和停止。
- **安装路径**：包展开、校验、磁盘峰值、复制与版本切换。

进程外隔离增加 IPC/进程成本，同时减少主进程故障耦合；同进程库省 IPC 但扩大故障与 AOT 兼容面。选择依赖真实插件需求，不能把某个“hello world”数字当成整个平台的资源承诺。

首版优化顺序：输出采集上界 → 生命周期竞争 → status/依赖缓存和单飞 → per-plugin 配额 → 连接复用与 Schema 缓存 → 最后测是否值得改变通信格式。更具体的候选预算和工作集在[主设计](plugin-platform-feasibility-and-design.md#9-性能验证与验收)中。

## 6. 推荐复用决策

| 决策 | 首版结论 |
|---|---|
| 主宿主语言 | 保持现有 .NET 10；Go 是未来 Bridge 或其他 Native 插件的实现语言 |
| 进程库 | 先对现有实现做有界抽取，CliWrap 用短验证比较维护成本后再决定 |
| 状态机 | 先修现有 FSM 的事件串行、runId 与意图；Stateless 非必需 |
| RPC | 首版不强制引入；HTTP JSON 与 hook JSON 分别解决具体场景 |
| Schema | 单一 typed manifest 做明确验证，动态动作 Schema 再验证 JsonSchema.Net 的实际发布兼容 |
| 安装/持久化 | BCL ZIP/文件原语 + 受限 staging，已有 SQLite/Dapper；需要业务事务而非新数据库平台 |
| 重试/熔断 | 已有 Polly，只对定义明确的探测/幂等操作启用 |
| 强隔离 | 另行选择受限 OS 用户/容器/WASM；不将同账户进程外执行等同安全沙箱 |

## 7. 外部资料与本地证据的边界

所有外部页面在 2026-11-01 查询，部分为可变主分支/官网页面；正式实施需要固定版本、许可证和支持范围。没有使用第三方博客/论坛跑分支撑性能结论，也没有将下载量/星数当选型证明。

本地设施与版本以配套审计中的源码/Directory.Packages.props 为准；上表候选均不代表已安装或已集成。没有进行 NuGet 新包还原、AOT 实测、WASM/gRPC 集成或 NAS 压测，因此可行性有明确后续验证门槛，不能描述为功能已完成。
