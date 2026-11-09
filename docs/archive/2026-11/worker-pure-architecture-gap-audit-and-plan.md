# 纯粹 Worker 架构现状差距审计与解耦实施计划

> 日期：2026-11-01  
> 目标：系统审计宿主系统当前存在的耦合遗留，制定从“专有侵入”到“纯粹解耦”的工程落地计划与验收标准。

---

## 1. 宿主现有代码侵入与耦合审计

经过对代码库的全面静态扫描，发现此前迭代中遗留的专有侵入代码如下：

### 1.1 前端代码遗留审计（待清理）
1. `src/LinuxWebTool.WebHost/wwwroot/app/components/workers/McpBridgePanel.js`（专有管理面板）；
2. `src/LinuxWebTool.WebHost/wwwroot/app/components/workers/McpServerEditor.js`（专有配置编辑器）；
3. `src/LinuxWebTool.WebHost/wwwroot/app/composables/useMcpBridge.js`（专有业务数据流）；
4. `src/LinuxWebTool.WebHost/wwwroot/app/config.js` 中的 `mcpBridge` 路由常量（第 267~284 行）。

> **违背原则**：宿主前端直接嵌入了特定 Worker 的业务逻辑，任何新增 Worker 都需要修改宿主核心前端代码，违背开闭原则（OCP）。

### 1.2 宿主后端专有路由与 DTO 遗留审计（待下沉解耦）
1. `src/LinuxWebTool.WebHost/Features/McpBridge/Routes/McpBridgeController.cs`；
2. `src/LinuxWebTool.Application/Features/McpBridge/McpBridgeOrchestrator.cs`；
3. `src/LinuxWebTool.Contracts/Features/McpBridge/Contracts/`。

> **解耦策略**：Worker 自身的管理逻辑（如 Tunnel 启停、第三方 MCP 服务增删查改、工具勾选过滤）完全属于 Worker 内部领域，已在 Go Worker `internal/admin` 与 `internal/gateway` 完整实现。宿主无需维护这套专有 Controller，外部流量统一通过 `/w/mcp-bridge/*` 走通用 Ingress 转发。

---

## 2. 宿主现有可复用基础设施与能力差距分析

### 2.1 宿主已有坚实基础设施
1. **反向代理能力**：`src/LinuxWebTool.WebHost/Middleware/PipelineExtensions.cs` 已实现 `/w/{workerId}/{**catch-all}` 动态代理，支持将外部请求转发到回环端口；
2. **生命周期监管**：`WorkersManager` 配合 `WorkerStateMachine` 提供了进程启动、崩溃探测、自愈重试和端口分配；
3. **安全与权限门禁**：`ApiKeyMiddleware` 与 JWT 身份认证，支持细粒度权限控制；
4. **原生工具与契约定义**：`NativeToolModels.cs` 已抽象出 `NativeCommandDefinition`、`CommandArgumentBinding`、`CommandStdinBinding`、`ResidentControlSpec`。

### 2.2 核心能力差距（Gaps）
1. **通用静态沙盒托管缺失**：缺少对非 HTTP 扩展静态前端包的通用托管中间件（`/api/extensions/{id}/assets/*`）；
2. **前端通用 Schema 渲染器缺失**：对于非 HTTP 扩展，前端目前没有通用的 `GenericSchemaForm.js`，导致工具必须自己写视图；
3. **缺少统一 IPC Bridge SDK**：静态沙盒缺少开箱即用的 `window.HostBridge` 与宿主通用调用端点；
4. **进程流控与孤儿回收**：手写 `Process.Start` 缺少跨平台进程树级联销毁保证（需引入 `CliWrap` 或完善 JobObject/Pdeathsig）。

---

## 3. 分阶段解耦与落地实施计划

```mermaid
flowchart TD
    Phase1[阶段一：宿主彻底去侵入化] --> Phase2[阶段二：Worker 自包含 Web 控制台完善]
    Phase2 --> Phase3[阶段三：非 HTTP 扩展通用 Schema 注入]
    Phase3 --> Phase4[阶段四：全链路验收与门禁固化]

    subgraph Phase1_Tasks["阶段一：宿主去侵入"]
        P1_1[清除 wwwroot 中的 McpBridge 专有组件]
        P1_2[清除 config.js 中的专有路由常量]
        P1_3[验证 frontend-gate 保持 0 违规]
    end

    subgraph Phase2_Tasks["阶段二：Worker 自治"]
        P2_1[Go Worker 集成 //go:embed 自包含单页 UI]
        P2_2[Go Worker HTTP Handler 提供静态页面与 API]
        P2_3[宿主通过 /w/{id}/* 纯 Ingress 代理呈现]
    end

    subgraph Phase3_Tasks["阶段三：非 HTTP 通用支持"]
        P3_1[编写前端通用 GenericSchemaForm.js]
        P3_2[后端提供通用 POST /api/plugins/{id}/execute]
        P3_3[提供 SSE 实时日志流终端弹窗]
    end

    Phase1 --> Phase1_Tasks
    Phase2 --> Phase2_Tasks
    Phase3 --> Phase3_Tasks
```

---

## 4. 二十项验收标准（Acceptance Criteria）

### 宿主零侵入标准
- [x] **AC-01**：宿主核心前端 `WorkersView.js` 不包含任何针对特定 Worker ID 的 `v-if` 或专用按钮；
- [ ] **AC-02**：`src/LinuxWebTool.WebHost/wwwroot/` 下不存在任何 Worker 专有的 `.js` 或 `.vue` 文件；
- [ ] **AC-03**：`src/LinuxWebTool.WebHost/wwwroot/app/config.js` 不包含任何特定 Worker 的专属 API 路由；
- [x] **AC-04**：前端静态代码门禁扫描 `node frontend-gate.cjs` 通过，0 违规；

### Worker 自闭环标准
- [x] **AC-05**：`workers/mcp-bridge` 包含完整的配置管理、草稿保存、工具测试与网关核心；
- [ ] **AC-06**：`workers/mcp-bridge` 通过 Go 原生 `//go:embed` 内嵌自包含 Web 管理界面，无需外部宿主前端支持；
- [x] **AC-07**：Worker 单元测试 100% 通过（`go test ./...`）；
- [x] **AC-08**：打包脚本能够生成独立于宿主的分发归档包（`.zip` 与 `SHA256SUMS`）；

### 非 HTTP 扩展通用注入标准
- [ ] **AC-09**：非 HTTP 扩展可通过 `manifest.json` 中的 `ui.formSchema` 声明参数，宿主通用表单自动解析渲染；
- [ ] **AC-10**：非 HTTP 扩展执行采用异步作业模式，返回 `jobId`，不阻塞 HTTP 线程；
- [ ] **AC-11**：宿主提供通用 SSE 日志流接口，前端实时渲染控制台输出；
- [ ] **AC-12**：支持结构化进度解析（`json-lines`），进度条平滑更新；
- [ ] **AC-13**：执行产生的产物（文件/图片）按规则收集并提供通用下载；
- [ ] **AC-14**：静态 Webview 扩展加载于 `<iframe sandbox="allow-scripts">`，阻断跨域与 Cookie 访问；
- [ ] **AC-15**：宿主注入的 `HostBridge` 通信仅限当前扩展作用域，越权调用直接拦截；

### 稳定性与性能标准
- [x] **AC-16**：单次请求执行遵循严格的 120 秒安全预算递减模型；
- [x] **AC-17**：敏感凭据在内存与网络传输中实行掩码化，不回显明文；
- [ ] **AC-18**：单扩展并发执行施加严格信号量限制（默认并发 ≤ 2）；
- [x] **AC-19**：Worker 进程常驻内存 ≤ 30 MiB，空闲 CPU < 0.5%；
- [x] **AC-20**：所有架构文档与设计决策均持久化沉淀于 `docs/` 目录。
