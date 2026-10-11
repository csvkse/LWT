# LinuxWebTool 软件测试框架与性能追踪指标建设方案

> **参考基准**：对标 `软件开发测试框架/软件测试的核心概览.md` 持续质量治理体系（Continuous Quality Engineering）。  
> **核心目标**：建立完整的测试金字塔（白盒/黑盒、单元/集成/E2E/冒烟），补全分布式链路追踪与性能度量指标，升级 GitHub Actions 自动化流水线。

---

## 一、当前项目测试与质量现状调研

### 1.1 白盒与黑盒测试维度现状

| 维度 | 当前现状 | 核心缺陷与问题诊断 | 改进决策 |
|---|---|---|---|
| **白盒测试：业务单元测试 (Unit Test)** | 混杂在 `tests/LinuxWebTool.ArchitectureTests` 中（如 `CoreUnitTests.cs`, `EasyTierStaticIpTests.cs` 等）。 | 1. 缺少独立单元测试工程 `LinuxWebTool.UnitTests`，职责边界混乱；<br>2. 核心业务服务（命令调度、权限矩阵、数据路径安全、追踪度量）缺乏高内聚隔离测试；<br>3. 未启用代码覆盖率收集（Coverlet）。 | **必须补充**：设立独立 `LinuxWebTool.UnitTests` 工程，补充领域与安全算法单元测试，接入 Coverlet 覆盖率收集。 |
| **白盒测试：架构适应度函数 (Architecture Gates)** | `ArchitectureTests.cs` 覆盖 L0~L5 规则（LinuxArch001~018 及部分扩展规则）。 | 已有良好的架构约束体系，但需解耦业务测试，保证架构测试聚焦于依赖方向、命名规范、AOT DTO 与 SQL NOCASE。 | **保持并深化**：保持现有 170+ 架构门禁，确保架构防腐蚀。 |
| **白盒测试：前端单元测试 (Frontend Unit Test)** | `tests/frontend/` 编写了 25 个用例（测试 API client 超时重试、EasyTier 路由与静态 IP 校验）。 | **致命缺陷**：因为缺少 Node.js `--experimental-vm-modules` 启动参数，测试直接崩溃；且未接入 `ci.yml` 和 `verify-fast.ps1`，导致前端测试长期处于孤岛状态。 | **必须修复并接入**：补全启动参数，纳入快速门禁与 CI 流水线。 |
| **黑盒测试：功能集成测试 (Integration Test)** | `tests/LinuxWebTool.IntegrationTests` 使用 `TestServer` 覆盖了常用 API、API Key、EasyTier 等 68 个测试。 | 缺少链路追踪头（`traceparent`）、系统性能指标端点、文件流式下载与 Zip 压缩全功能集成覆盖。 | **必须扩展**：添加追踪标头透传、度量端点及安全边界的真实集成测试。 |
| **黑盒测试：核心功能冒烟测试 (Smoke Test)** | `scripts/smoke-http.mjs` 针对 Native AOT 容器与本地程序执行黑盒探测。 | 仅覆盖了基本的 Commands/Files 简单增删改，缺少链路追踪 Header 规范性检验、内部度量数据验证和性能预算硬门禁。 | **必须强化**：扩展黑盒 API 契约断言，增加对 W3C TraceContext 与度量接口的黑盒校验。 |
| **黑盒测试：端到端测试 (E2E Test)** | `tests/smoke/browser.spec.mjs` 使用 Playwright 进行了登录和三个导航页的黑盒测试。 | 缺少对指令执行、系统监控图表、文件浏览的完整用户旅程（User Journey）验证。 | **规划并扩展**：扩展核心业务路径的端到端黑盒。 |

---

### 1.2 性能与追踪指标（Observability & Metrics）现状调研

- **追踪指标 (Tracing)**：
  - **现状**：项目完全**没有** W3C TraceContext (`traceparent`) 支持，没有结构化 `X-Correlation-Id`，跨组件或前后端交互时无法通过单次请求 ID 追踪日志和调用链路。
  - **可行性**：**完全可行**。应在 WebHost 中实现高性能、零依赖、纯 Native AOT 兼容的 `TraceContextMiddleware`，自动提取或生成标准 W3C TraceContext（32位十六进制 TraceId + 16位十六进制 SpanId），并回写到响应头 `traceparent` 与 `X-Trace-Id`。
- **性能指标 (Metrics)**：
  - **现状**：仅在黑盒 Node.js 冒烟脚本中串行采样了 4 个接口的外部客户端耗时；服务端内部缺乏指标收集机制（没有请求总数、状态码分布、耗时直方图、活跃会话等统计），也没有标准的 `/api/Metrics` 端点。
  - **可行性**：**完全可行**。基于并发安全原子计数器和 Reservoir 采样，实现 `IAppMetricsService` / `AppMetricsService`，并通过 `/api/Metrics` 提供显式 DTO 格式指标数据（遵循 Native AOT 规范）。
- **性能预算门禁 (Performance Budget Guard)**：
  - **现状**：现有性能冒烟仅作为 `advisory` 参考，无法在 CI 中阻止性能劣化。
  - **改进**：增加延时预算比对逻辑（如 P95 < 200ms，错误率 = 0%），在严格模式下阻断流水线。

---

### 1.3 GitHub Actions 测试流水线现状调研

- **现状**：`.github/workflows/ci.yml` 包含 `build-test` 与 `docker` 矩阵构建。
- **问题**：
  1. 遗漏了前端 25 项单元测试；
  2. 未配置代码覆盖率（Coverlet）收集，无法量化白盒测试质量；
  3. 未在 GitHub Step Summary 中输出结构化的测试通过数与覆盖率汇总；
  4. 缺少性能指标预算红线校验。
- **可行性**：**完全可以升级**，重塑为多层防御的 CI/CD 现代化流水线。

---

## 二、架构设计与落地实施方案

### 2.1 分布式链路追踪与指标度量架构设计

```
[HTTP Request]
       │
       ▼
[TraceContextMiddleware]
  ├─ 提取或生成 W3C `traceparent` (version-traceid-spanid-flags)
  ├─ 注入 `X-Trace-Id` / `X-Correlation-Id` 到响应头与 HttpContext.Items
  ├─ 记录请求开始时间戳 (Stopwatch.GetTimestamp)
       │
       ▼
[业务处理管线 / Minimal API / Controller / Proxy]
       │
       ▼
[Response 返回与度量归档]
  └─ AppMetricsService 记录:
       - 吞吐计数 (Total Requests, Status 2xx/3xx/4xx/5xx)
       - 接口请求耗时 (Duration Ms, Min/Max/Avg/P50/P95)
       - 活跃会话与并发数
```

#### 契约定义 (`LinuxWebTool.Contracts`)
- `AppMetricsSnapshot`：包含请求总量、活跃请求、各状态码计数、平均/P50/P95 耗时、运行时间等。
- 遵循零第三方依赖（BCL 纯净），并通过 `AppJsonSerializerContext` 注册以保证 Native AOT 兼容。

#### 端点设计
- `GET /api/Metrics`：返回当前系统内部的性能度量快照。响应包含：
  - `totalRequests`
  - `activeRequests`
  - `status2xx`, `status4xx`, `status5xx`
  - `averageDurationMs`, `p95DurationMs`, `maxDurationMs`
  - `uptimeSeconds`

---

### 2.2 测试工程体系重构方案

```
tests/
  ├── LinuxWebTool.ArchitectureTests/    # 架构适应度函数与代码质量门禁 (L0 ~ L5)
  ├── LinuxWebTool.UnitTests/            # 【新增】业务逻辑、权限矩阵、安全、算法纯内存白盒单元测试
  ├── LinuxWebTool.IntegrationTests/     # 基于 TestServer 的 API 契约、状态机与追踪集成测试
  ├── frontend/                          # 前端 API client、超时重试与状态计算单元测试
  └── smoke/                             # 黑盒 HTTP 协议冒烟、浏览器 Playwright E2E 与性能指标压测
```

#### 新增 `LinuxWebTool.UnitTests` 覆盖重点：
1. `TraceContextTests`：验证 W3C TraceContext 的解析、生成、格式校验与父子 Span 派生。
2. `AppMetricsServiceTests`：验证并发递增、耗时统计、分位数近邻计算、状态码分类统计。
3. `ApiKeyPermissionMatrixTests`：验证细粒度权限校验矩阵、通配符支持与越权防御。
4. `DataPathsSafetyTests`：验证目录跨越防御（`../` 逃逸拦截）与路径解析一致性。
5. `PasswordHasherTests`：验证密码哈希计算、加盐抗碰撞及空密码边界。

#### 扩展 `LinuxWebTool.IntegrationTests`：
1. `DiagnosticsAndMetricsIntegrationTests`：
   - 验证任意 API 请求自动携带合法的 W3C `traceparent` 和 `X-Trace-Id` 响应头；
   - 验证客户端传入 `traceparent` 时服务端的继承与子 Span 派生；
   - 验证 `/api/Metrics` 能够正确反映已发起的请求统计。

#### 强化黑盒冒烟 `scripts/smoke-http.mjs`：
1. 校验服务端返回的所有响应均包含合规的 `traceparent` 与 `X-Trace-Id`；
2. 黑盒请求 `/api/Metrics`，断言内部统计数据正常自增；
3. 校验性能预算门禁（Latency Budget Guard：P95 < 200ms）。

---

### 2.3 GitHub Actions CI/CD 流水线设计 (四层门禁矩阵)

| 阶段 | 步骤名称 | 检验工具 | 阻断条件 |
|---|---|---|---|
| **G1 静态与架构** | Architecture Tests | `dotnet test LinuxWebTool.ArchitectureTests` | 任何架构违规或非法依赖阻断 |
| | Frontend Gate & Lint | `node frontend-gate.cjs` + ESLint | 依赖越权或语法违规阻断 |
| **G2 白盒与覆盖率**| C# Unit Tests | `dotnet test LinuxWebTool.UnitTests --collect:"XPlat Code Coverage"` | 任何用例失败阻断；收集 Cobertura 报告 |
| | Frontend Unit Tests | `node --experimental-vm-modules --test tests/frontend/*.test.mjs` | 前端 25 项测试失败阻断 |
| | Integration Tests | `dotnet test LinuxWebTool.IntegrationTests` | 任何集成契约失败阻断 |
| **G3 报告与摘要** | Step Summary & Upload | GitHub Actions TRX / Coverage Artifacts | 自动生成 Markdown 摘要表格 |
| **G4 黑盒与性能** | Native AOT Docker Smoke | `./scripts/smoke-aot.ps1` (HTTP + Browser + PTY) | 契约破坏、401/404/500 异常或性能预算超标阻断 |

---

## 三、实施与落地执行

## 四、落地实施与验收记录

### 4.1 实施完成情况
- [x] **契约与模型**：在 `LinuxWebTool.Contracts` 中新增度量模型 `MetricsModels.cs`（`AppMetricsSnapshot`）与契约接口 `IAppMetricsService.cs`，遵循零第三方依赖（纯 BCL），在 `AppJsonSerializerContext` 显式注册，保持 Native AOT 序列化兼容。
- [x] **度量与追踪实现**：
  - 在 `LinuxWebTool.Infrastructure` 中实现高并发、原生 AOT 友好的 `AppMetricsService`；
  - 在 `LinuxWebTool.WebHost` 中实现标准 W3C TraceContext 解析与派生的 `TraceContextMiddleware`，生成并回传 `traceparent`、`X-Trace-Id`、`X-Correlation-Id`；
  - 在 `Composition.PipelineExtensions` 中挂载 `TraceContextMiddleware`，并暴露 `/api/Metrics` 查询端点。
- [x] **白盒单元测试工程**：
  - 新建独立的 `tests/LinuxWebTool.UnitTests/LinuxWebTool.UnitTests.csproj`，引入 `coverlet.collector`；
  - 新增 `TraceContextTests`、`AppMetricsServiceTests`、`ApiKeyPermissionMatrixTests`、`DataPathsSafetyTests`、`PasswordHasherTests` 共 **38 项单元测试**；
  - 成功生成 `coverage.cobertura.xml` 代码覆盖率报告。
- [x] **黑盒与集成测试扩展**：
  - 在 `LinuxWebTool.IntegrationTests` 中新增 `DiagnosticsAndMetricsIntegrationTests`，验证真实 HTTP 请求中的 W3C 追踪头派生、未授权透传与实时指标自增；集成测试通过数从 68 提升至 **72 项**。
  - 在 `scripts/smoke-http.mjs` 中接入严格 W3C Trace 标头黑盒校验与 `/api/Metrics` 自动化断言；
  - 在 `scripts/smoke-performance.mjs` 中新增 `verifyBudgets` 性能预算门禁检查（支持 P95 < 250ms SLA 与 0% 错误率断言）。
- [x] **前端单元测试修复与门禁接入**：
  - 修复 Node.js 原生 VM 模块解析，开启 `--experimental-vm-modules` 参数，前端 25 项测试全部通过；
  - 将前端单元测试正式集成到本地 `verify-fast.ps1` 和 CI 工作流中。
- [x] **GitHub Actions 流水线升级**：
  - 更新 `.github/workflows/ci.yml` 的 `Build & Gates` 作业：并行执行架构门禁、白盒单元测试（收集 Cobertura 覆盖率）、API 集成门禁、前端架构门禁、前端单元测试及冒烟工具套件；
  - 更新本地 `scripts/verify-fast.ps1` 为 6 级完整自动化门禁矩阵。

### 4.2 本地全量门禁实测结果
```text
== [1/6] dotnet build == 成功 (0 警告, 0 错误)
== [2/6] dotnet test (架构门禁) == 170 passed, 0 failed
== [3/6] dotnet test (白盒单元测试) == 38 passed, 0 failed
== [4/6] dotnet test (API 集成门禁) == 72 passed, 0 failed
== [5/6] 前端架构门禁与前端单元测试 == 25 passed, 0 failed + ESLint 通过
== [6/6] 冒烟与性能测试套件回归 == 7 passed, 0 failed
✅ 快速门禁全部通过
```

