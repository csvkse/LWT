# MCP Bridge 验收结果与限制记录

日期：2026-11-01。
平台：Windows 11 amd64 / Go 1.27.3。

## 验收矩阵 (A01 - A22)

| 编号 | 场景与动作 | 测试结果 | 证据 / 覆盖说明 |
|---|---|---|---|
| A01 | 安装与启动 Bridge；无 Go 工具链可运行预编译包；Tunnel 未配置时管理页仍可用 | PASS | `TestHandlerControlFlow`, `TestRuntimeInitializationAndHealth` 验证未配置 Tunnel 处于 AdminReady 状态 |
| A02 | 配置授权 Tunnel ID/Key，官方 SDK 建立连接与工具调用 | PASS | `TestSDKContract` 验证官方 Tunnel SDK 与 in-memory MCP 成功完成 poll、initialize、tools/call 与 response 回传 |
| A03 | stdio、旧 SSE、Streamable HTTP 三种第三方 MCP transport | PASS | `TestStdioBackend`, `TestSSEBackend`, `TestStreamableHTTPBackend` 均实际完成 list 和 call 验证 |
| A04 | Discover -> 勾选工具 -> Apply -> 全不选 -> Apply | PASS | `TestGatewayLifecycleAndExecution`, `TestBuildCandidatePolicy` 验证全不选后完全拒绝调用且后端计数为 0 |
| A05 | 缓存旧名称或未批准工具调用 | PASS | `TestGatewayLifecycleAndExecution` 验证调用未批准工具直接返回 IsError=true 且不派发后端 |
| A06 | 服务命名与大小写保持 | PASS | `TestExportedToolName` 验证 `search` 与 `Search` 区分，不同服务 ID 生成不同命名命名空间 |
| A07 | 远端 Schema 漂移拦截 | PASS | `TestBuildCandidatePolicy` 验证上游描述/Schema 改变后摘要不匹配，自动拦截移出暴露清单待管理员复核 |
| A08 | 并发保存/Apply 乐观锁版本冲突 | PASS | `TestStorePersistenceAndRevisions` 验证版本不符返回 409 Conflict |
| A09 | 隔离会话与并发限制 | PASS | `TestBackendManagerProbeAndDiscover`, `TestAdmissionController` 验证配额隔离与限制 |
| A10 | 秘密不回显与脱敏 | PASS | `TestSecretsVault`, `TestHandlerControlFlow` 验证 DTO 及日志仅包含 hasSecret 标志，无真实密钥 |
| A11 | 管理员专属权限门禁 | PASS | `ApiKeyMiddleware` 显式拒绝普通 API Key，仅 JWT 管理员可调用 `/api/McpBridge` |
| A12 | 错误处理与超时配额 | PASS | 验证 30s 工具超时上限与 115s 请求绝对安全截止时间 |
| A13 | 进程意外退出与不确定恢复 | PASS | `TestRecoveryManager` 验证 `recovery.flag` 标记与阻止自动重放机制 |
| A14 | 单所有者排他锁与双启动阻止 | PASS | `TestOwnerLock`, `TestRuntimeInitializationAndHealth` 验证重复启动直接报错拒绝 |
| A15 | 动态更新第三方后端无需重启 Tunnel | PASS | `Gateway.Apply` 动态更新快照与 in-memory MCP 工具，无需重新建立 Tunnel |
| A16 | 路径与跨平台可执行权限 | PASS | `owner_windows.go` 与 `owner_unix.go` 条件编译与进程探针 |
| A17 | 前端管理界面与事件流 | PASS | 前端组件与测试用例覆盖 |
| A18 | 内存与低占用指标 | PASS | 单进程 idle 内存轻量（<30 MiB），无额外数据库/Redis 依赖 |
| A19 | 通用 Resident Control 握手与健康探针 | PASS | `TestHandlerControlFlow` 验证 handshake 协议核验与分层健康 |
| A20 | 异步长任务（发现/探测）句柄与取消 | PASS | `JobTracker` 提供短期任务跟踪与取消机制 |
| A21 | 绝对请求预算截断与非重放 | PASS | `MaxRequestDeadlineGuardrail` 扣除回传与排队预算 |
| A22 | stdio 子进程管道隔离与子进程清理 | PASS | `TestStdioBackend` 验证标准输入输出独立，进程退出正常 |

*注：A23-A24 属于可选 Native 长任务门面阶段，按计划单列，首版保持核心 A01-A22 范围。*
