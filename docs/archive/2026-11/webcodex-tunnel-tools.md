# WebCodex Tunnel：工具发现、执行与 LinuxWebTool 接入映射

[返回调研索引](webcodex-tunnel-research.md) · [连接和传输代码](webcodex-tunnel-connection.md)

参考源码根：`E:\WorkProject\Rust\【参考项目】\webcodex-main`。下文“建议”是基于代码的接入推导，不表示已实现或已通过实连。

## 1. Tunnel 不负责定义业务功能

Tunnel 将 `tools/list`、`tools/call` 等 JSON-RPC 作为 payload 转发给固定 MCP HTTP 服务。决定模型能看见什么、参数如何校验、能否执行以及交给哪个 Runner，均由 WebCodex 的服务端负责。

```text
ChatGPT 发出 JSON-RPC
  → OpenAI Tunnel poll 命令
  → TunnelClient.forward
  → POST 本地 /mcp + 本地 Bearer
  → AuthMiddleware
  → MCP 方法分发
      tools/list → 描述目录 + scope / exposure 过滤
      tools/call → ToolRuntime → 权限/参数/能力检查 → canonical dispatch
  → 需要远程执行的工具交给目标 Runner
  → MCP JSON/SSE → Tunnel response → ChatGPT
```

并非所有工具都需要 Runner：运行状态、目录和发现类功能可以在 Server 层完成。文件、进程等执行能力按工具的目标与权限路由。

## 2. HTTP 入口仍有本地认证

来源：[src/lib.rs:735](<E:/WorkProject/Rust/【参考项目】/webcodex-main/src/lib.rs:735>)：

```rust
Router::with_path(route_metadata::shared_root_path(
    RouteId::McpGet,
    RouteId::McpPost,
))
.hoop(AuthMiddleware)
.get(mcp::mcp_info)
.post(mcp::mcp_post)
```

`src/auth/middleware.rs:283` 附近处理认证，随后仍有路由和工具 scope 检查。Tunnel 不绕过这些检查，只负责提供已经绑定的本地 Bearer。不要把“已知 Tunnel ID”当作访问本地 Server 的充分条件。

`src/server_tunnels.rs:85` 生成 loopback `/mcp` 地址；`:216` 先用 profile 的 `local_token` 探测，`:231` 启动 Tunnel 时绑定同一令牌。传输 ready、本地 MCP ready、ChatGPT 实际成功调用是不同证据。

## 3. tools/list：将工具描述提供给模型

`src/mcp.rs:1499` 将 `tools/list` 交给 `tools::handle_list_with_runtime`。工具清单核心生成段见 [src/mcp/tools.rs:339](<E:/WorkProject/Rust/【参考项目】/webcodex-main/src/mcp/tools.rs:339>)：

```rust
let mut specs = filter_descriptors_for_oauth(
    crate::model_surface::adaptive_runtime_direct_tool_descriptors(),
    auth,
);
specs
    .extend(stateless_advertised_operator_extension_descriptors_for_auth(stateless_2026, auth));
specs.retain(|spec| exposure.allows(&spec.name));

let mut tools = specs
    .into_iter()
    .map(|spec| mcp_tool_descriptor_json(spec, compact, app_enabled))
    .collect::<Vec<_>>();
```

该函数后面还有条件性工具追加；不能把这段摘录理解成所有协议模式下的完整工具清单。关键设计是：统一描述源 → 授权过滤 → surface 曝光过滤 → 协议描述输出。

基础结构在 `crates/webcodex-tool-contracts/src/tool_spec.rs:15`：

```rust
pub struct ToolSpec {
    pub name: String,
    pub description: String,
    #[serde(skip)]
    pub discovery_keywords: &'static [&'static str],
    pub input_schema: Value,
    pub output_schema: Value,
    pub annotations: Value,
}
```

其中 `ToolSpec` 是内部结构，MCP 输出还会经过 descriptor 转换。名称用于匹配调用；description 帮助模型选择工具；schema 约束参数/结果；annotations 表达工具行为特征。功能实现仍需后续执行入口。

示意 MCP 请求，不是本次实际调用：

```json
{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{}}
```

## 4. tools/call：把工具调用交给统一运行时

`src/mcp.rs:1558` 将调用交给 `tools::handle_call`。参数和上下文准备后，进入 [src/mcp/tools/call.rs:624](<E:/WorkProject/Rust/【参考项目】/webcodex-main/src/mcp/tools/call.rs:624>)：

```rust
let outcome = context
    .runtime
    .call_tool_with_invocation_metadata(
        KernelToolCallRequest {
            tool_name: params.name.clone(),
            arguments: params.arguments,
        },
        ToolCallContext {
            transport: ToolTransport::Mcp,
            session_id: session_id.as_deref(),
            auth: context.auth,
            window: context.window,
            record_oauth_scope_denials: false,
            host_file_import_trust: context.host_file_import_trust,
        },
        metadata,
        response.surface.capabilities,
    )
    .await;
```

重点是把 **工具名、参数、认证上下文、transport 和能力上下文** 一起交给运行时。`src/tool_runtime/kernel.rs:495` 进入 `dispatch_canonical`，而不是 MCP 层自行启动进程。发现阶段过滤工具不能代替执行阶段授权。

例如读取文件，源码路径为：

```text
ToolCall::ReadFiles
  → src/tool_runtime/dispatch/routing.rs:333 的路由分支
  → src/tool_runtime/file_tools.rs:21 的 dispatch_file_tool
  → read_files_resolved
  → src/tool_runtime/read_files.rs 中的项目/Runner 身份检查
  → ProjectFileReader（持有 runner_registry）
  → 目标 Runner
```

通用文件操作如何发给 Runner，可读 `src/tool_runtime/files.rs:108` 的 `run_runner_json_file_op`：它构造 `ShellFileOpRequest`，携带 `client_id`、`cwd`、`path`、操作类型和内容等参数，经 `runner_registry.enqueue_file_op` 排队；随后等待回执、检查 error/exit_code、解析 JSON。超时或回执通道丢失有取消处理，不把“已入队”当作执行成功。

## 5. 如何新增功能

| 方式 | 入口 | 适用情况 |
|---|---|---|
| 新增内置工具 | `crates/webcodex-tool-contracts/src/tool_definition.rs` 及分域定义 | 改造 WebCodex 核心能力 |
| 接入本地 MCP provider | 已有 `mcp_tool` 网关 | 复用独立 MCP 服务 |
| Runner native Tool Plugin | 已有 `plugin_tool` 网关 | Runner 上独立部署扩展工具 |

内置工具需要贯通：声明与 Schema → 参数解析 → 模型 surface admission → canonical dispatch → scope/permission/capability → 实际实现与验证。`registry/tool_specs.rs:69` 把描述转为 ToolSpec；仅在工具列表加一条记录不能提供可执行功能。

本地 MCP / Plugin 网关是另一层发现与调用，不代表所有 provider 工具自动成为外层 MCP 的顶级工具。使用既有网关时应先发现、再读取精确 Schema、再调用；不通过猜参数跳过授权或绑定检查。

## 6. 当前 LinuxWebTool 可以复用什么

下面两处已在当前项目源码确认：

1. [McpEndpoints.cs:81](../src/LinuxWebTool.WebHost/Features/Mcp/Endpoints/McpEndpoints.cs)：提供 `POST /mcp`，读取 JSON-RPC，交给 `McpServerEngine.HandleRequestAsync`，返回 JSON。它是独立 Tunnel 客户端应绑定的入口，而非旧式 `/mcp/sse`。
2. [ApiKeyMiddleware.cs:229](../src/LinuxWebTool.WebHost/Shared/Middleware/ApiKeyMiddleware.cs)：除了 `X-Api-Key`，也支持 `Authorization: Bearer lwt_...`。

关键本地鉴权代码：

```csharp
if (context.Request.Headers.TryGetValue("Authorization", out var authHeader)
    && !string.IsNullOrWhiteSpace(authHeader))
{
    var authStr = authHeader.ToString().Trim();
    if (authStr.StartsWith("Bearer lwt_", StringComparison.OrdinalIgnoreCase))
    {
        return authStr["Bearer ".Length..].Trim();
    }
}
```

因此，认证头层面可设置 `MCP_AUTHORIZATION=Bearer <lwt_... Key>`，无需为了 Tunnel 专门增加 `X-Api-Key` 转换代理。Key 必须具有当前项目要求的 MCP 及相关业务权限，不能借用 OpenAI Platform Key。

**建议的最小方案**：先复用独立 `webcodex-tunnel` 程序，将 `MCP_SERVER_URL` 指向 LinuxWebTool 的 `/mcp`，保留现有 MCP 工具实现；通过一次真实 `tools/list` 和只读 `tools/call` 验证后，再决定是否将 Tunnel 客户端生命周期集成到应用。这样不必把 WebCodex 的 Server/Runner 全部移植过来。

这里确认的是请求入口与 Bearer 头兼容，不是完整协议兼容证明。仍需验证 initialize/协议版本协商、notifications、JSON-RPC 错误格式、会话头及终止语义、长耗时调用、超时后副作用和重复请求处理。WebCodex 自己的本地 readiness probe 也不能直接等同于 LinuxWebTool 所需的探测契约。

## 7. 验收顺序与范围

1. 本地使用独立低权限 Key 验证 `/mcp` 的工具列表及一次只读调用。
2. 运行独立客户端的 `doctor` 检查配置，再确认实际 poll 成功；401/403 优先核对控制面 Key / Tunnel 权限。
3. 在 ChatGPT 中绑定同一 Tunnel，验证工具发现和一次真实只读业务调用。
4. 分别观察控制面投递、本地鉴权、MCP 方法、工具执行和结果回传，避免只根据健康绿灯判断成功。

本次仅完成静态步骤追踪和文档编写，上述操作均未执行，未修改任何服务、凭据或运行配置。

## 文件索引

| 文件（相对于参考源码根） | 作用 |
|---|---|
| `src/lib.rs`、`src/auth/middleware.rs` | HTTP MCP 入口和认证 |
| `src/mcp.rs`、`src/mcp/protocol.rs` | MCP 方法及旧协议 / stateless 2026 分支 |
| `src/mcp/tools.rs`、`src/mcp/tools/call.rs` | 工具发现、调用上下文与运行时交接 |
| `crates/webcodex-tool-contracts/src/tool_spec.rs`、`tool_definition.rs` | 统一工具契约和声明入口 |
| `src/tool_runtime/kernel.rs`、`dispatch/routing.rs` | canonical 执行入口和路由 |
| `src/tool_runtime/files.rs`、`read_files.rs` | 文件工具到 Runner 的执行证据 |
| `src/server_tunnels.rs` | 本地 MCP 准备、Tunnel 启动和生命周期观察 |
