# SDK 兼容性与协议保证记录

## 1. 锁定的 SDK 与 Toolchain

| 组件 | 版本 / 标识 | 许可证 | 作用 |
|---|---|---|---|
| Go Toolchain | `go 1.27.3` (`go.mod` 要求 `go 1.27`) | BSD-3-Clause | 编译构建运行时 |
| `github.com/openai/tunnel-client` | `v0.0.19` | Apache-2.0 | OpenAI Secure MCP Tunnel 客户端协议对接 |
| `github.com/modelcontextprotocol/go-sdk` | `v1.7.0` | MIT | 官方 MCP Server 与 Client 实现，动态工具支持 |

## 2. 协议支持矩阵与实证保证

- **前端 Server 协议**: MCP 2024-11-05+ 标准。支持 `tools/list` 动态注册与快照路由、`tools/call` 执行；支持 content 数组、`structuredContent`、`isError` 与 `_meta`。
- **后端 Client 协议**:
  1. `stdio`: 标准输入输出子进程，环境变量最小隔离，argv 结构化传递，禁止拼接 shell。
  2. `streamable-http`: 标准 HTTP POST MCP 端点。
  3. `sse` (HTTP+SSE): GET 建立 SSE 流，POST 发送 JSON-RPC 消息。
- **Tunnel 控制面协议**:
  - `GET /v1/tunnels/{tunnel_id}/poll` 长轮询接收命令。
  - `POST /v1/tunnels/{tunnel_id}/response` 回传执行结果。
  - 认证: `Authorization: Bearer <TUNNEL_KEY>`。
  - 请求上限预算: 120 秒硬超时（扣除回传与排队预算后，单次工具调用分配 30 秒预算）。

## 3. 约束与单进程单所有者行为

- **单进程单 Owner**: 同一 Tunnel ID 仅允许一个 Bridge 实例持有 `owner.lock`。重复启动直接阻止，避免双重轮询消费。
- **崩溃与恢复**: 若发生未决 (In-Flight) 调用或异常退出，状态机进入 `RecoveryRequired`，阻止自动重放写操作。
- **无旧插件兼容**: 纯粹新契约架构，不引入遗留格式或向下兼容分支。
