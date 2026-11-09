# APIKey 授权体系与细粒度权限矩阵方案

## 1. 目标与背景

`LinuxWebTool` 原有认证机制仅支持单管理员账密 + JWT 方案，主要面向浏览器交互，无法满足以下场景诉求：
1. **AI Agent / MCP 客户端无感鉴权**：Cursor、Claude Desktop 等工具需要稳定长效且可独立吊销的接入凭据。
2. **外部自动化与脚本调用 (M2M)**：第三方监控脚本、CI/CD 流水线需要调用特定 API，不宜直接交付管理员登录凭据。
3. **细粒度权限安全管控**：不同调用方应遵循最小权限原则（例如：监控 Agent 只读存储，转码任务机器人只允许提交转码，禁止操作终端和执行指令）。

借鉴参考工程 `ProxyYARP`（`E:\WorkProject\C#\【个人项目】\命令行\ProxyYARP`）的密钥管控架构，设计一套高安全性、零性能开销、支持**通道（Channel）**与**业务模块（Module）**双维度的 APIKey 授权管控体系。

---

## 2. 权限维度与控制矩阵

APIKey 权限分为两层控制：**访问通道控制** 与 **业务功能模块控制**。

### 2.1 维度定义

```
                                  ┌───────────────────────────────┐
                                  │       API Key 鉴权入口        │
                                  └──────────────┬────────────────┘
                                                 │
                  ┌──────────────────────────────┴──────────────────────────────┐
                  ▼                                                             ▼
    【通道维度: 通信协议开关】                                      【模块维度: 业务能力开关】
    ┌───────────────────────────────┐                             ┌───────────────────────────────┐
    │ 1. AllowApi  (允许 REST API)  │                             │ 3. AllowTerminal   (终端执行) │
    │ 2. AllowMcp  (允许 MCP 协议)  │                             │ 4. AllowSchedules  (定时任务) │
    └───────────────────────────────┘                             │ 5. AllowFiles      (文件管理) │
                                                                  │ 6. AllowTranscode  (媒体转码) │
                                                                  └───────────────────────────────┘
```

### 2.2 权限组合矩阵与访问决断表

| 请求目标 | 所需通道权限 | 所需业务模块权限 | 权限未命中时响应行为 |
|---|---|---|---|
| `/api/Commands/*`<br>`/api/Terminal/*` | `AllowApi = true` | `AllowTerminal = true` | HTTP 403 Forbidden (`模块或通道未授权`) |
| `/api/Schedules/*` | `AllowApi = true` | `AllowSchedules = true` | HTTP 403 Forbidden |
| `/api/Files/*`<br>`/api/SmbMounts/*`<br>`/api/WebDavMounts/*` | `AllowApi = true` | `AllowFiles = true` | HTTP 403 Forbidden |
| `/api/Transcode/*` | `AllowApi = true` | `AllowTranscode = true` | HTTP 403 Forbidden |
| `/mcp/sse`<br>`/mcp/message` 握手 | `AllowMcp = true` | 任意模块有效即可 | HTTP 403 Forbidden (`拒绝建立 MCP 会话`) |
| MCP `tools/list` | `AllowMcp = true` | **动态投影过滤** | 仅下发该 Key 具备对应模块权限的工具清单 |
| MCP `tools/call` (`terminal_*`) | `AllowMcp = true` | `AllowTerminal = true` | JSON-RPC Error (`Permission Denied: Terminal`) |
| MCP `tools/call` (`schedule_*`) | `AllowMcp = true` | `AllowSchedules = true` | JSON-RPC Error (`Permission Denied: Schedule`) |
| MCP `tools/call` (`file_*`) | `AllowMcp = true` | `AllowFiles = true` | JSON-RPC Error (`Permission Denied: Files`) |
| MCP `tools/call` (`transcode_*`)| `AllowMcp = true` | `AllowTranscode = true` | JSON-RPC Error (`Permission Denied: Transcode`) |

> **MCP 动态投影过滤设计（Dynamic Projection）**：
> 当 AI 客户端请求 `tools/list` 时，系统依据该 Key 的模块权限进行动态过滤。若某个 Key 仅开启了 `AllowFiles`，则返回给大模型的工具列表中**完全不会出现**终端或定时任务工具。该机制不仅从根本上杜绝了越权风险，还能大幅节省 LLM 上下文 Prompt 消耗，降低幻觉概率。

---

## 3. 密码学安全与生成规范

1. **密钥格式标准**：
   采用标准前缀 + 高熵随机串：`lwt_live_<32 位密码学安全 16 进制字符串>`。
   例如：`lwt_live_9f81a7b3c4d2e0f1883716a5c2d3e4f5`。
2. **安全存储机制（一次性展示原则）**：
   - 生成时通过 `RandomNumberGenerator` 生成 128 位安全随机熵。
   - 数据库中**严禁明文保存完整 Key**。仅存储：
     - `KeyPrefix`（前 12 位，如 `lwt_live_9f81`，用于列表辨识）。
     - `KeyHash`（完整 Key 的加盐 SHA-256 哈希值，用于匹配认证）。
   - 完整明文 Key **仅在创建成功的瞬间向管理员展示一次**，之后任何接口无法再次读取明文。
3. **微秒级内存鉴权缓存 (In-Memory Key Cache)**：
   为避免高频 API 或 SSE 长连接每个数据包都产生 SQLite 磁盘 IO 查询，在 `ApiKeyService` 中维持基于 `ConcurrentDictionary<string, ApiKeyEntity>` 的高效哈希缓存。在 Key 发生更新、禁用或删除时触发局部无效化。

---

## 4. 数据库持久化结构 (SQLite DDL)

在 `DbSetup.cs` 中增加表 `api_key`：

```sql
CREATE TABLE IF NOT EXISTS api_key (
  Id TEXT PRIMARY KEY,
  Name TEXT NOT NULL,
  KeyPrefix TEXT NOT NULL,
  KeyHash TEXT NOT NULL UNIQUE,
  IsEnabled INTEGER NOT NULL DEFAULT 1,
  AllowApi INTEGER NOT NULL DEFAULT 1,
  AllowMcp INTEGER NOT NULL DEFAULT 1,
  AllowTerminal INTEGER NOT NULL DEFAULT 0,
  AllowSchedules INTEGER NOT NULL DEFAULT 0,
  AllowFiles INTEGER NOT NULL DEFAULT 0,
  AllowTranscode INTEGER NOT NULL DEFAULT 0,
  CreatedAt TEXT NOT NULL,
  LastUsedAt TEXT,
  ExpiresAt TEXT
);

CREATE INDEX IF NOT EXISTS idx_api_key_hash ON api_key(KeyHash);
```

---

## 5. 双轨认证管道与拦截器实现

### 5.1 凭据提取顺序 (Credentials Extraction)

中间件支持通过以下途径携带 APIKey（按优先级）：
1. **HTTP 请求头**：`X-Api-Key: lwt_live_...`
2. **Authorization 请求头**：`Authorization: Bearer lwt_live_...`
3. **URL Query 参数**：`?apiKey=lwt_live_...` 或 `?key=lwt_live_...`（针对不支持自定义请求头的浏览器原生 `EventSource` 与 WebSocket 握手端点）。

### 5.2 认证中间件流水线 (ApiKeyMiddleware)

```csharp
public class ApiKeyMiddleware(RequestDelegate next, ILogger<ApiKeyMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ApiKeyService keyService)
    {
        // 1. 若当前用户已通过管理员 JWT 登录，具备超级权限，直接放行
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            await next(context);
            return;
        }

        var path = context.Request.Path.Value ?? "";
        var key = ExtractKey(context);

        // 静态资源、登录页、健康检查端点直接放行
        if (IsPublicRoute(path))
        {
            await next(context);
            return;
        }

        // 2. 检查未携带凭据的受保护接口
        if (string.IsNullOrWhiteSpace(key))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new MessageResponse("需要身份认证：请提供有效的 API Key 或登录管理员"));
            return;
        }

        // 3. 校验 APIKey 是否有效、未禁用、未过期
        var entity = await keyService.ValidateAsync(key);
        if (entity is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new MessageResponse("无效、已禁用或已过期的 API Key"));
            return;
        }

        // 4. 通道拦截判断
        var isMcpRoute = path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase);
        var isApiRoute = path.StartsWith("/api", StringComparison.OrdinalIgnoreCase);

        if (isMcpRoute && !entity.AllowMcp)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new MessageResponse("该 API Key 未被授予访问 MCP 协议通道权限"));
            return;
        }

        if (isApiRoute && !entity.AllowApi)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new MessageResponse("该 API Key 未被授予访问 REST API 通道权限"));
            return;
        }

        // 5. 模块级权限拦截（若为 REST API 请求）
        if (isApiRoute && !CheckModulePermission(path, entity))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new MessageResponse("该 API Key 对所请求的业务模块无权访问"));
            return;
        }

        // 注入当前已鉴权凭据至上下文
        context.Items["CurrentApiKey"] = entity;
        await next(context);

        // 异步更新活跃时间戳（批处理削峰）
        keyService.TouchLastUsed(entity.Id);
    }
}
```

---

## 6. REST API 设计规范

控制器：`LinuxWebTool.WebHost.Routes.ApiKeysController`，基础路由 `/api/ApiKeys`。

| 路径 | 方法 | 鉴权要求 | 说明 |
|---|---|---|---|
| `/api/ApiKeys` | `GET` | 管理员 | 获取所有 APIKey 列表（脱敏） |
| `/api/ApiKeys` | `POST` | 管理员 | 创建新 APIKey，返回包含明文的单次创建结果 |
| `/api/ApiKeys/{id}` | `PUT` | 管理员 | 更新指定 APIKey 的名称、权限矩阵、启用状态 |
| `/api/ApiKeys/{id}` | `DELETE` | 管理员 | 永久删除并吊销指定 APIKey |
| `/api/ApiKeys/{id}/Toggle` | `PATCH` | 管理员 | 快速切换启用/禁用状态 |

### 核心 DTO 契约 (Contracts)

```csharp
public sealed record CreateApiKeyRequest(
    string Name,
    bool AllowApi,
    bool AllowMcp,
    bool AllowTerminal,
    bool AllowSchedules,
    bool AllowFiles,
    bool AllowTranscode,
    DateTime? ExpiresAt);

public sealed record ApiKeyCreatedResponse(
    string Id,
    string Name,
    string RawKey, // 仅此一次返回完整明文
    string KeyPrefix,
    bool AllowApi,
    bool AllowMcp,
    bool AllowTerminal,
    bool AllowSchedules,
    bool AllowFiles,
    bool AllowTranscode,
    DateTime CreatedAt);

public sealed record ApiKeyItemResponse(
    string Id,
    string Name,
    string KeyPrefix,
    bool IsEnabled,
    bool AllowApi,
    bool AllowMcp,
    bool AllowTerminal,
    bool AllowSchedules,
    bool AllowFiles,
    bool AllowTranscode,
    DateTime CreatedAt,
    DateTime? LastUsedAt,
    DateTime? ExpiresAt);
```

---

## 7. 前端密钥管理视图设计 (ApiKeysView.js)

1. **路由挂载**：在 `router/index.js` 中增加路由 `/keys`，导航栏增加 **「API 密钥」**（图标：🔑）。
2. **列表卡片大盘**：
   - 表头包含：名称、密钥前缀、授权通道标签（`API`、`MCP`）、模块权限标签组（`终端`、`定时任务`、`文件`、`转码`）、状态开关（即时 Toggle）、最后活跃时间、操作列（编辑权限、删除）。
3. **新建密钥向导弹窗**：
   - **基础信息**：名称（如 `Cursor 编程助手`、`NAS 自动转码脚本`）。
   - **通道授权区**：
     - [ ] 允许 REST API 调用 (`AllowApi`)
     - [ ] 允许 MCP 服务调用 (`AllowMcp`)
   - **模块授权矩阵区**：
     - [ ] 终端与执行 (`AllowTerminal`)
     - [ ] 定时调度 (`AllowSchedules`)
     - [ ] 文件浏览与读写 (`AllowFiles`)
     - [ ] 媒体转码 (`AllowTranscode`)
   - **有效期设置**：永久有效 / 30天 / 90天 / 自定义。
4. **生成成功凭据展示窗 (One-Time Credential Modal)**：
   - 醒目警示：「请立即复制并保存该 API 密钥，离开本页面后将无法再次查看！」。
   - 明文 Key 展示框，配合一键复制按钮。
   - **内置一键配置生成器**：
     - 切换 Tab 显示 Cursor `mcp.json`、Claude Desktop `claude_desktop_config.json` 或 `curl` 调用示例，自动填充该 Key 与当前服务地址，提供极佳的开发者体验。
