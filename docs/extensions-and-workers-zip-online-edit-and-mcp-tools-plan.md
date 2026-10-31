# 动态脚本扩展与函数微服务：ZIP 导入、在线代码编辑与 MCP 运维开发工具集方案调研与设计

## 1. 需求背景与痛点分析

当前 LinuxWebTool (LWT) 已经构建了双引擎架构：
1. **运维扩展服务 (Script Extensions)**：负责宿主系统运维、守护管理与轻量脚本集成（如 SSH 安全防护、Caddy 管理、进程监控等）；
2. **函数微服务与守护平台 (Workers)**：负责长时驻留微服务与轻量 API（如 Node.js Webhook、Python 数据分析 API、.NET 极速微服务、Shell/PowerShell 自动化守护等）。

在当前的实际交付与使用场景中，存在以下核心演进诉求：
1. **扩展服务 ZIP 离线/本地快速导入**：目前 Workers 已支持从本地 ZIP 压缩包导入，而 Extensions 仅支持从 Git 远程仓库拉取；在离线专网、内部自研打包或无需推 Git 的场景下，运维人员亟需直接上传 `.zip` 压缩包一键部署扩展；
2. **在线代码查看与实时编辑 (Online Code Editing)**：开发者与运维专家希望直接在 Web 控制台快速查看并微调脚本（如修改 `worker.js`、`hooks/start.ps1`、`manifest.json`），免于每次都必须通过 SSH 或终端手动寻找物理目录；
3. **安全防护与版本韧性 (Safety & Memento Pattern)**：在线直接修改运行中脚本极易引发语法错误导致服务中断；必须引入自动备份与快照备忘录机制，支持保存后自动热重启与错误时一键回滚；
4. **瞬态隔离调试运行 (Debug Run)**：无需在生产守护主进程中硬触发，提供瞬态沙箱调试能力，输入测试参数后捕获实时 stdout、stderr、执行耗时与退出码；
5. **AI 智能运维与 MCP 工具集打通**：Antigravity 与外部 LLM 智能体通过 MCP 协议管理系统时，需要不仅能管理容器/服务启停，更能通过 MCP 远程“读代码、改代码、建服务、查日志、调运行”，实现全生命周期 AI 编程闭环。

---

## 2. 业界主流方案调研与对比

| 平台 / 方案 | 导入机制 | 代码在线编辑 | 历史版本与回滚机制 | 调试运行 (Debug Run) | 智能体 / API 开放能力 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| **AWS Lambda** | ZIP 上传 (S3 / Console)、容器镜像 | 基于 Monaco Editor，仅支持单文件/小包编辑 | 基于 S3 版本控制与 Publish Version 别名机制 | Test Event 模拟触发，捕获 CloudWatch 日志与 Execution Result | 完整的 AWS SDK / CLI 与 Bedrock MCP 工具集成 |
| **Cloudflare Workers** | Wrangler CLI、ZIP、Web 界面 | 内置 Web Editor (Quick Edit / Playground) | 自动记录 Deployment History，支持 Instant Rollback | Real-time Logs 与 Playground 隔离测试请求 | Workers AI、REST API 与 MCP 扩展支持 |
| **Azure Functions** | ZIP Deploy、Run-From-Package | 门户 Code + Test (App Service Editor) | 基于 Kudu 部署历史记录 | Test/Run 侧边栏，支持 Query/Header 调试调用 | Azure Developer CLI 与 REST 管理端点 |
| **OpenFaaS** | Docker 镜像、CLI 模板 | 无原生在线编辑，依赖 CLI / CI/CD | 依赖镜像 Tag 回滚 | `faas-cli invoke` 命令行测试 | OpenFaaS REST API |
| **LinuxWebTool (本方案)** | **Git 仓库探针 + ZIP 压缩包双轨安全导入** | **轻量内嵌代码编辑器 + 目录树多文件穿梭** | **备忘录模式 (Memento Pattern) 自动生成时间戳快照，支持无损回退** | **策略模式 (Strategy Pattern) 瞬态轻量子进程隔离运行，捕获 stdout/stderr/耗时** | **全套双向 MCP 工具集 (`worker_*` / `extension_*`)** |

---

## 3. 架构设计与设计模式应用

### 3.1 备忘录模式 (Memento Pattern) —— 安全代码编辑与历史回退
- **痛点**：在线修改代码一旦包含语法错误（如缺少括号、语法异常），将导致常驻进程崩溃或脚本无法解析；
- **设计**：在写入任何代码或配置文件前，管理器自动提取原文件内容生成备忘录快照存储于当前目录的 `.bak_{filename}_{timestamp}` 中，最多保留 5 份历史快照。
- **结构**：
  ```mermaid
  sequenceDiagram
    autonumber
    actor User as 用户 / MCP Agent
    participant Route as Controller / MCP Provider
    participant Mgr as Manager (Workers / Extensions)
    participant Memento as Backup Memento Store
    participant Disk as Local FileSystem

    User->>Route: POST SaveCode(file, newContent, autoRestart)
    Route->>Mgr: SaveCodeAsync(...)
    Mgr->>Mgr: 校验安全路径 (Path Traversal 防护)
    Mgr->>Disk: 读取原文件内容
    Mgr->>Memento: 写入 .bak_{filename}_{timestamp} 快照
    Mgr->>Disk: 写入 newContent
    opt 启用 autoRestart
      Mgr->>Mgr: 触发平滑重启 / 钩子刷新
    end
    Mgr-->>User: 保存成功 (返回最新版本与备份快照元数据)
  ```

### 3.2 策略模式 (Strategy Pattern) —— 瞬态多语言调试执行器
- **痛点**：Worker 支持 Node.js、Python、.NET、Shell、PowerShell 等多种语言环境；Extension 的 Hooks 支持 Bash、Python、Node、PowerShell；调试运行必须是瞬态的，不能常驻端口，不能触发 CrashLoop 累加；
- **设计**：针对不同的运行时构建调试执行策略（Debug Runner Strategy）：
  - 瞬态沙箱执行，设定硬超时限制（默认 5~10 秒）；
  - 实时重定向与收集 stdout、stderr、执行耗时 (ms) 与退出码 (exitCode)；
  - 隔离环境变量，防止修改系统宿主核心环境变量。

### 3.3 适配器模式 (Adapter Pattern) —— 统一路径与安全校验适配
- 对 Workers 与 Extensions 的内部目录提供统一的路径安全适配器：
  - `NormalizeAndValidatePath(string baseDir, string relativePath)`：
    - 严格校验 `Path.GetFullPath(combined).StartsWith(baseDir)`，杜绝 `../` 或符号链接越权逃逸；
    - 文件白名单与黑名单过滤：严禁访问或覆写私钥、证书、隐藏系统凭证；
    - 大小硬限制：限制单文件编辑大小 $\le 2$ MB，防止内存撑爆。

### 3.4 状态机模式 (State Machine) 协同
- 结合此前已有的 `WorkerStateMachine`：
  - 当通过 Web 或 MCP 编辑代码并指定 `autoRestart: true` 时，状态机无缝流转：`Running -> Stopping -> Stopped -> Starting -> Running`；
  - 若编辑后出现启动异常，状态机平滑进入 `Degraded` 或 `MissingRuntime`，保留错误日志，用户可通过备忘录快照一键回退代码并重新自愈。

---

## 4. 详细接口设计与数据契约

### 4.1 扩展服务 ZIP 导入
- **路由**：`POST /api/Services/Extensions/Import/Zip` (`multipart/form-data`)
- **逻辑**：
  1. 接收上传的 `.zip` 文件流；
  2. 解压到 `Path.GetTempPath()/lwt_ext_zip_{guid}`，全流程执行 Zip Slip 前缀校验；
  3. 递归扫描 `manifest.json`，支持单扩展直接安装或多扩展批量导入；
  4. 保护原已存在的 `config.json`；
  5. 记录 `source.json`（标记 `sourceType: "zip"`）；
  6. 清理临时解压目录并返回导入结果。

### 4.2 在线代码查看与编辑 API
- **Workers**：
  - `GET /api/Workers/{id}/Code?file=worker.js`：获取代码内容、文件大小、最后修改时间及同级可编辑文件列表；
  - `POST /api/Workers/{id}/Code`：保存代码（入参：`filePath`, `content`, `autoRestart`）；
- **Extensions**：
  - `GET /api/Services/Extensions/{id}/Code?file=hooks/start.sh`：获取代码内容及文件树；
  - `POST /api/Services/Extensions/{id}/Code`：保存代码；

### 4.3 调试运行 API
- **Workers**：
  - `POST /api/Workers/{id}/Debug`：入参 `{ "args": "...", "timeoutSeconds": 5 }`，瞬态执行并返回 `{ "exitCode": 0, "stdout": "...", "stderr": "...", "elapsedMs": 120 }`；
- **Extensions**：
  - `POST /api/Services/Extensions/{id}/Debug`：入参 `{ "hook": "status", "params": { ... }, "timeoutSeconds": 5 }`；

### 4.4 扩展的 MCP 工具全景矩阵

| 工具提供者 | 新增 MCP 工具名 | 工具描述与参数 | 核心价值 |
| :--- | :--- | :--- | :--- |
| **WorkersMcpToolProvider** | `worker_get_code` | `worker_id`, `file` (可选) | AI 读取微服务代码与配置文件 |
| | `worker_edit_code` | `worker_id`, `file`, `content`, `restart` | AI 在线重构或修复微服务代码，自动快照 |
| | `worker_create` | `worker_id`, `name`, `runtime`, `code`, `mode` | AI 一键创建全新微服务实例 |
| | `worker_debug_run` | `worker_id`, `args`, `timeout_seconds` | AI 运行测试并分析调试输出 |
| **ServicesMcpToolProvider** | `extension_get_code` | `extension_id`, `file` (可选) | AI 读取运维扩展脚本与清单配置 |
| | `extension_edit_code` | `extension_id`, `file`, `content` | AI 在线调整扩展运维脚本，自动快照 |
| | `extension_create` | `extension_id`, `name`, `runner`, `start_script` | AI 快速生成自定义运维扩展包 |
| | `extension_get_logs` | `extension_id`, `lines` | 补全运维扩展日志读取能力 |
| | `extension_debug_run` | `extension_id`, `hook_name`, `params` | AI 触发特定动作调试并校验输出 |

---

## 5. 前端架构与门禁规范遵循

1. **单文件行数严格门禁 (FE-FILE-SIZE-LIMIT)**：
   - 视图文件上限 2000 行（`WorkersView.js` 当前 1258 行）；
   - 组件文件上限 400 行（`ExtensionsSubmodule.js` 当前 364 行，必须抽离子组件，绝不超标）；
   - 独立新建轻量组件：
     - `src/LinuxWebTool.WebHost/wwwroot/app/components/workers/WorkerCodeModal.js` (< 350 行)；
     - `src/LinuxWebTool.WebHost/wwwroot/app/components/services/ExtensionCodeModal.js` (< 350 行)；
     - `src/LinuxWebTool.WebHost/wwwroot/app/components/services/ExtensionZipImportModal.js` (< 200 行)；
2. **API 统一契约 (FE-API-SIGNATURE)**：
   - 在 `config.js` 统一声明 API 端点函数；
   - 严格区分无参直接获取与带参数函数调用。

---

## 6. 验证与回归测试策略
1. **单元测试 (UnitTests)**：
   - `ScriptExtensionManagerZipTests`：覆盖单扩展 ZIP 与多扩展 ZIP 导入、Zip Slip 恶意攻击拦截测试；
   - `CodeEditorAndBackupTests`：验证备忘录模式快照生成、越权路径拦截、恢复与保存；
   - `DebugRunTests`：验证瞬态超时控制与标准输出捕获；
   - `McpToolsTests`：验证新增 9 个 MCP 工具的参数解析与执行。
2. **架构测试 (ArchitectureTests)**：
   - 确保 Contracts、Infrastructure 与 WebHost 依赖解耦；
   - Native AOT 序列化上下文覆盖所有新增 DTO。
3. **前端门禁 (frontend-gate.cjs)**：
   - 确保 0 违规，扫描全量前端脚本文件。
