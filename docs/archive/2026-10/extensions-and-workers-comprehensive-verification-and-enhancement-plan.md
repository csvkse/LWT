# 扩展服务 (Extensions) 与 SaaS 函数微服务 (Workers & Daemons) 全功能真实演练、架构头脑风暴与深度自愈方案

## 一、 背景与验证目标

在 LinuxWebTool (LWT) 系统的演化过程中，**扩展服务中枢 (Extensions)** 与 **SaaS 函数微服务与长时守护平台 (Workers & Daemons)** 构成了系统的双核动态服务引擎：
- **扩展服务 (Extensions)**：基于契约脚本驱动的运维扩展中枢，承载宿主级系统运维能力（如 SSH 登录安全卫士、Caddy 现代 Web 服务器、系统健康自检守护等）。
- **SaaS 函数微服务 (Workers & Daemons)**：基于类 Cloudflare Workers / PM2 / Fly.io 架构的多语言轻量微服务与长时常驻守护进程托管平台，支持独立存储沙箱（专属 SQLite `data.db` 与专属 KV `kv.db`）、动态反向代理路由（`/w/{id}/*`）与防雪崩自愈。

本文针对真实运行环境（`http://10.126.1.70:5270/app/#/workers` 及 `/app/#/services`）进行全功能现场演练测试，开展可行性、逻辑闭环、性能开销与设计模式优化的头脑风暴，对比业界主流工业级方案，并给出落地实施与防回归策略。

---

## 二、 现场真实环境验证诊断与故障根因

### 2.1 现场验证执行轨迹与实测结果

通过内置浏览器（Chrome DevTools 自动化契约）与 API 矩阵对远端服务器执行全功能穿透测试：

| 服务模块 | 测项 | 现象 / 结果 | 诊断判定 |
| :--- | :--- | :--- | :--- |
| **SaaS 巡检守护** (`demo-sh-daemon`) | 启动 / 停止 / 重启 / 日志流 | 成功启动 (PID: 235106)，每 30 秒控制台输出心跳，RingBuffer 实时捕获日志流 | 🟢 **正常运行** |
| **SaaS KV 存储** (`demo-ps-daemon`) | Key-Value 增删改查 | 保存 `test_config_key` = `{"hello":"world"}` 成功，列表即时呈现并可删除 | 🟢 **正常运行** |
| **SaaS 服务向导** (`➕ 新建服务`) | 多语言模板切换 / 源码编辑 | 弹窗正常，Python/Node/C#/Bash/PS 模板即时载入，表单校验合法 | 🟢 **正常运行** |
| **SaaS Git 导入** (`🐙 Git 仓库导入`) | 单仓多服务扫描弹窗 | 弹窗正常，支持输入 Git 仓库 URL 与分支 | 🟢 **正常运行** |
| **SaaS 热更新** (`demo-ps-daemon`) | 版本与来源元数据呈现 | 正确识别本地创建来源 (`💻 本地创建`)，由于无 Git 上游自动安全禁用检查更新 | 🟢 **正常运行** |
| **运维扩展动作** (`ssh-access-guard`) | 自定义动作 (`📜 登录审计记录`) | 成功调用底层脚本并捕获输出 (`=== 最近成功登录记录...`) | 🟢 **正常运行** |
| **运维扩展环境** (`caddy-server-manager`) | 运行时缺失探测与引导 | 正确识别缺少 `node` 与 `caddy`，启动按钮安全禁用，安装引导弹窗提供 `apk add --no-cache nodejs caddy` | 🟢 **正常运行** |
| **SaaS .NET 极简服务** (`demo-dotnet-service`) | 启动服务 (`POST /api/Workers/{id}/Start`) | ❌ **失败**：界面弹出 `✕ 启动 Worker 'demo-dotnet-service' 失败，请检查日志。`；日志记录 `环境中未检测到 .NET SDK 运行时环境` | 🔴 **体验与逻辑缺陷** |
| **SaaS Python API** (`demo-py-api`) | 启动服务 | ❌ **失败**：容器内无 `python3`，启动失败，退避重试 | 🔴 **体验与逻辑缺陷** |
| **SaaS Node Webhook** (`demo-js-webhook`) | 启动服务 | ❌ **失败**：容器内无 `node`，启动失败，退避重试 | 🔴 **体验与逻辑缺陷** |
| **SaaS PS 守护** (`demo-ps-daemon`) | 启动服务 | ❌ **失败**：容器内无 `pwsh`，启动失败，退避重试 | 🔴 **体验与逻辑缺陷** |

---

### 2.2 核心根因与系统性缺陷分析

#### 缺陷 1：SaaS Worker 缺失前置依赖探测与状态呈现（用户体验断崖）
- **现象**：
  在「扩展服务」中，若缺少 `node` 或 `caddy`，卡片会醒目呈现 `⚠️ 缺少运行时` 徽标，明确提示 `缺少执行运行时: node`，启动按钮置灰，并提供 `🛠️ 安装引导` 弹窗一键复制宿主安装命令。
  但在「SaaS 函数服务」中，`WorkerInstanceDto.MissingDependencies` 字段**从未在后端被赋值计算**（永远为空数组 `[]`）。
  无论宿主是否安装了 .NET / Python / Node / PowerShell，卡片都呈现 `已停止`，启动按钮完全可点。用户点击启动后直接报 400 失败并闪现泛型错误提示，必须手动点进「日志」弹窗才能查到原因。
- **根因**：
  `WorkersManager.cs` 的 `GetAllAsync()` 和 `GetByIdAsync()` 仅反序列化 `worker.json`，从未调用 `adapter.CheckAvailable(...)` 进行前置环境校验；`IWorkerRuntimeAdapter` 的检查逻辑被孤立在 `StartProcessInternal` 私有方法中。

#### 缺陷 2：启动失败信息被吞没，API 缺乏可解释性错误输出
- **根因**：
  `WorkersController.Start` 中硬编码了泛型错误提示：
  ```csharp
  var success = await _workersManager.StartAsync(id, ct);
  if (!success) return BadRequest(new ApiResult<WorkerInstanceDto> { Success = false, Message = $"启动 Worker '{id}' 失败，请检查日志。" });
  ```
  底层适配器已生成精确的诊断文本（如 `系统中未检测到 .NET SDK 运行时环境，请先安装 .NET Core SDK。`），但该信息被 `bool` 返回值完全丢弃，未能直达前端 Toast。

#### 缺陷 3：缺失运行时的静态环境故障错误触发 CrashLoop 退避死循环
- **根因**：
  当环境检查失败时，`WorkersManager` 调用了 `sm.RecordExit(-1, missingReason)`。
  状态机将此视为一次**运行时进程崩溃 (Flapping)**，导致 `FastCrashCount++` 并将状态变迁为 `WorkerState.Backoff`（前端显示为 `⏳ 退避重试中`）。若配置了 `AutoRestart = true`，守护协程将在 1s、3s、10s 后不断重试拉起必崩的进程，大量刷爆无意义的错误日志并虚耗系统调度。
  缺少静态运行时属于**终端前置条件不满足 (Condition Check Failed)**，绝对不应进入自动重启退避。

#### 缺陷 4：路径反向代理转发 (`/w/{id}/*`) 存在 Hop-by-Hop 响应头崩溃隐患
- **根因**：
  在 `PipelineExtensions.cs` 中，代理转发将上游子进程返回的所有 Response Headers 无差别拷贝到 Kestrel 的 `context.Response.Headers`。
  当上游服务（如 Node.js / Python / .NET）返回分块传输 `Transfer-Encoding: chunked` 或 `Connection: keep-alive` 时，Kestrel 内核驱动会直接抛出：
  `System.InvalidOperationException: Setting the header 'Transfer-Encoding' is not supported on this response.`
  导致代理请求报 502/500 崩溃。

---

## 三、 头脑风暴与主流业界方案对比

### 3.1 业界主流方案调研对比

| 平台 / 工具 | 状态机设计 | 环境与依赖检测策略 | 自动重启 / 防雪崩策略 | 反向代理与网关处理 |
| :--- | :--- | :--- | :--- | :--- |
| **PM2 (Node.js)** | God Daemon 状态机 (`online`, `stopping`, `stopped`, `errored`, `launching`) | 仅支持 Node.js 及其解释器映射 (`interpreter`)，若二进制不存在直接标 `errored` | 指数退避，支持 `min_uptime` 与 `max_restarts` 阈值熔断 | 依赖第三方集群反代 |
| **Systemd** | 工业级事件驱动状态机 (`inactive`, `activating`, `active`, `deactivating`, `failed`) | 前置条件断言 (`ConditionPathExists`, `ConditionCapability`)，若二进制缺失报 `status=203/EXEC` 并**直接终止**，不触发 `Restart=always` | `StartLimitIntervalSec` 与 `StartLimitBurst` 熔断机制 | socket 激活机制 |
| **Cloudflare Workers** | V8 Isolate 沙箱状态机 | 编译构建期环境校验，缺失依赖在部署阶段阻断 | 边缘无状态，冷启动毫秒级拉起，无常驻崩溃概念 | Anycast 路由网关，自动剥离 HTTP/1.1 Hop-by-Hop 头 |
| **YARP (ASP.NET Core)** | 路由与集群健康状态机 | 主动与被动健康探测 (`HealthCheck`) | 熔断器与重试策略 | **严格过滤 Hop-by-Hop 协议头**，全异步流式转发 |

---

### 3.2 设计模式重构与架构优化构想

#### 1. 状态机模式 (State Pattern / FSM) 演进
在 `WorkerStateMachine` 中引入状态机细化分类：
- 区分**动态进程状态**（`Starting`, `Running`, `Crashed`, `Backoff`, `Degraded`）与**静态环境约束状态**（`MissingRuntime`, `Disabled`, `Stopped`）；
- 引入 `RecordPrecheckFailure(string reason)` 显式入口：
  - 变迁为 `WorkerState.MissingRuntime`；
  - 强制返回 `(ShouldRestart: false, Delay: TimeSpan.Zero)`，坚决杜绝死循环退避重启。

#### 2. 策略模式与适配器模式 (Strategy & Adapter Pattern) 演进
在 `IWorkerRuntimeAdapter` 中升级契约：
- 统一输出：
  - `PrimaryExecutable`（主程序名，如 `dotnet`, `python3`, `node`, `pwsh`, `bash`）；
  - `CandidateExecutables`（候选匹配程序表）；
  - `ResolveInstallGuide(string distroKey)`（跨系统安装命令自动解析）；
  - `CheckAvailable(out string? reason, out IReadOnlyList<string> missingExecutables)`。

#### 3. 读探测性能优化：带 TTL 的轻量环境探针缓存 (RuntimeProbeCache)
- 为防止每次前端调用 `GetAllAsync()` 或轮询时无脑启动子进程检测 `dotnet --version` 拖垮系统，引入单例 `RuntimeProbeCache`（60 秒有效 TTL，线程安全无锁读取）；
- 当用户显式点击「刷新」或触发环境变更时，即时失效缓存重新探测。

#### 4. 网关防线：Hop-by-Hop 头部剥离 (Gateway Hop-by-Hop Header Stripping)
- 遵循 RFC 7230 / RFC 9110 标准，反向代理转发响应时剔除：
  `Connection`, `Keep-Alive`, `Proxy-Authenticate`, `Proxy-Authorization`, `TE`, `Trailer`, `Transfer-Encoding`, `Upgrade`。

---

## 四、 具体落地实施规划

### 阶段 1：后端契约与核心状态机增强
1. [`src/LinuxWebTool.Contracts/Features/Workers/Contracts/WorkerModels.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.Contracts/Features/Workers/Contracts/WorkerModels.cs)：
   - `WorkerState` 枚举扩充 `MissingRuntime = 6`；
   - `WorkerInstanceDto` 扩充 `InstallGuideText` 属性；
2. [`src/LinuxWebTool.Infrastructure/Features/Workers/Adapters/IWorkerRuntimeAdapter.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.Infrastructure/Features/Workers/Adapters/IWorkerRuntimeAdapter.cs) & `WorkerRuntimeAdapters.cs`：
   - 统一提供可执行程序名、跨发行版安装引导文本解析器与带结构化缺失项的 `CheckAvailable`；
3. [`src/LinuxWebTool.Infrastructure/Features/Workers/Supervisor/WorkerStateMachine.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.Infrastructure/Features/Workers/Supervisor/WorkerStateMachine.cs)：
   - 新增 `MarkMissingRuntime(string reason)`，阻断 CrashLoop 退避重启；
4. [`src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.Infrastructure/Features/Workers/WorkersManager.cs)：
   - 引入 60 秒 TTL 的运行时探测缓存；
   - 在 `BuildDto` 中主动填补 `MissingDependencies`、`InstallGuideText` 与 `WorkerState.MissingRuntime`；
   - 新增 `StartWithResultAsync`，向控制器返还精确错误消息；
5. [`src/LinuxWebTool.WebHost/Features/Workers/Routes/WorkersController.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.WebHost/Features/Workers/Routes/WorkersController.cs)：
   - `Start` 接口返回真实底层诊断信息；
6. [`src/LinuxWebTool.WebHost/Composition/PipelineExtensions.cs`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.WebHost/Composition/PipelineExtensions.cs)：
   - 严格剥离 Hop-by-Hop 响应头，消除分块传输崩溃隐患。

### 阶段 2：前端交互与安装引导组件建设
1. [`src/LinuxWebTool.WebHost/wwwroot/app/components/workers/WorkerInstallGuideModal.js`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.WebHost/wwwroot/app/components/workers/WorkerInstallGuideModal.js)：
   - 新建 Worker 专有依赖安装引导弹窗组件，呈现当前宿主推荐命令与 Debian/Alpine/RHEL/Windows 一键复制；
2. [`src/LinuxWebTool.WebHost/wwwroot/app/views/WorkersView.js`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/src/LinuxWebTool.WebHost/wwwroot/app/views/WorkersView.js)：
   - 适配 `MissingRuntime` 状态呈现（琥珀色醒目徽标）；
   - 卡片增加 `🛠️ 安装引导` 快捷按钮；
   - 缺失运行时置灰启动按钮并给出微提示；
   - 接收后端 Start 接口返回的具体失败原因并予以 Toast 呈现；
   - 新建服务模板下拉列表中标注运行时可用性。

### 阶段 3：自动化测试与架构门禁
1. 单元测试覆盖：
   - 验证 `WorkerStateMachine` 在 `MissingRuntime` 下不触发退避自愈；
   - 验证 `WorkersManager` 对缺失运行时服务的 DTO 字段拼装与安装命令解析；
   - 验证反向代理响应头清理逻辑；
2. 运行 `frontend-gate.cjs` 与单元测试确保 100% 通过。

---

## 五、 验证准则
- 单元测试与架构测试全部通过；
- 前端门禁扫描 0 违规，符合 `< 400` 行规范与安全标准；
- Native AOT 编译 0 警告 0 错误；
- 在真实内置浏览器与实际调用中，Worker 缺失运行时即时感知、操作自愈、引导友好。
