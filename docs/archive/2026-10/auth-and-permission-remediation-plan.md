# 认证安全与权限控制全流程整改及防护门禁建设方案

**文件版本**：v1.0  
**制定日期**：2026-10-09  
**方案目标**：彻底修复审计报告（`docs/auth-and-permission-audit-report.md`）指出的全部逻辑安全漏洞与性能瓶颈，并新增自动化架构门禁，防止同类越权或安全防护旁路问题再次出现。

---

## 1. 方案背景与目标

在最新的系统代码审计中，确认了登录认证、API Key 权限矩阵、反向代理受信与长连接调度中存在如下需整改的关键项：
1. **防爆破机制漏洞**：`RecordFailure` 清理条件失效，多 IP 混淆请求可抹除失败计数；
2. **IP 伪造与 DoS 风险**：`GetClientIp` 盲信 `X-Real-IP`，公网直连时存在伪造与管理员封禁风险；
3. **API Key 权限漏斗**：`ApiKeyMiddleware` 对新增的 `EasyTier`、`Groups`、`History DELETE` 默认放行；
4. **WebSocket 安全戳记旁路**：终端直连握手未核验 `SecurityStamp`，改密后旧 Token 存在逃逸通道；
5. **长连接 CPU 空转**：MCP SSE 管道使用 `Task.Delay(100)` 忙轮询；
6. **对象高频分配**：`JsonWebTokenHandler` 未单例化。

### 核心整改原则
- **零破坏性（Zero Regressions）**：完全兼容现有管理后台 UI、API 规范与 Native AOT 编译；
- **默认拒绝（Default Deny）**：权限矩阵必须对未知路由严格拒绝，杜绝未来新增控制器静默泄露；
- **门禁常态化（Guardrails as Code）**：在 `ArchitectureTests` 中新增自动化代码扫描门禁，一旦出现新增控制器未配置权限矩阵，CI/CD 立即拦截构建。

---

## 2. 详细整改实施设计

### 2.1 P0-1：修复 `RecordFailure` 计数清理逻辑 (`AuthController.cs`)

#### 机制设计
在 IP 失败状态元组中引入 `LastAttemptAt`：
`ConcurrentDictionary<string, (int Count, DateTime LockUntil, DateTime LastAttemptAt)>`
- **初始失败**：`(1, DateTime.MinValue, now)`；
- **达到 10 次**：`(0, now.AddMinutes(5), now)`；
- **清理逻辑**：当字典容量超过 100 时：
  - 仅清理锁定已过期记录（`LockUntil > DateTime.MinValue && LockUntil <= now`）；
  - 或超过 10 分钟未再尝试的陈旧失败计数（`LockUntil == DateTime.MinValue && now - LastAttemptAt > TimeSpan.FromMinutes(10)`）；
  - **绝不清理** 10 分钟内活跃的 1~9 次失败计数，确保防爆破锁定不可被冲刷绕过。

---

### 2.2 P0-2：闭环 `ApiKeyMiddleware` 模块控制矩阵 (`ApiKeyMiddleware.cs`)

#### 机制设计
重构 `CheckModulePermission`，实施**全覆盖白名单与严格黑名单**：
1. **系统管理专属（绝对禁止 API Key）**：
   - `/api/Auth`（除允许匿名探测的登录/检查外，改密与续签等一律拦截）；
   - `/api/ApiKeys`（凭据管理专属于管理员）；
   - `/api/FrpTunnel`（公网隧道配置）；
   - `/api/EasyTier`（虚拟组网配置与节点管控，严禁低权限 API Key 触碰）。
2. **指令与终端模块**：
   - `/api/Commands`、`/api/Groups`、`/api/Terminal`：严格要求 `entity.AllowTerminal`。
3. **定时任务模块**：
   - `/api/Schedules`：要求 `entity.AllowSchedules`。
4. **文件与存储模块**：
   - `/api/Files`、`/api/SmbMounts`、`/api/WebDavMounts`、`/api/RcloneMounts`、`/api/MountTasks`：要求 `entity.AllowFiles`。
5. **媒体与网关模块**：
   - `/api/Transcode`：要求 `entity.AllowTranscode`；
   - `/api/Gateway`：要求 `entity.AllowGateway`。
6. **审计历史与日志**：
   - `/api/History`：`DELETE` 请求严禁 API Key 调用；
   - 基础只读查询（`Overview`、`SystemStatus`、`History GET`、`Logs GET`）要求 `entity.AllowApi`。

---

### 2.3 P0-3：WebSocket 终端握手补齐 `SecurityStamp` 校验 (`TerminalEndpoints.cs`)

#### 机制设计
在 `/api/terminal/ws/{sessionId}` 中：
- 当使用 URL Query `?token=...` 握手时，在 `handler.ValidateTokenAsync` 验签成功后，提取 `sub` 与 `stamp` Claim；
- 从 DI 容器获取 `AdminCredentialService`，比对 `string.Equals(sub, adminCred.Account.UserName)` 以及 `adminCred.ValidateSecurityStamp(stamp)`；
- 改密后的旧 Token 尝试直连 WebSocket 时，立即返回 `401 Unauthorized` 拒绝握手。

---

### 2.4 P0-4：`GetClientIp` 引入受信回环/反向代理判定 (`HttpContextExtensions.cs`)

#### 机制设计
- 获取底层连接地址 `var remoteIp = context.Connection.RemoteIpAddress`；
- 仅当连接来源于**回环地址（Loopback）**（如 `127.0.0.1`、`::1`）或常见内网反代 IP 时，才解析 `X-Real-IP` 或 `X-Forwarded-For`；
- 若服务直面外部不可信客户端，直接使用真实的 `remoteIp`，杜绝伪造与 DoS。

---

### 2.5 P1-1：MCP SSE 下行管道迁移至 `System.Threading.Channels` (`McpEndpoints.cs`)

#### 机制设计
- 将 `ConcurrentQueue<string>` 替换为 `Channel<string>`；
- SSE 维持管道使用 `await foreach (var msg in session.Channel.Reader.ReadAllAsync(context.RequestAborted))`；
- 消除 `Task.Delay(100)` 忙轮询，实现微秒级消息即时送达与长连接零 CPU 空转。

---

### 2.6 P1-2：`JsonWebTokenHandler` 单例化 (`JwtIssuer.cs` / `TerminalEndpoints.cs`)

#### 机制设计
- 在 `JwtIssuer` 中声明 `private static readonly JsonWebTokenHandler TokenHandler = new();`；
- 所有 Token 生成（`CreateToken`）与解析校验（`ReadJsonWebToken` / `ValidateTokenAsync`）共享该线程安全实例，降低 GC 压力。

---

### 2.7 P1-3：API Key 快速前缀格式预检 (`ApiKeyService.cs`)

#### 机制设计
- `ValidateAsync` 先行校验格式：必须以 `lwt_live_` 开头且总长度为 41 字符；
- 非法格式直接返回 `null`，不触发哈希计算与 SQLite 查库，防范缓存穿透。

---

## 3. 防护门禁建设规范 (Architecture Gates)

为避免未来因新增控制器或修改中间件导致同类越权问题复发，新增以下自动化质量门禁：

### 3.1 架构门禁：`LinuxArch024_所有控制器必须在ApiKeyMiddleware中显式声明权限归属`
在 `LinuxWebTool.ArchitectureTests` 中：
- 动态扫描 `src/LinuxWebTool.WebHost` 下的所有 `*Controller.cs`；
- 读取 `ApiKeyMiddleware.cs` 的源码文本；
- 校验每一个控制器对应的基础路由（如 `/api/EasyTier`、`/api/Groups` 等）必须在 `ApiKeyMiddleware.CheckModulePermission` 中显式出现，**严禁任何控制器落入隐式默认规则**。
- 若未来开发者新增了控制器（如 `NewModuleController`）但未同步更新 `ApiKeyMiddleware`，`dotnet test` 架构门禁将直接失败阻止提交。

### 3.2 功能集成门禁：针对性端到端测试用例
在 `LinuxWebTool.IntegrationTests` 中增加自动化测试：
1. **防爆破计数保护测试**：模拟 100+ 随机 IP 请求，验证目标 IP 的失败计数未被抹除并正常触发 429 锁定；
2. **API Key 越权拦截测试**：验证仅拥有 `AllowApi: true` 的 Key 尝试访问 `/api/EasyTier` 或执行 `DELETE /api/History` 时均返回 `403 Forbidden`；
3. **WebSocket 改密吊销测试**：验证管理员修改密码后，旧 Token 无法直连 WebSocket PTY 握手。
