# MCP Bridge 隧道状态固化回填、配置生效容错降级与界面协同调研报告

## 一、 用户面临的三大问题表现

在远程开发环境（`http://10.126.1.70:5271/plugins/mcp-bridge`）操作 MCP 智能安全网关时，出现以下异常：

1. **问题一（Tunnel 固化疑虑）**：
   通过 `tunnel.save` 保存了 OpenAI Tunnel 接入点与密钥后，刷新页面或重新进入控制台，输入框变为空白，引发“开发容器是否未持久化数据文件夹”的疑问。
2. **问题二（配置生效 Apply 报错）**：
   点击“🚀 应用生效配置 (Apply)”按钮，返回 HTTP 400 失败：
   ```json
   {
       "success": false,
       "activeRevision": 0,
       "activeToolCount": 0,
       "message": "discovery failed for server \"fetch\" during apply: failed to connect to stdio backend \"fetch\": exec: \"uvx\": executable file not found in $PATH"
   }
   ```
3. **问题三（纳管服务列表为空）**：
   导入标准 MCP 配置（如 Claude Desktop 格式）显示导入成功并自增版本号，但控制台页面上的“第三方 MCP 纳管服务”表格仍然显示“暂无已配置的服务”。

---

## 二、 根因定位与排查证据

通过在远程 Linux 容器直接检查底层存储文件 `/app/data/plugins/mcp-bridge/data/mcp-bridge/bridge-state.json`，发现了明确的真实状态：

```json
{
  "draftRevision": 10,
  "desiredRevision": 10,
  "activeRevision": 6,
  "tunnel": {
    "tunnelId": "tunnel_0123456789abcdef0123456789abcdef",
    "encryptedKey": "bCFZKIisEOP72rZMB1DT..."
  },
  "servers": {
    "fetch": { "id": "fetch", "transport": "stdio", "command": "uvx", "args": ["mcp-server-fetch"], "enabled": true },
    "filesystem": { "id": "filesystem", "transport": "stdio", "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/data"], "enabled": true },
    "knowledge-arena": { "id": "knowledge-arena", "transport": "sse", "url": "https://p.asairo.de/mcp/sse?room=knowledge", "enabled": true },
    "weather-sse": { "id": "weather-sse", "transport": "sse", "url": "http://127.0.0.1:8000/sse", "enabled": true }
  }
}
```

### 证据分析结论：
1. **数据持久化真实存在且完全正常**：
   Docker 挂载的数据卷 `linuxwebtool-dev-data:/app/data` 正常工作，`bridge-state.json` 中完整持久化了 `tunnelId`、已加密的预共享密钥、以及导入的 4 个 MCP 服务配置。
2. **问题一根因：前端数据回填契约错位**：
   - 前端 `index.html` 的 `refreshStatus()` 函数仅调用了 `status.get` 接口；
   - `status.get` 返回的 `StatusDTO` 仅包含运行时健康指标，未输出 `tunnelId` 与 `hasTunnelKey`；
   - 前端代码却试图从 `s.config.tunnel.tunnelId` 中读取值；由于 `s.config` 为 `undefined`，输入框永远显示为空白。
3. **问题三根因：服务列表从未被拉取**：
   - 后端早已提供了 `servers.list` 接口，但前端 `refreshStatus()` 从未调用该接口；
   - 前端视图错误地假设 `s.config.backends` 存在，导致表格永远渲染为 `暂无已配置的服务`。
4. **问题二根因：Apply 过程缺乏容错降级（Hard Fail）**：
   - 当点击 Apply 时，`gateway.Apply` 遍历所有 `Enabled: true` 的服务并执行同步探针 `Discover(ctx, srv)`；
   - 导入的 Claude 配置文件中包含 `uvx mcp-server-fetch`，但 Alpine 容器并未预装 `uvx` 工具链；
   - `gateway.Apply` 发现任一后端连接失败即直接阻断整笔 Apply 事务并抛错退出，导致其他正常服务（如 `filesystem`、`weather-sse`、`knowledge-arena`）和 OpenAI Tunnel 无法激活生效。

---

## 三、 业界主流方案调研与对比

| 方案 / 平台 | 针对未就绪/缺失依赖上游的配置生效策略 | 服务启停与状态反馈机制 |
| :--- | :--- | :--- |
| **Kong Gateway / Envoy** | **健康隔离与优雅降级（Graceful Degradation）**：配置重载时单点上游不可用不阻塞整体变更生效，将异常节点标记为 Degraded，流量仅路由到就绪 upstream。 | 针对每个 Upstream / Target 提供独立的 enabled/disabled 状态与探针。 |
| **Claude Desktop / Cursor** | **进程隔离与容错告警**：单个 stdio 命令（如 `uvx`）缺失时在控制台记录 warning 并在卡片打标，不阻断主窗口启动与其他 MCP 服务正常运行。 | 配置文件通过 `disabled` 属性或图形界面单选开关随时开启/关闭服务。 |
| **Open WebUI / Dify** | **工具链沙箱化与单项禁用**：批量导入 MCP 或 OpenAPI 契约时，连通性测试失败的服务自动标记为禁用或警告，允许用户单独切换启停。 | 表格内提供直观的 Switch 开关，一键切换服务启用/禁用。 |

---

## 四、 具体改造方案设计

### 1. 后端：网关配置生效容错降级（Gateway Apply Fault Tolerance）
- 在 `plugins/mcp-bridge/internal/gateway/gateway.go` 的 `Apply` 方法中：
  - 对每个启用的后端执行 `g.manager.Discover(ctx, srv)`；
  - 若探测失败（例如可执行文件不存在或网络暂时不通），不中断整体流程，而是记录 Warning 日志，将该服务加入 `degradedServers` 列表；
  - 其余就绪服务正常编译成 Candidate 快照，并提交 Draft 版本升级为 Active 版本；
  - 返回结果携带 `DegradedServers` 清单与友好提示信息。

### 2. 后端：状态透出与单项启停动作（Status & Toggle Action）
- 在 `StatusDTO` 中增加：
  - `tunnelId`：当前配置的 Tunnel ID；
  - `hasTunnelKey`：布尔值，标识是否已固化密钥；
- 在 Admin Handler 中新增 `servers.toggle` Action：
  - 路由：`POST /control/invoke/servers.toggle?id=<id>`；
  - 原子翻转指定服务的 `Enabled` 状态并自增版本号，便于管理员一键关闭缺少运行环境的服务。

### 3. 前端：Webview 控制台协同回填与完整渲染
- `refreshStatus()` 改为并发调用 `status.get` 与 `servers.list`；
- 回填 `tunnelIdInput`，当检测到 `hasTunnelKey === true` 时显示绿色已保存徽标，并更新密码输入框占位符为“已保存密钥；留空保持不变”；
- 完整渲染纳管服务表格，增加【⏯ 启用/禁用】快捷按钮；
- Apply 生效按钮在遇到降级服务时，弹出清晰的警告提示，而不是全盘红脸崩溃。

---

## 五、 门禁与防回归测试

新增自动化架构门禁：
`LinuxArch052_MCP桥接服务状态固化回填_容错降级生效与界面协同门禁`：
1. 校验 `StatusDTO` 包含 `TunnelID` 与 `HasTunnelKey`，且 `ApplyResult` 包含 `DegradedServers`；
2. 校验 `Gateway.Apply` 具备容错降级逻辑，不会因为单点后端探针失败阻断整体发布；
3. 校验 `Admin Handler` 注册并实现了 `servers.toggle` 动作；
4. 校验 Web 控制台 `index.html` 包含 `servers.list` 与 `servers.toggle` 调用链路，确保隧道信息和纳管服务列表真实回填。
