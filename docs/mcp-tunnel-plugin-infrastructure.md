# MCP Tunnel 插件：当前系统可复用设施与缺口核验

日期：2026-11-01。范围：当前 LinuxWebTool 工作树的实际源码与声明依赖，以及当前 Windows 进程 PATH；不是 NAS 部署环境的完整软件清单。未运行服务、安装包或读取实际凭据。

[方案总览](mcp-tunnel-plugin-feasibility.md) · [语言与现成方案](mcp-tunnel-plugin-options.md)

## 1. 核验结论

**已有宿主能力足以开始做原型，但没有现成的动态 MCP 聚合插件。** 最值得复用的是 Worker 生命周期入口、模块/工具双层授权、工具勾选 UI、凭据加密原语和持久化包。运行时隔离、就绪探测和优雅停止仍需专项增强，不能把设计文档中的目标当成已经落地。

源码路径均从当前项目根目录计算；行号对应本次工作树，后续可能变化。

## 2. Workers / 函数服务

| 能力 | 实际证据 | 结论及复用方式 |
|---|---|---|
| Service / Daemon | `src/LinuxWebTool.Contracts/Features/Workers/Contracts/WorkerModels.cs:21` | 已有；常驻插件不必引入 serverless 框架 |
| 运行时策略接口 | `src/LinuxWebTool.Infrastructure/Features/Workers/Adapters/IWorkerRuntimeAdapter.cs:11` | 已有；新增 Native 适配器的自然位置 |
| 当前运行时 | `WorkerModels.cs:6`、`WorkersManager.cs:33` | Node/Python/DotNet/Bash/PowerShell/Batch；没有 Go/Rust/Native 项 |
| 启动与输出消费 | `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.cs:252` | 已有 ProcessStartInfo、stdout/stderr 日志采集；适合托管 Bridge，不适合直接消费 stdio MCP |
| 状态机 / 退避 | `src/LinuxWebTool.Infrastructure/Features/Workers/Supervisor/WorkerStateMachine.cs:10`、`:91` | 已有 1/3/10/30 秒退避、连续瞬崩熔断；没有 Tunnel/后端认证语义 |
| 健康判定 | `WorkersManager.cs:291`、`:350` 附近 | 存活 10 秒才 NotifyRunning，DTO 也根据进程存活投影状态；不是 MCP/Tunnel readiness |
| 停止进程树 | `WorkersManager.cs:135`、`:453` | 有强制清理路径；Windows taskkill /F，Linux 尝试 kill -TERM -PID 后 Kill(true)，未体现完整 drain/等待协议 |
| HTTP 端口 | `src/LinuxWebTool.Infrastructure/Features/Workers/Supervisor/WorkerPortManager.cs:12` | 已有 18000–18999 端口池和指定端口；探测释放到应用 bind 之间仍有竞争窗口 |
| 日志缓冲 | `src/LinuxWebTool.Infrastructure/Features/Workers/Supervisor/WorkerProcessRingBuffer.cs:16` | 默认 1,000 行；并非按总字节限制，不是凭据脱敏或持久审计设施 |
| ZIP/Git 导入 | `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Imports.cs:11`、`:43` | 已有 worker.json 发现与文件复制；NAS 原生二进制需要架构/校验和/执行权限处理 |
| Worker 更新 | `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Lifecycle.cs` | 已有更新/备份入口；协议配置、密钥和执行收据不得随代码回滚到旧状态 |
| 专属存储 | `src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.Storage.cs:9` | 已有 data.db / kv.db 管理；可参考存储布局，敏感配置不能直接放通用 KV |
| 宿主启动 | `WorkersManager.cs:45` | StartAsync 只初始化 CTS 和默认示例；未发现按持久 startup intent 自动拉起全部 Worker 的逻辑 |

### 关键缺口 1：原生可执行文件托管

`DotNetRuntimeAdapter` 根据 .dll/.csproj/其他入口生成 `dotnet` 参数，不是通用二进制执行器。当前 Linux 可用 `Bash` Worker 的短脚本 `exec ./mcp-bridge` 做验证，使 shell 被进程替换；这不等于系统已有 Native runtime，也不覆盖 Windows。

正式 Native 方案至少包括：绝对或 Worker 内受限入口、结构化 argv、cwd、平台/架构校验、最小环境、生命周期和前端枚举同步。运行预编译二进制无需在 NAS 安装 Go/Rust 编译器，但应核对 libc/证书/架构等运行条件；不能假定所有 Go 构建都无 CGO，或所有 Rust 构建都可跨发行版直接执行。

### 关键缺口 2：进程生命周期不等于连接生命周期

复用外层 WorkerStateMachine 管理进程；Bridge 内另外管理 Tunnel 与每个 backend。宿主 Stop 需要先发送认证停止请求/信号，等待 admission 停止、in-flight 结算和子进程退出，超时再强杀。Linux 进程组所有权、Windows 子进程树所有权需实际验证，不能仅根据负 PID kill 注释断言已成立。

协议认证失败、配置错误和执行结果未知应成为可见状态，不能让通用 AutoRestart 把它们变成无限连接循环。外层守护进程和内层重试各有责任，避免两层同时重启同一资源。

### 关键缺口 3：HTTP 管理面

`src/LinuxWebTool.WebHost/Composition/PipelineExtensions.cs:96` 实现 `/w/{workerId}/{**catch-all}` 转发，采用 HttpClient 和 ResponseHeadersRead；并不是该路径直接使用 YARP 的证据。该实现转发大量请求头，不能把它原样当作具有细粒度授权和凭据隔离的 Bridge 管理代理。

建议管理页面走明确的宿主管理员 API，再调用 loopback Bridge `/admin/*`；不要将普通 Worker 访问 URL 当作管理员授权。Tunnel 数据面使用 in-memory 或直接 loopback 入口，避免额外经过 `/w` 路径、通用超时和认证头转发。

## 3. MCP 与工具过滤

| 能力 | 实际证据 | 复用与限制 |
|---|---|---|
| 本地 MCP Server | `src/LinuxWebTool.WebHost/Features/Mcp/Endpoints/McpEndpoints.cs:16`、`:81` | 旧 SSE 与直接 POST /mcp 已有；没有发现外连 MCP Client |
| 工具 Provider | `src/LinuxWebTool.WebHost/Features/Mcp/Engine/IMcpToolProvider.cs:11` | 有 GetTools / IsPermitted / TryExecuteAsync，体现提供者策略模式 |
| 启动期路由索引 | `src/LinuxWebTool.WebHost/Features/Mcp/Engine/McpServerEngine.cs:37` | 构造时遍历工具建立 FrozenDictionary；不能直接容纳运行期新名称 |
| 发现/执行双重授权 | `McpServerEngine.cs:87`、`:115` 附近 | 已对 tools/list 和 tools/call 做模块/工具过滤，思想可复用 |
| MCP 协议基线 | `McpServerEngine.cs:17` | initialize 固定 2024-11-05，不能声称已支持当前全部协议模式 |
| 会话与队列 | `McpEndpoints.cs:12`、`:111` | SSE session 字典与无界 Channel；缺少本方案所需全局会话配额/队列字节上限 |
| 工具目录 | `src/LinuxWebTool.WebHost/Features/Mcp/Engine/McpCatalogRegistry.cs` | 当前为内置静态分类目录，不是远端 discover 聚合目录 |
| 目录 API/UI | `src/LinuxWebTool.WebHost/Features/Security/Routes/ApiKeysController.cs:200`、`wwwroot/app/views/ApiKeysView.js:181` | 已有目录获取、分类勾选、单工具勾选；可复用交互方式，需要换成远端目录和 revision |
| 本地 Bearer Key | `src/LinuxWebTool.WebHost/Shared/Middleware/ApiKeyMiddleware.cs:229` | 支持 X-Api-Key 或 Authorization: Bearer lwt_...，适合将自身作为后端 |

**不能直接复用空集合语义。** `src/LinuxWebTool.Application/Features/Security/ApiKeyPermissionMatrix.cs:143` 将 null/空配置解释成继承全部，`:184` 的集合重载也将空集放行；`ApiKeysView.js:406` 的保存流程也有继承全部和空选择处理。聚合插件必须新建明确的 selection 语义：`selectedTools=[]` 表示 deny all，继承全部必须是独立且显式的选项，首版建议不提供自动全选未来新增工具。

原引擎采用 OrdinalIgnoreCase 的工具路由字典，外部 MCP 工具应按协议精确名称匹配，不能未经检查沿用大小写折叠。若选择将聚合能力内置主应用，必须把启动期 FrozenDictionary 改成动态、可原子替换的路由快照；仅新增一个 Provider 不会让启动后新工具可执行。独立 Worker 可避免改动现有内置工具引擎。

## 4. 可复用依赖与凭据设施

`Directory.Packages.props` 声明：Dapper 2.1.35、Dapper.AOT 1.0.52、Microsoft.Data.Sqlite 10.0.12、Polly.Core 8.7.0、Yarp.ReverseProxy 2.3.0 等。`ServiceCollectionExtensions.cs:121` 注册 HttpClient；`:51` 注册 HostCredentialProtector；`:114` 附近注册 WorkersManager 与 IHostedService；`:212` 起注册内置 MCP providers。

这些是**工作树声明与注册证据**，不是 NuGet 全部已还原、版本无漏洞或 NAS 已部署的证明。未发现 `ModelContextProtocol` SDK 包声明，亦未发现 stdio/SSE/HTTP 外连 MCP Client 基础设施。YARP 解决 HTTP 转发，不解决工具目录或会话协议；Polly 可用于后端探测和连接退避，不能对所有工具执行开启自动重试。

`src/LinuxWebTool.Infrastructure/Features/RemoteHost/Security/HostCredentialProtector.cs:27` 已提供 AES-GCM 加解密，`:81` 解析主密钥；可以参考其机制抽取受审查的通用秘密存储边界，但它目前属于 RemoteHost 领域，不宜让新模块直接越层依赖。

Go Worker 不会自动获得宿主密钥解密能力。建议宿主拥有管理凭据，Bridge 自己持有私有 secret store/受限 secret 文件，或通过短期受认证本地接口获取必要凭据；不要把整个宿主 master.key 交给插件。明文 Worker EnvironmentVariables 会写入普通配置，因此真实 Tunnel Key 不应直接嵌入可导出 worker.json。磁盘加密也不等于能隔离具有相同 OS 账户权限的恶意子进程。

## 5. 当前开发机可用性

仅检查当前 PowerShell 的 `Get-Command`，未运行这些程序、未安装内容：

| 工具 | 当前 PATH 结果 |
|---|---|
| dotnet / node / python | 可找到 |
| go / rustc / cargo | 未找到 |
| tunnel-client / webcodex-tunnel | 未找到 |

“未找到”不等于磁盘任何位置都未安装；不据此安装依赖。NAS 环境完全未检查。本次没有编译与性能结果；Go 方案可在构建环境产出适配 NAS 的制品，不要求 NAS 具有 Go 工具链。

## 6. 实施边界建议

最小核心增量：独立 Bridge Worker、明确管理入口、选择/审批目录。优先不修改现有业务 MCP Server，将它作为普通下游接入。按需增强 Workers 的 Native、autostart、readiness、优雅停止和日志字节上限；这些增强应有聚焦生命周期测试，不能以本次研究文档代替验证。

没有必要先引入 Redis、PostgreSQL、Kubernetes 或新的分布式注册中心。插件私有存储和进程内目录足以满足当前单机目标，后续团队场景再重新审查隔离与身份。
