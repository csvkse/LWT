# WebCodex Tunnel：连接、凭据与传输核心代码

[返回调研索引](webcodex-tunnel-research.md)

本文所有 WebCodex 路径均相对于 `E:\WorkProject\Rust\【参考项目】\webcodex-main`。本次为源码调研，不包含实连验证。

## 1. ID、Key 和本地令牌分别做什么

| 配置 | 用途 | 使用位置 |
|---|---|---|
| `CONTROL_PLANE_TUNNEL_ID` | 定位已有 OpenAI Tunnel | `/v1/tunnels/{id}/poll` 和 `/response` |
| `CONTROL_PLANE_API_KEY` | 认证本机客户端访问 OpenAI 控制面 | 发往控制面的 `Authorization: Bearer ...` |
| `CONTROL_PLANE_BASE_URL` | 独立 CLI 的控制面地址，默认 `https://api.openai.com` | 控制面客户端；WebCodex 嵌入适配器固定使用该官方地址 |
| `MCP_SERVER_URL` | 独立 CLI 的固定 MCP HTTP 目标 | 本地转发请求 |
| `MCP_AUTHORIZATION` | 独立 CLI 的完整本地 Authorization 头 | 如 `Bearer <local-key>`；不复用控制面 Key |
| WebCodex `local_token` | WebCodex 适配器使用的本地 MCP 凭据 | 传入 `FixedMcpTarget`，与控制面凭据独立 |

配置契约：`crates/webcodex-openai-tunnel/src/bin/webcodex-tunnel/config.rs:28`。
安装文档 `docs/desktop-install.zh-CN.md:48`、`:121` 说明：在 OpenAI Platform 的 Tunnels 页面创建资源，另建具备 Tunnels Read + Use 权限的 API Key。项目不在建立连接时自行创建 Tunnel。

ChatGPT 侧操作按参考文档 `docs/desktop-install.zh-CN.md:306`：添加自定义 MCP → 连接方式 Tunnel → 选择或输入同一个完整 ID → 个人接入选择 No Auth → 创建并安装。网页不填写 Tunnel API Key。这里的 No Auth 只表示该个人连接不另配终端用户 OAuth，本机到控制面和本机到 MCP 仍有认证；不能直接据此建立团队成员隔离。

## 2. 核心构造：固定两组身份与目标

来源：[src/project_entry_openai_tunnel.rs:98](<E:/WorkProject/Rust/【参考项目】/webcodex-main/src/project_entry_openai_tunnel.rs:98>)。以下为构造段摘录，省略 surrounding 错误处理：

```rust
let client = TunnelClient::new_with_proxy(
    ControlPlaneIdentity::new(
        CONTROL_PLANE,
        &prerequisites.tunnel_id,
        Credential::bearer(&prerequisites.credential).map_err(tunnel_error)?,
    )
    .map_err(tunnel_error)?,
    FixedMcpTarget::new(
        &mcp_url,
        Credential::bearer(local_token).map_err(tunnel_error)?,
    )
    .map_err(tunnel_error)?,
    DeadlinePolicy::RequireFinite {
        max_duration: Duration::from_secs(120),
    },
    Limits::default(),
    prerequisites.proxy.clone(),
)
.map_err(tunnel_error)?;
```

`CONTROL_PLANE` 在该文件第 14 行固定为 `https://api.openai.com`。`launch` 在第 77 行附近创建任务，并通过 oneshot 接收停止信号。持久模式进一步启用 `with_restart_recovery`，不是每次启动都无条件重放命令。

来源：[lib.rs:156](<E:/WorkProject/Rust/【参考项目】/webcodex-main/crates/webcodex-openai-tunnel/src/lib.rs:156>)：

```rust
let mut headers = reqwest::header::HeaderMap::new();
headers.insert(AUTHORIZATION, cp.credential.0.clone());
// 还设置 X-Tunnel-Client-* 和 X-Tunnel-MCP-Server-Info。
// 控制面 client 通过 .default_headers(headers) 绑定这些头。
```

其中 Wire Protocol Version 为 `2026-08-25`，MCP Server Info 声明 `main` channel。控制面 Client 使用所选代理策略；本地 MCP Client 独立创建并 `.no_proxy()`。两者禁止重定向和 HTTP 库自动重试，避免凭据被带向另一个目标。

库层允许的 Tunnel ID 是受限字符字符串（`lib.rs:56`）；WebCodex 产品适配器更严格，要求 `tunnel_` 后接 32 位小写十六进制字符（`src/project_entry_openai_tunnel.rs:331`）。不要混淆库层和产品层校验。

## 3. 拉取命令：GET poll

来源：[control_plane.rs:54](<E:/WorkProject/Rust/【参考项目】/webcodex-main/crates/webcodex-openai-tunnel/src/control_plane.rs:54>)。核心摘录：

```rust
pub(crate) fn endpoint(&self, operation: &str) -> String {
    format!(
        "{}v1/tunnels/{}/{operation}",
        self.cp.origin, self.cp.tunnel
    )
}
// poll 内：
let timeout_ms = if startup { "1000" } else { "15000" };
let response = self.control
    .get(self.endpoint("poll"))
    .query(&[
        ("limit", limit.min(25).to_string()),
        ("timeout_ms", timeout_ms.into()),
    ])
    .timeout(self.limits.poll_timeout)
    .send().await
    .map_err(|_| Error::Transport)?;
```

等价请求形状如下，所有尖括号内容都是占位符：

```http
GET /v1/tunnels/<tunnel-id>/poll?limit=8&timeout_ms=15000
Host: api.openai.com
Authorization: Bearer <control-plane-api-key>
X-Tunnel-Client-Wire-Protocol-Version: 2026-08-25
X-Tunnel-MCP-Server-Info: {"version":1,"channels":[{"name":"main"}]}
```

200 解码 `commands`，204 表示无命令；401/403 终止认证失败，408/429/5xx 等按受限退避策略处理。首次 poll 的 1 秒参数用于缩短首次就绪等待，并不跳过真实响应验证。

命令结构位于 `wire.rs:9`，核心字段为：

```json
{
  "commands": [{
    "request_id": "example-request",
    "shard_token": "<opaque-shard-token>",
    "command_type": "jsonrpc",
    "channel": "main",
    "headers": {},
    "jsonrpc": {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
  }]
}
```

这是精简示意，不是完整抓包；还可包含 `response_timeout`、`created_at`。外层 `request_id` 用于传输关联/去重，内层 JSON-RPC `id` 用于 MCP 响应匹配，`shard_token` 只用于回传响应，三者不能互换。超时策略见 `deadline.rs`、`policy.rs`。

## 4. 转发本地 MCP

来源：[mcp_http.rs:36](<E:/WorkProject/Rust/【参考项目】/webcodex-main/crates/webcodex-openai-tunnel/src/mcp_http.rs:36>)，连续核心摘录：

```rust
let headers = request_headers(command)?;
let termination = command.command_type == "session_termination";
let mut request = if termination {
    self.mcp.delete(self.target.url.clone())
} else {
    self.mcp.post(self.target.url.clone())
};
request = request
    .headers(headers)
    .header("accept", "application/json, text/event-stream");
if let Some(credential) = &self.target.credential {
    request = request.header("authorization", credential.0.clone());
}
if !termination {
    request = request
        .header("content-type", "application/json")
        .body(serde_json::to_vec(&command.jsonrpc).map_err(|_| Error::Protocol)?);
}
```

普通 JSON-RPC 发 POST；会话终止发 DELETE。该客户端连接一个固定 Streamable HTTP 地址，不是旧式 `/mcp/sse` 握手后再访问 `/mcp/message` 的客户端。响应支持 JSON 或 POST 响应中的 SSE，SSE 解析也在 `mcp_http.rs`。

`command.rs:7` 只允许有限协议/上下文头通过；来自命令的 Authorization、Cookie 等覆盖尝试被拒绝。`X-OpenAI-Subject` 等上下文头不替代已绑定的本地凭据。

## 5. 回传 OpenAI：POST response

来源：[response.rs:67](<E:/WorkProject/Rust/【参考项目】/webcodex-main/crates/webcodex-openai-tunnel/src/response.rs:67>)：

```rust
let result = self.control
    .post(self.endpoint("response"))
    .header("X-Tunnel-Shard-Token", &command.shard_token)
    .header("content-type", "application/json")
    .body(body.clone())
    .timeout(self.limits.poll_timeout)
    .send().await;
```

`body` 按 `wire.rs:36` 序列化，包含 `request_id`、`channel`、`resp_json`、`resp_headers`、`resp_code` 和 `resp_type`；普通结果使用 `jsonrpc_response`。本地 SSE 通知可通过 `jsonrpc_notify` 回传，最终结果再独立回传。

200 表示回传成功；404 被视为无需继续投递的终态，不表示业务执行成功。最终响应重试复用同一个 body 和关联信息，不重新调用本地 MCP；通知仅对 429 重试，网络失败存在投递歧义时不盲目重发。

## 6. 为什么不能只复制 poll / response

- `command.rs:76` 校验命令类型、channel 和 JSON-RPC 结构；仅接受 `main`。
- `poller.rs` 保留请求收据并控制并发；`command.rs:117` 的执行入口在本地转发前处理 deadline、持久 admission 和重复请求抑制。
- `run_fence.rs` / `run_receipts.rs` 提供持久运行所有权和收据日志。WebCodex 的持久适配器启用跨重启重复请求抑制，但不宣称分布式 exactly-once；换一个 request ID 的重试是另一个操作。
- 默认并发 8、突发入口 128、body 上限 4 MiB、响应投递最多 3 次（`policy.rs:53`）。不是无限队列或无限重试。
- WebCodex 先验证本地 MCP 可用再启动 Tunnel（`src/server_tunnels.rs:200` 附近），健康状态分开记录 `local_mcp_ready` 与 `tunnel_ready`。
- 停止时先停止 Tunnel admission 并 drain，再停止本地 HTTP；失败或结果未知时保留恢复栅栏，不能通过删除状态文件假装任务未执行。

## 7. 最小复用入口

独立 `webcodex-tunnel` 不依赖 WebCodex Server / Runner，也不依赖 Go。参考项目根目录可构建：

```text
cargo build --locked --profile dogfood -p webcodex-openai-tunnel --bin webcodex-tunnel
```

准备以下进程环境配置，值仅为示意，不要把真实 Key 放入文档或命令行参数：

```text
CONTROL_PLANE_TUNNEL_ID=<OpenAI 颁发的完整 Tunnel ID>
CONTROL_PLANE_API_KEY=<具备该 Tunnel 权限的 Platform API Key>
MCP_SERVER_URL=http://127.0.0.1:5270/mcp
MCP_AUTHORIZATION=Bearer <独立的本地 MCP API Key>
```

然后运行 `webcodex-tunnel doctor`、`webcodex-tunnel run --json`。`doctor` 只做本地配置校验，不联网、不验证权限；`status --json` 只观察已有健康监听器。以上命令本次未执行。默认健康监听地址为 `127.0.0.1:8080`，冲突时可用 `--health.listen-addr 127.0.0.1:0`。

CLI 支持 `--control-plane.api-key env:NAME` / `file:PATH` 和 `--mcp.authorization env:NAME` / `file:PATH`；后者内容是完整 Authorization 值。字面密钥参数会被拒绝；不会回退使用无关的 `OPENAI_API_KEY`。嵌入库与独立 CLI 的 deadline 默认策略不同：WebCodex 适配器上限 120 秒，独立 CLI 默认 ProtocolCompatible。

## 文件索引

| 文件 | 重点 |
|---|---|
| `apps/desktop/src-tauri/src/tunnel_config.rs` | Desktop profile 保存、只写 Key、环境变量传递；持久 Environment 的目录权威另见下行 |
| `docs/architecture/server-owned-tunnels.md` | EnvironmentStore 唯一持久 profile 目录、standalone/embedded 生命周期 |
| `src/project_entry_openai_tunnel.rs` | 固定控制面和本地令牌绑定、启动停止、恢复策略 |
| `crates/webcodex-openai-tunnel/src/{lib,control_plane,wire}.rs` | 身份、HTTP Client、poll 与协议结构 |
| `crates/webcodex-openai-tunnel/src/{command,mcp_http,response}.rs` | 校验、一次转发、结果投递 |
| `crates/webcodex-openai-tunnel/README.md` | 支持边界、完整配置、重放保护与恢复说明 |
