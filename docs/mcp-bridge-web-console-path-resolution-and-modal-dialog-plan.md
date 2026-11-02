# MCP Bridge 控制台刷新异常与添加服务点击无效根因分析、业界方案调研与加固落地方案

> **文件标识**：`docs/mcp-bridge-web-console-path-resolution-and-modal-dialog-plan.md`  
> **创建时间**：2026-11-02  
> **涉及组件**：`plugins/mcp-bridge` (Go 智能网关), `PluginWebviewModal.js`, `handler.go`, `config.js`  
> **目标**：彻底解决 MCP Bridge 网关控制台刷新异常（返回 HTML 导致 JSON 解析失败）与添加服务按钮点击无响应两大缺陷，对比业界标准并落地多层纵深防御门禁。

---

## 1. 缺陷深度调研与根因剖析 (Root Cause Analysis)

### 1.1 问题一：MCP Bridge 网关控制台「刷新」按钮异常
#### 1. 现象复现
用户或控制台触发的 cURL 请求：
```bash
curl --url 'http://10.126.1.70:5271/Plugins/mcp-bridge/Assets/index.html/control/invoke/status.get' \
  -X 'POST' \
  -H 'Content-Type: application/json' \
  -H 'Referer: http://10.126.1.70:5271/Plugins/mcp-bridge/Assets/index.html'
```
控制台响应返回了 HTML 网页内容（`<!DOCTYPE html>...`），前端报错：
`SyntaxError: Unexpected token '<', "<!DOCTYPE "... is not valid JSON`，状态卡死在“检查中...”。

#### 2. 本质原因逐层链条
1. **前端 BaseUrl 路径计算存在严重缺陷**：
   在 `plugins/mcp-bridge/internal/web/index.html` 中：
   ```javascript
   const baseUrl = window.location.pathname.replace(/\/+$/, '');
   const url = `${baseUrl}/control/invoke/${action}${qs}`;
   ```
   `window.location.pathname` 包含了页面文件名本身（如 `/Plugins/mcp-bridge/Assets/index.html` 或 `/plugins/mcp-bridge/index.html`）。`replace(/\/+$/, '')` 仅移除了末尾的斜杠，未剔除文件名和上级资产虚拟目录，直接拼接出 `/Plugins/mcp-bridge/Assets/index.html/control/invoke/status.get`。
2. **宿主路由穿透与网关反向代理重叠**：
   在 `PipelineExtensions.cs` 中，统一网关配置了 `app.Map("/plugins/{pluginId}/{**catch-all}")`。由于 ASP.NET Core URL 路由不区分大小写，`/Plugins/mcp-bridge/Assets/index.html/...` 被反向代理网关捕获，并将 `Assets/index.html/control/invoke/status.get` 原样透传给 Go 后端。
3. **后端 SPA Fallback 兜底吞噬路由**：
   在 Go `plugins/mcp-bridge/internal/admin/handler.go` 中，路由注册为：
   ```go
   mux.HandleFunc("/control/invoke/", h.handleInvoke)
   webHandler := web.Handler()
   mux.Handle("/", webHandler)
   ```
   Go `http.ServeMux` 精确前缀匹配失效后，非 `control/invoke/` 开头的请求全部回退到根路由 `/`（即 `webHandler`）。`webHandler` 无条件对所有 HTTP 方法（包括 POST）返回 `200 OK` 与 `index.html`。
4. **控制面 Token 鉴权脱节**：
   Go 进程默认生成 `dev-token-<runId>`，但 `index.html` 首次加载时 `localStorage` 为空，未携带任何 Token，即使路径正确也会遭遇 `401 Unauthorized`。

---

### 1.2 问题二：➕「添加 MCP 服务」按钮点击无效
#### 1. 现象复现
在控制台中点击「➕ 添加 MCP 服务」按钮，页面没有任何反应，无弹窗、无报错、无网络请求。

#### 2. 本质原因剖析
1. **Iframe 沙箱策略阻断原生模态窗口 (`allow-modals` 缺失)**：
   在 `PluginWebviewModal.js` 中：
   ```html
   <iframe
     sandbox="allow-scripts allow-forms allow-same-origin allow-popups"
   />
   ```
   W3C HTML 规范明确规定：当 `<iframe>` 声明了 `sandbox` 但未包含 `allow-modals` 时，在沙箱环境中调用 `window.prompt()`、`window.alert()` 或 `window.confirm()` 将被**静默阻断并立即返回 `null`**（或抛出 SecurityError）。
2. **前端代码脆弱依赖 4 级连续 `window.prompt()`**：
   在 `plugins/mcp-bridge/internal/web/index.html` 中：
   ```javascript
   function openAddServerModal() {
     const id = prompt('请输入新 MCP 服务 ID:');
     if (!id) return; // 因 prompt() 静默返回 null，首行立即 return 退出！
     ...
   }
   ```
   首行 `prompt(...)` 返回 `null` 后触发 `if (!id) return;`，函数直接退出，因此按钮表现为“点击完全无效”。
   同理，`openTokenDialog()` 与 `deleteServer()` (`confirm()`) 也全部由于此原因失效。
3. **体验与规范反模式 (Anti-Pattern)**：
   依赖多级连续阻断式 `window.prompt()` 是上世纪早期遗留的反模式，现代 Web UI 必须采用页面内原生模态框（In-Page Modal Component / `<dialog>`）。

---

## 2. 业界主流方案调研与对比 (Industry Benchmarks)

| 维度 | VS Code Webview 扩展 | Home Assistant Ingress | Grafana / Portainer 插件 | 我们的选型与落地 |
| :--- | :--- | :--- | :--- | :--- |
| **Iframe 沙箱安全** | 禁止 `allow-modals`，强制使用 RPC / `postMessage` | 注入 `allow-modals allow-forms` | 开放基础交互沙箱权限 | **双重保障**：沙箱补充 `allow-modals`，同时组件全面重构为**页内原生模态框** |
| **相对路径与 BaseUrl 解析** | Webview 专用虚拟 URI 协议 (`vscode-webview://`) | 动态重写 `<base href="...">` 或由 `X-Ingress-Path` 引导 | 约定前缀正则清理 + 规范化 BaseUrl 计算 | **双重自愈**：前端 `getBaseUrl()` 智能裁剪文件名与 `/Assets/`；Go 后端对 `/control/invoke/` 实施路径规范化路由 |
| **SPA Fallback 防护** | 严格区分静态文件路由与 API 路由 | 仅对 `GET/HEAD` 且未匹配 API 前缀的请求响应 HTML | 非 GET 请求或 API 前缀绝不返回 HTML (404 JSON) | **严格方法守卫**：`webHandler` 仅响应 `GET/HEAD`；非 GET 或 `/control/` 路由未匹配时明确返回 404 JSON |
| **模态交互模式** | 宿主提供 `showInputBox` / 页内虚拟 DOM | Ingress 原生 Web Component 模态弹窗 | Bootstrap / Tailwind 原生模态浮层 | **自包含内嵌模态框**：零外部依赖，使用纯 CSS + Flex/Grid + `<div class="modal">` |

---

## 3. 当前系统已有基础设施复用评估

1. **宿主网关转发与请求头支持**：
   - `PipelineExtensions.cs` 已注入 `X-Forwarded-Prefix: /plugins/{pluginId}` 与 `X-LWT-Plugin-Id: {pluginId}`；
   - Go 后端可通过解析 `X-Forwarded-Prefix` 或提取 loopback 请求直接确认为可信反代流量。
2. **动态 Token 注入**：
   - Go `web.Handler` 可以在输出 `index.html` 时动态注入 `<script>window.__MCP_BRIDGE_TOKEN__ = "{{.Token}}";</script>`，实现打开页面即自动完成控制面鉴权，无需用户手动寻找并输入 Token。
3. **前端暗黑赛博主题 CSS 系统**：
   - `index.html` 已经拥有成熟的 CSS 变量系统（`--bg`, `--card-bg`, `--border`, `--text`, `--primary` 等），可直接复用于模态弹窗、表单浮层与确认框，风格与 LinuxWebTool 保持高度一致。

---

## 4. 落地实施方案与架构变更

### 4.1 方案 A：`plugins/mcp-bridge/internal/web/index.html` 彻底重构
1. **规范化 BaseUrl 计算**：
   ```javascript
   function getBaseUrl() {
     let p = window.location.pathname;
     p = p.replace(/\/[^/]+\.(?:html?|php|jsp|asp|aspx)$/i, '');
     p = p.replace(/\/(?:Assets|ui)(?:\/.*)?$/i, '');
     return p.replace(/\/+$/, '');
   }
   ```
2. **新增原生页面级模态弹窗**：
   - **添加 MCP 服务模态框 (`#addServerModal`)**：
     包含服务 ID（必填、小写字母数字短横线）、显示名称、传输协议（`<select>` 下拉框：stdio / http / sse）、启动命令 / URL、超时时间（默认 30s）、启用开关；
   - **令牌设置模态框 (`#tokenModal`)**：
     密码输入框、保存与清空按钮；
   - **操作确认模态框 (`#confirmModal`)**：
     通用删除/高危操作确认提示框。
3. **自动读取并同步 Token**：
   ```javascript
   let currentToken = window.__MCP_BRIDGE_TOKEN__ || urlParams.get('token') || localStorage.getItem('mcp_bridge_token') || '';
   ```

### 4.2 方案 B：Go 后端安全守卫与路由容错加固
1. **SPA Fallback 与方法守卫 (`handler.go`)**：
   - `webHandler` 限制仅在 `r.Method == http.MethodGet || r.Method == http.MethodHead` 且路径不以 `/control/` 开头时提供 HTML；
   - 对非 GET 方法或未匹配的 `/control/` 请求，返回 `404 Not Found` (JSON)，杜绝向 API 调用返回 HTML 200。
2. **控制面路由前缀自愈容错**：
   - 若请求路径中包含 `/control/invoke/`（即使前缀带有 `/Assets/index.html`），正则/子串规范化后分发给 `handleInvoke`。
3. **运行时 Token 自动注入 (`web.go`)**：
   - `web.HandlerWithToken(token)` 动态向 HTML 中注入全局变量，省去用户繁琐的手工配置。

### 4.3 方案 C：前端 `PluginWebviewModal.js` 与 `config.js` 统一修复
1. `PluginWebviewModal.js` 中的 `iframe` 增加 `allow-modals` 沙箱权限；
2. 针对常驻型网关服务（`network.mode === 'path'` 或 `ui.entry === 'web'`），直接使用网关反代路径 `/plugins/{id}/`；
3. `config.js` 的 `API.plugins.asset` 规范化为 `/api/Plugins/{id}/Assets/{file}`。

---

## 5. 架构门禁与防回归保障 (Architecture Gates)

1. **C# 架构门禁 (`LinuxWebTool.ArchitectureTests`)**：
   - `LinuxArch045_PluginWebviewSandboxAndModalIntegrity`：门禁拦截 iframe 缺少 `allow-modals`、拦截控制台前端误用阻断式 `window.prompt`、拦截粗暴拼接 baseUrl。
2. **Go 单元测试与端到端测试 (`handler_test.go`)**：
   - 测试包含 `/Assets/index.html/control/invoke/status.get` 时的自愈路由与 JSON 响应；
   - 测试非 GET 请求访问不存在路径时返回 404 JSON 而非 200 HTML；
   - 测试 Web UI 成功嵌入 Token。
3. **前端自动化门禁 (`frontend-gate.cjs`)**：
   - `FE-WEBVIEW-SANDBOX-MODALS`：检查所有宿主 iframe 组件必须声明 `allow-modals`。
