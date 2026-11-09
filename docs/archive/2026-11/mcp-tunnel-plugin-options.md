# MCP Tunnel 插件：语言、现有库与行业方案比较

调研日期：2026-11-01。面向单机/NAS、自用、低资源占用。比较依据为一手仓库/官方文档与当前项目源码；**没有运行横向基准测试，不能把语言特征当作实测排行**。

[可行性与设计](mcp-tunnel-plugin-feasibility.md) · [当前基础设施核验](mcp-tunnel-plugin-infrastructure.md)

## 1. 语言选型

| 路线 | 部署与资源特征 | 本项目成本 | 建议 |
|---|---|---|---|
| Go 单进程 Bridge | 预编译程序；goroutine 适合长连接 I/O；GC、Schema 和队列仍占内存 | 官方 Tunnel 与 MCP SDK 可同语言集成；当前 Workers 需 wrapper 或 Native 扩展 | **独立低占用插件首选验证**，不是宣称一定最快 |
| C# 独立预编译 Worker | JIT/AOT 两种选择；AOT 有缩小启动/内存的机会 | 可复用团队语言、DTO/日志/存储经验；独立进程不会免费共享主进程堆 | **维护成本优先的首选备选**；先验证 SDK AOT 兼容 |
| C# 内置主进程 | 少一个应用进程，可增量复用连接池/宿主 | 修改现有 frozen MCP 路由、引入新客户端协议，故障影响主应用 | 仅在认可耦合后选择；与“可独立卸载插件”目标有取舍 |
| Rust | 可构建原生异步服务，资源控制细；构建目标/libc/依赖影响制品 | 有本地 WebCodex tunnel crate 可借鉴；现代 rmcp 的旧 SSE 边界需补齐 | 实测 Go 不达标或团队熟悉 Rust 时考虑 |
| Node.js / TypeScript | MCP 生态适配方便，已有 Worker 支持；需要 JS runtime | 开发便利，适合协议试验；每个 stdio server 可能又起一个 runtime | 原型/生态集成路线，不能预判一定比 Go 慢 |
| Python / FastMCP | 代理/组合现成能力丰富，已有 Worker 支持；需要解释器与依赖 | 最快验证聚合逻辑的候选，部署/包管理成本需计入 | 有现成 Python 后端时值得试；不是最低资源默认答案 |
| Bash / PowerShell | 适合启动包装；文本流、并发会话与协议错误处理成本高 | 已有 runtime | 仅做 launcher，业务代理不用 shell 重写 |

本地项目目标框架为 net10.0，WebHost 声明 AOT 兼容，并关闭默认反射 JSON。Microsoft 文档确认 Native AOT 可以改善启动和内存，但存在动态加载、代码生成、裁剪等约束；因此“项目支持 AOT”不能证明新 MCP SDK 的任意使用方式都能 AOT 发布。[Native AOT 说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)

**选择依据**：本次优先 Go 的关键是官方 Tunnel SDK 可嵌入、现有 MCP Client/Server transport 齐全、容易封装独立制品；不是拿不同框架的公开跑分替代同负载测试。下游 Python/Node MCP、浏览器自动化或容器可能远大于 Bridge 的内存，不能只优化主程序语言。

## 2. 可直接复用的库

| 库 / 来源 | 已核实的能力 | 需要验证的边界 |
|---|---|---|
| [openai/tunnel-client](https://github.com/openai/tunnel-client) | 官方控制面客户端；CLI 和可嵌入 Go SDK；有 in-memory 集成示例 | 与所选版本的 lifecycle、会话、热更新、去重/恢复语义兼容，不自行猜协议 |
| [modelcontextprotocol/go-sdk](https://github.com/modelcontextprotocol/go-sdk) | 官方 Go MCP Client/Server；提供协议与 transport 实现 | 使用固定版本；工具动态目录更新、任务/订阅与所需版本兼容 |
| [Go transport 文档](https://github.com/modelcontextprotocol/go-sdk/blob/main/docs/protocol.md) | 当前保留 SSEClientTransport，支持旧式双端点 SSE | 不把“支持旧 protocol version”自动等价于全部 transport/特性都可用 |
| [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk) | 官方 C# 客户端/服务端 | 动态 Schema/JSON 与 AOT 的实际发布验证、依赖版本锁定 |
| [C# transport 文档](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md) | stdio、Streamable HTTP、旧 SSE 及传输选择说明 | 不盲目依赖 AutoDetect，明确后端 transport 可减少误探测 |
| [modelcontextprotocol/rust-sdk / rmcp](https://github.com/modelcontextprotocol/rust-sdk#transports) | 当前提供子进程/stdio、Streamable HTTP 与异步服务集成 | 当前主线明确不提供旧 2024-11-05 双端点 SSE；不要承诺直接满足旧 SSE 需求 |
| [MCP TypeScript Client](https://ts.sdk.modelcontextprotocol.io/client) | stdio、Streamable HTTP、旧 SSE client 示例 | 核对 v1/v2 API 和运行时要求，避免混用文档示例 |
| 当前仓库 Polly.Core / SQLite / HttpClient | 重试/熔断原语、持久化、连接池 | C# 原语不能直接当成 Go 可调用库；只复用宿主边界或设计经验 |

官方 Go SDK 的文档给出了版本/协议支持矩阵；当前已覆盖现代协议及兼容分支。实施时应锁定一组验证通过的 Tunnel SDK + MCP SDK 版本，记录 commit/tag 与许可证清单，不能每次安装自动拉 main/latest。[Go SDK 版本矩阵](https://github.com/modelcontextprotocol/go-sdk#version-compatibility)

推荐实现顺序是 SDK 组合，不是复制 poll/response 后自己实现 MCP 会话。原始 JSON 转发可能丢失取消、进度、版本 metadata 和异步通知；SDK 也不能替代授权与目标绑定策略。

### WebCodex Rust crate 的定位

本地 `E:\WorkProject\Rust\【参考项目】\webcodex-main\crates\webcodex-openai-tunnel` 的 Cargo.toml 声明 Apache-2.0。它提供独立 Rust TunnelClient 与 CLI，并有本地固定凭据、响应投递和持久 receipt/fence 等实现，值得作为可靠性参考；不等于完整的多 MCP 聚合网关。

其 README 明确只接固定 Streamable HTTP `main` 目标，不包含旧 SSE 客户端、stdio、多 channel 或动态服务目录。当前 SDK 与官网可能比该本地快照更新；不能把参考项目严格的 legacy Tunnel ID 正则照搬为整个产品的通用资源 ID 校验。应保留平台颁发的完整 ID，并采用所选官方 SDK 的验证行为。[官方 Tunnel client 说明](https://github.com/openai/tunnel-client)

## 3. 代表性行业方案比较

这是按目标挑选的代表性开源/厂商方案，不是市场占有率排名。功能有文档证据，NAS 资源评价是基于部署形态的推断，均须实测。

| 方案 | 与需求重合点 | 相对缺口/成本 | 本任务建议 |
|---|---|---|---|
| OpenAI tunnel-client | 官方 Secure Tunnel 接入、健康观测、transport 转发 | 不应默认等于带勾选 UI 的任意多后端聚合器 | 无论自研或现成网关，优先复用其连接层 |
| MCPProxy（Go） | 多后端、单二进制/内置 UI、工具发现、直接或按需调用 | 要核对选中白名单、direct 模式、会话/配置 API 与 Tunnel 结合方式 | **最优先做现成方案 P0 对照** |
| FastMCP（Python） | create_proxy、多服务组合、namespace、工具 visibility | 需 Python 环境；默认 session/头转发策略要调整 | 最快验证逻辑或作为低开发成本方案 |
| Docker MCP Gateway | 容器化 MCP 生命周期、profiles、工具选择 | 要有 Docker daemon/镜像与资源；宿主若在容器中，stdio 所在环境需明确 | NAS 已使用 Docker 且重视隔离时优先考虑 |
| IBM ContextForge | registry、代理、管理、virtual servers 的工具组合 | 完整治理系统的依赖/管理成本；ARM 制品支持需检查具体 release | 借鉴虚拟工具集模型，单机首版不整套引入 |
| Microsoft MCP Gateway | session-aware 路由、动态工具路由、容器编排 | 面向 Kubernetes/平台部署，超出单机低占用范围 | 借鉴会话和工具路由，不引入整个平台 |
| WebCodex（本地参考） | Tunnel 可靠性、MCP facade、权限与运行时调度 | 完整 Server/Runner 平台，与轻量聚合目标不同 | 复用设计与小范围库，避免整体移植 |

### MCPProxy：先验证是否已经足够

仓库描述其核心为带内置 Web UI 的单二进制，可做多上游聚合。默认按需发现与直接暴露工具是不同模式；本需求优先测试 direct 方式，避免将每个勾选工具都改为模型手写通用 call 参数。[项目仓库](https://github.com/smart-mcp-proxy/mcpproxy-go)

其 direct 模式文档描述配置修改后重建工具集并发送 list_changed。这提供了动态目录的参考，但不是 ChatGPT 立即刷新缓存的证明；必须验证服务/工具选择是否同时约束所有调用路径，以及 loopback HTTP 入口能否被 Tunnel 消费。[动态直接工具模式](https://docs.mcpproxy.app/features/schema-deferred-direct-mode/)

若现成代理通过 P0 的过滤、身份和资源验收，只开发 LinuxWebTool 的管理适配；不重复造完整代理。需要两个进程并不自动意味着不可接受，必须量测实际额外开销。

### FastMCP：能力完整，默认值不能原样套用

proxy 文档有多后端与会话隔离说明，也警告共享连接可能造成上下文混用；当前默认行为还会转发符合条件的入口头。此方案应显式关闭无关 incoming headers 转发，按后端绑定凭据，并针对 stdio 冷启动/会话池化测量成本。[Proxy Provider](https://gofastmcp.com/servers/providers/proxy)

visibility API 支持按名称/标签启停组件，可映射为工具勾选；但外部审批、精确版本指纹、原子配置和执行授权仍需产品层处理。不能把可见性变化等同于完整用户鉴权。[Component Visibility](https://gofastmcp.com/servers/visibility)

### Docker、IBM、Microsoft：借鉴哪些部分

Docker 的开源 CLI 可以独立于 Desktop 使用；profiles 可组织服务并启停工具，适合已有 Docker 的 NAS。容器生命周期、镜像信任和 profile 工具集值得借鉴；本任务不默认赋予插件 Docker socket 权限，不将厂商其他治理产品的可用资格混同于开源 Gateway。[开源 Gateway](https://github.com/docker/mcp-gateway)、[profiles](https://docs.docker.com/ai/mcp-catalog-and-toolkit/profiles/)

ContextForge 的 virtual server 通过 associated_tools 组合工具集，贴近“勾选后对外形成一个 MCP”。可借鉴其逻辑模型；仓库中的 ARM/容器限制应按将采用的 release 核验，不能据一个示例推断任意 NAS 均可运行。[ContextForge](https://github.com/IBM/mcp-context-forge)

Microsoft 方案以 Kubernetes 环境中的会话路由和生命周期为重点，另有工具登记与 router。它证明“入口/工具路由/执行服务器分离”是实际方案，但当前单机不需要为了采用同一思想引入 K8s。[Microsoft MCP Gateway](https://github.com/microsoft/mcp-gateway)

## 4. 三条可实施路线的权衡

| 路线 | 进程形态 | 开发量 | 风险与收益 |
|---|---|---|---|
| A：现成代理 + 官方 Tunnel CLI | 代理 + Tunnel + 所需 stdio 后端 | 最少，自定义管理集成 | 先验证过滤/API 是否满足；资源可测，升级要兼顾两边 |
| B：Go Bridge + 官方 Tunnel SDK | 一个 Bridge + stdio 后端；不另开数据面端口 | 中等 | 精确满足 UI/策略，协议主体复用官方；需自己管理后端目录与会话 |
| C：C# Worker + 官方 MCP SDK + 独立 Tunnel | C# Worker + Tunnel + stdio 后端 | 中等，团队语言统一 | 需要验证 SDK/AOT；可先用预编译 DLL，避免运行时编译 |

优先顺序：**用 A 做验收基线，B 作为定制产品目标，C 作为维护成本导向的备选。** 并不要求先把三条路线全部实现；P0 只做到足以验证真实兼容和资源，不扩成产品开发。

若采用 B，管理 HTTP 与 SDK in-memory MCP 数据面分开；Tunnel 本身需要的 outbound HTTPS 仍存在。省掉本地 HTTP hop 的绝对延迟通常不是主要瓶颈，主要价值是减少生命周期对象和一份独立程序。

## 5. 上线前必须取得的证据

- 固定 SDK/网关版本，检查许可证、release 制品架构、校验和、支持的协议矩阵；本次没有安装或安全扫描它们。
- 三个真实 transport 后端的工具分页、取消、错误、结构化结果和通知行为；现代协议与 legacy 协议各自验证。
- 旧 ChatGPT 缓存调用被拒绝、全不选无工具、同名工具不串路由、新增工具不自动放行、Schema/URL/凭据变更不继承旧信任。
- 同一授权主体多个聊天下，有状态后端不串扰；没有稳定会话身份时采用隔离或明确拒绝状态依赖功能。
- NAS 同负载的进程树总内存、CPU、p95、本地开销、FD/进程增长和异常恢复；单次执行不超过控制面预算。
- 真实 ChatGPT 工具发现和一次只读调用通过后，才称为端到端连通；仅 SDK 配置合法/健康 ready 不足。

## 6. 来源使用范围

外部来源均为上述项目所有者仓库或官方文档，访问日期 2026-11-01；主要阅读可变的 main/master/官网页面，没有将其当成固定 release。性能表为工程推断，预算在主方案中明确标为待测目标；功能声明不保证特定账号可以使用，也不证明具体软件包已装入当前系统。
