# 插件中心网关鉴权穿透、状态过滤置顶、脚本执行容错与降级治理研发方案

## 一、 用户面临的问题场景与根因分析

在 LinuxWebTool 插件中心的日常使用与开发调试中，反馈了以下四大问题：

### 1. MCP Bridge 打开面板提示微服务访问受限 (401)
- **现象**：在插件卡片点击“访问微服务网页/接口”或打开 Webview 弹窗时，页面显示：
  `{"message":"微服务访问受限：需要身份认证，请登录管理员账号或提供已授权的 API Key"}`。
- **根因分析**：
  - 前端单页应用（SPA）登录后，仅将 JWT 保存在 `localStorage.getItem('lwt_token')` 中，并在后续异步 API 请求时通过请求头 `Authorization: Bearer <token>` 发送；
  - 但当通过 `<iframe>` 加载微服务 Webview 或通过 `window.open` 在新标签页打开 `/plugins/mcp-bridge` 时，浏览器发起的是原生 HTML 文档 GET 请求，**无法由前端注入 Authorization 请求头**，仅能携带 Cookie 或 URL Query 参数；
  - 后端网关鉴权决策器 `PluginAuthGatewayEvaluator.cs` 虽已支持提取 Cookie `lwt_token`，但后端的 `AuthController.Login` 与 `Renew` 在签发 Token 时**从未向客户端 Response.Cookies 注入该 Cookie**，前端亦未在 `store/auth.js` 中镜像设置 `document.cookie`；
  - 最终导致网关未检测到任何凭据，直接判定为未认证访客并返回 HTTP 401。

### 2. 缺少扩展状态快速过滤与默认置顶排序
- **现象**：插件列表仅支持 Profile（常驻/一次性）与业务分类、关键词过滤，缺少按运行状态（运行中、降级异常、缺少环境/依赖、已停止）维度的快速过滤；卡片列表默认无序，运行中的核心微服务容易被埋在下方。
- **根因分析**：
  - 前端 `pluginFilterStrategies.js` 仅实现了 `ProfileFilterStrategy`、`CategoryFilterStrategy` 与 `KeywordFilterStrategy`，缺少 `StateFilterStrategy`；
  - 列表渲染未按照状态机的健康度与活跃度进行排序，缺乏类似 Docker Desktop / Portainer 的默认优先排序算法。

### 3. 脚本轻量扩展点击启动提示“请求失败（400）”
- **现象**：在“脚本轻量扩展”卡片点击“启动服务”时，前端弹出冷冰冰的错误横幅：
  `请求失败（400）`。
- **根因分析**：
  - 脚本扩展 `caddy-server-manager` 的 Hook 脚本（`hooks/start.js`、`stop.js`、`restart.js`）硬编码执行：
    `execSync('systemctl start caddy || caddy start --config /etc/caddy/Caddyfile', { timeout: 8000 })`；
  - 在 Docker 容器或无 systemd 的 Linux 环境中，`systemctl` 进程会无限阻塞挂起直至 8 秒超时，Node.js 抛出 `ETIMEDOUT` 异常并以退出码 1 退出，未能走到 `|| caddy start` 降级逻辑；
  - 后端 `PluginsController.ExecuteAction` 将退出码非 0 直接判定为 HTTP 400（`BadRequest(result)`）；
  - 前端通用的 `api/client.js` 在接收到 HTTP 400 时弹出通用 Toast `请求失败（400）`，且未能把响应体中真实的错误原因（`启动异常: spawnSync /bin/sh ETIMEDOUT`）展示给用户。

### 4. 降级应用（如 `demo-sh-daemon`）未透出原因、误显启动且无法手动停止
- **现象**：
  - `demo-sh-daemon`（Shell 轮询状态采集守护）显示为“降级”，但页面不显示降级原因；
  - 按钮依然显示为绿色的“▶ 启动”，误导用户认为系统仍处于可用态；
  - 无法点击“⏹ 停止”来中止反复崩溃的进程与熔断状态。
- **根因分析**：
  - **Bash 脚本换行符污染**：`PluginManager.Defaults.Samples.cs` 中生成的 `daemon.sh` 在 Windows 环境编译时内嵌了 CRLF（`\r\n`），在 Linux 容器内执行时，`\r` 字符导致 Bash 报 `syntax error near unexpected token 'done'`，进程以退出码 2 崩溃；
  - **CrashLoop 熔断**：由于配置了 `Supervision.AutoRestart = true`，守护轮询在 10 秒内连续重启崩溃 8 次，触发了状态机的 `PluginState.Degraded` 保护；
  - **展示层未渲染原因**：前端 `PluginCard.js` 仅针对 `MissingRuntime / MissingDependencies` 渲染了黄色引导横幅，在 `Degraded / Crashed` 状态下完全未渲染 `statusMessage`；
  - **状态机停止守卫死锁**：前端 `PluginStateMachine.canStop()` 仅允许在 `Running (2)` 与 `Starting (1)` 下执行停止；当状态变为 `Degraded (5)` 或 `Backoff (4)` 时，`canStop` 为 `false`，停止按钮直接被隐藏，导致用户无法手动终止处于故障中的应用。

---

## 二、 业界主流方案调研与对比

| 架构维度 | 业界主流实践 (K8s / Envoy / Docker / Grafana) | 本项目原有实现 | 本次改造增强方案 |
| :--- | :--- | :--- | :--- |
| **Webview / 嵌入页面鉴权** | **多通道凭据适配器 (Multi-Channel Credential Adapter)**：同时支持 Cookie、Bearer Header 与单次/URL Query Token。 | 仅依赖未初始化的 Cookie，未设置 Cookie，无 URL 参数兜底。 | **四重鉴权保障**：1. 后端 Login/Renew 下发 `lwt_token` Cookie；2. 前端设置 `document.cookie`；3. Webview URL 自动追加 `?token=...`；4. 网关鉴权解析器支持 Header+Cookie+Query 全通道解析。 |
| **状态快速过滤与置顶** | **状态多维过滤与优先级排序**：顶部胶囊按钮支持按状态过滤；默认按优先级排序（Running > Degraded > Stopped）。 | 仅支持 Profile、Category、Keyword，无状态过滤；列表按默认无序呈现。 | **状态过滤策略 + 权重排序**：新增 `StateFilterStrategy`；在 `PluginsView.js` 引入稳定排序，运行中与启动中微服务优先置顶展示。 |
| **脚本执行与异常反馈** | **自适应服务管理器探针 + 结构化结果对象**：嗅探 systemd，容器内无缝切换原生 CLI；脚本退出码非零返回 HTTP 200 + 业务失败 JSON。 | 硬编码 systemctl 导致 8 秒挂死；退出码非零返回 HTTP 400 并弹出晦涩提示。 | **探针自适应 Caddy 命令**；`ExecuteAction` 返回 HTTP 200 携带 `PluginActionResult`；前端精准显示脚本控制台 stderr 详情。 |
| **降级治理与状态机生命周期** | **容错熔断可视化 + 故障手动重置状态机**：明确展示 Backoff / CrashLoop 原因；提供 Stop / Reset 动作重置熔断计数器。 | 降级隐藏停止按钮；卡片误显绿色启动；不展示 `statusMessage`；Bash 存在 CRLF。 | **清洗 `daemon.sh` 换行符 (LF)**；完善状态机允许在 Degraded / Crashed 下执行 Stop（清零计数并重置为 Stopped）；在卡片呈现异常预警横幅与重试按钮。 |

---

## 三、 具体实施步骤

### 1. 修复 Webview 网关鉴权穿透（解决问题 1）
- **C# 后端**：
  - 在 `src/LinuxWebTool.WebHost/Features/Auth/Routes/AuthController.cs` 的 `Login` 与 `Renew` 方法中，注入 `lwt_token` Cookie：
    ```csharp
    Response.Cookies.Append("lwt_token", token, new CookieOptions
    {
        Path = "/",
        SameSite = SameSiteMode.Lax,
        HttpOnly = false,
        Expires = DateTimeOffset.UtcNow.AddDays(7)
    });
    ```
  - 在 `PluginAuthGatewayEvaluator.cs` 中增加对 Query String 的解析：
    ```csharp
    if (!isAdmin)
    {
        if (context.Request.Cookies.TryGetValue("lwt_token", out var cookieToken) && !string.IsNullOrWhiteSpace(cookieToken))
        {
            isAdmin = true;
            adminName = "web-admin";
        }
        else if ((context.Request.Query.TryGetValue("token", out var qToken) || context.Request.Query.TryGetValue("lwt_token", out qToken)) && !string.IsNullOrWhiteSpace(qToken))
        {
            isAdmin = true;
            adminName = "web-admin";
        }
    }
    ```
- **前端协同**：
  - 在 `src/LinuxWebTool.WebHost/wwwroot/app/store/auth.js` 的 `login`、`renewToken` 中同步设置 `document.cookie = 'lwt_token=' + auth.token + '; path=/; SameSite=Lax;'`；在 `logout` 时清除 cookie；
  - 在 `src/LinuxWebTool.WebHost/wwwroot/app/components/plugins/PluginWebviewModal.js` 中，为 `assetUrl` 拼接 `?token=${encodeURIComponent(auth.token)}`，彻底保证 iframe 加载与新标签页访问零 401。

### 2. 增加扩展状态快速过滤与默认置顶排序（解决问题 2）
- **过滤策略模式**：
  - 在 `src/LinuxWebTool.WebHost/wwwroot/app/utils/pluginFilterStrategies.js` 中新增 `StateFilterStrategy`：
    - `all`: 全部；
    - `running`: `state === 2`；
    - `degraded`: `state === 4 || state === 5 || state === 3`；
    - `missing`: `state === 7 || state === 8 || isMissingRuntime || isMissingDependencies`；
    - `stopped`: `state === 0 || state === 6`；
  - 注册入 `CompositePluginFilter`。
- **状态优先级排序**：
  - 新增 `sortPluginsByState(plugins)` 纯函数：
    - 运行中（Running, state 2）：权重 1
    - 启动中（Starting, state 1）：权重 2
    - 降级/崩溃（Degraded 5, Backoff 4, Crashed 3）：权重 3
    - 缺少依赖（Missing 7, 8）：权重 4
    - 已停止/禁用（Stopped 0, Disabled 6）：权重 5
  - 在 `PluginsView.js` 的 `filteredPlugins` 计算属性中应用状态过滤与排序。
- **UI 交互增强**：
  - 在 `PluginsView.js` 界面增加状态快捷下拉筛选框或状态胶囊标签；
  - 顶部 4 个大指标卡片（全部插件、常驻运行中、轻量脚本、异常降级）增加点击过滤交互。

### 3. 优化轻量脚本扩展执行与 400 报错治理（解决问题 3）
- **Caddy Hook 自适应命令优化**：
  - 修改 `plugins/caddy-server-manager/hooks/start.js`、`stop.js`、`restart.js`；
  - 增加环境嗅探函数 `isSystemd()`：检测是否存在 `/run/systemd/system`；
  - 若在 Docker/Alpine 环境，直接执行 `/usr/sbin/caddy start --config /etc/caddy/Caddyfile` 或 `caddy start`，彻底杜绝 `systemctl` 导致的 8 秒 `ETIMEDOUT`；
- **后端 Action 接口返回规范**：
  - 修改 `PluginsController.ExecuteAction`：将非零退出码的业务执行结果统一以 HTTP 200 返回（携带 `status: "failed", exitCode: 1, message: "..."`），仅在插件不存在或未授权时返回 404/403；
- **前端错误提示友好化**：
  - `PluginActionModal.js` 在动作执行失败时展示输出框和详细错误文本。

### 4. 降级应用原因透出、手动停止与 Bash 脚本清洗（解决问题 4）
- **状态机允许手动停止**：
  - 修改 `src/LinuxWebTool.WebHost/wwwroot/app/utils/pluginStateMachine.js`：
    - `canStop(plugin)`：在 `RUNNING`、`STARTING`、`CRASHED`、`BACKOFF`、`DEGRADED` 状态下均返回 `true`；
- **卡片渲染降级原因横幅**：
  - 在 `PluginCard.js` 中增加：
    当 `isDegradedOrError` 且 `plugin.statusMessage` 存在时，高亮显示黄色预警横幅与原因；
  - 当应用处于降级状态时，按钮显示 `⏹ 停止`（手动重置熔断）与 `🔄 重试拉起`；
- **Bash 换行符清洗**：
  - 修改 `PluginManager.Defaults.Samples.cs`：将 `daemon.sh` 的换行符使用 `.Replace("\r\n", "\n")` 严格清洗为 Unix LF；
  - 在远端容器重新生成或修复 `daemon.sh`。

---

## 四、 架构门禁规则 (`LinuxArch055`)

在 `tests/LinuxWebTool.ArchitectureTests/ArchitectureTests.cs` 中增加架构门禁：
1. `AuthController.Login` 与 `Renew` 必须向 Response Cookies 写入 `lwt_token`；
2. `PluginAuthGatewayEvaluator` 必须支持从 Query 参数（`token` 或 `lwt_token`）提取认证凭据；
3. 前端 `pluginFilterStrategies.js` 必须包含 `StateFilterStrategy` 且包含对 `running`、`degraded`、`stopped` 的策略判断；
4. 前端 `pluginStateMachine.js` 的 `canStop` 必须支持在 `DEGRADED`、`CRASHED` 状态下返回 `true`。
