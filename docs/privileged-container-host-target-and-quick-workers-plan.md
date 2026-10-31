# 特权容器宿主穿透对象模型与服务平台单文件快速模式深度调研与架构方案

## 1. 背景与核心痛点

随着系统在容器化与微服务环境下的广泛部署，特别是以 Docker 特权模式（`--privileged --pid=host --net=host`）运行时，系统面临两大核心维度痛点：

### 痛点 A：特权容器下的“假隔离”与宿主操作断层
1. **终端与指令隔离脱节**：
   在特权容器内部启动的终端交互会话（PTY）和指令执行器（`ShellExecutor`），默认运行在容器的独立 Mount/UTS 命名空间。运维人员登录 WebTool 无法直接维护宿主机本身，必须手动在容器终端内输入冗长的 `nsenter` 命令才能切换至宿主环境。
2. **定时任务范围受限**：
   当前的定时任务（Quartz / Cron）仅能在容器内执行，无法直接在宿主上下文中调度宿主原生进程或操控宿主硬件/存储。
3. **扩展服务（如 SSH 安全卫士）失效风险**：
   `ssh-access-guard` 等安全与运维扩展服务需要审计 `/var/log/auth.log` 并操作 `/etc/hosts.deny`、`ufw` 或 `iptables`。当在容器中运行时，如果无法选择操作宿主，修改的只是容器内部孤立的文件和规则，完全无法防御宿主机遭受的 SSH 暴力破解攻击！
4. **安装引导缺乏操作闭环**：
   扩展服务的依赖安装引导仅提供只读命令复制，不支持根据当前运行环境（宿主 vs 容器）自由编辑并一键直通终端执行。

### 痛点 B：多文件脚手架对外部 MCP 与轻量脚本的协作摩擦
1. **多文件结构繁琐**：
   当前扩展服务（Extensions）和函数微服务（Workers）要求标准的完整目录结构（`manifest.json` / `worker.json`、`index.js`、子目录等）或 Zip 包，对于临时编写一个仅数十行的巡检探针、Webhook 回调或数据同步脚本过于厚重。
2. **MCP 工具接口调用摩擦**：
   外部 AI Agent（通过 MCP 协议）在自动化创建或调试 Worker 时，必须组装复杂的结构化对象，缺乏直接传入“单个脚本代码”即可一键运行并自动自愈解析的极速通道，且 MCP 工具元数据中缺乏预置模板和上下文文档引导。

---

## 2. 深度头脑风暴：可行性、逻辑缺陷与性能分析

### 2.1 容器穿透宿主机制：可行性与边界防护

#### 2.1.1 `nsenter` 宿主命名空间全穿透机制
Linux 内核提供 `setns(2)` 系统调用，可通过 `nsenter` 工具基于进程号切换命名空间：
```bash
nsenter -t 1 -m -u -i -n -p -- /bin/bash --login
```
- `-m` (mount namespace)：进入宿主机根目录挂载树，获取 `/etc`, `/var`, `/home`, `/root` 完全控制权；
- `-u` (uts namespace)：进入宿主主机名域；
- `-i` (ipc namespace)：共享宿主 System V IPC 与 POSIX 消息队列；
- `-n` (net namespace)：直接操控宿主物理网卡、iptables、nftables 与网络套接字；
- `-p` (pid namespace)：穿透进入宿主 PID 命名空间，可向宿主机所有进程（包括 PID 1）发信号与监控；
- `--login`：加载宿主机 `/etc/profile`、`~/.bashrc` 与完整的环境变量。

#### 2.1.2 逻辑欠缺与关键风险防范
| 风险点 | 缺陷表现 | 架构级防范措施 |
| :--- | :--- | :--- |
| **非特权模式穿透失败** | 容器缺少 `CAP_SYS_ADMIN` 或未挂载 `/proc/1/ns` 时执行 `nsenter` 导致崩溃 | 引入**状态机守卫**：`ServiceEnvironmentDetector` 提前判定 `IsPrivilegedContainer`，若为 false，前端与后端严格禁用 `host` 目标并显式告警提示 |
| **磁盘写入竞争与脚本污染** | 将临时脚本写入宿主 `/tmp` 可能与宿主机既有同名文件冲突或残留垃圾 | **零磁盘落地执行策略**：通过管道流执行 `nsenter ... /bin/bash -s arg1 arg2 < script_stream`，宿主磁盘零写入 |
| **环境变量污染** | 容器环境变量覆盖宿主机 PATH 导致命令找不到 | 执行宿主命令时强制隔离容器环境，优先使用宿主原生环境或显式注入指定前缀 |

---

### 2.2 单文件快速模式（Single-File Mode）：可行性与解析规范

#### 2.2.1 业界主流方案对比
* **Cloudflare Workers**：支持单文件 `export default { fetch(...) }`，通过模块元数据自省；
* **Deno Deploy / Bun**：单文件即服务，通过头部注释或直接自执行；
* **Python PEP 723 (Inline script metadata)**：在脚本头部通过 TOML/注释嵌入依赖与元信息。

#### 2.2.2 本系统单文件规范设计（Self-Contained Header Frontmatter）
支持单一 `.js`、`.py`、`.sh` 文件作为一个独立 Worker 或 Extension，脚本顶部支持轻量级 JSDoc 格式与 `# @key: value` 格式注释：
```javascript
/**
 * @name 磁盘空间智能巡检
 * @description 监控宿主机 /var/log 和 /tmp 空间，达到 90% 阈值时触发自动归档
 * @runtime node
 * @mode daemon
 * @target host
 * @schedule 0 */2 * * *
 * @version 1.0.0
 */
const fs = require('fs');
// ... 业务脚本
```
**性能优势**：
- 正则预编译单次扫描文件前 2KB 即可完成提取，耗时 $< 0.1\text{ms}$；
- 避免生成多层物理目录，极度适合 MCP Agent 快速生成、临时调试、在线替换与一键销毁。

---

## 3. 设计模式优化与架构设计

```mermaid
flowchart TD
    subgraph UI [交互层 (Presentation)]
        TerminalUI["终端视图 (Host/Container 切换 Tab)"]
        CmdUI["指令管理 (Target: 宿主 / 容器)"]
        CronUI["定时任务 (Target 继承或指定)"]
        GuideUI["安装引导 (可编辑命令 + 目标切换 + 一键跳转终端)"]
        QuickWorkerUI["单文件快速创建编辑器"]
    end

    subgraph Strategy [执行目标策略模式 (Strategy Pattern)]
        ITargetStrategy["ITargetExecutionStrategy"]
        HostStrategy["HostNsenterStrategy (nsenter 穿透宿主)"]
        ContainerStrategy["ContainerNativeStrategy (容器原生隔离)"]
        ITargetStrategy --> HostStrategy
        ITargetStrategy --> ContainerStrategy
    end

    subgraph StateMachine [环境就绪状态机 (State Machine)]
        EnvDetector["ServiceEnvironmentDetector"]
        ReadyState{"是否具备特权穿透?"}
        DualTargetReady["状态: DualTargetReady (宿主 + 容器)"]
        ContainerOnly["状态: ContainerOnly (仅容器)"]
        HostOnly["状态: HostOnly (物理裸机/虚拟机)"]
        EnvDetector --> ReadyState
        ReadyState -- 是 --> DualTargetReady
        ReadyState -- 否(容器内) --> ContainerOnly
        ReadyState -- 否(非容器) --> HostOnly
    end

    subgraph CorePlatform [核心支撑层 (Core Infrastructure)]
        ShellExecutor["ShellExecutor (支持 Target 参数)"]
        PtyEngine["CrossPlatformPtyEngine (支持 Host Shell)"]
        ExtensionMgr["ScriptExtensionManager (注入 LWT_TARGET)"]
        WorkerMgr["WorkersManager (支持单文件自愈包装)"]
        McpProvider["Services & Workers MCP Providers (增强 Schema & 模板)"]
    end

    UI --> Strategy
    Strategy --> CorePlatform
    StateMachine --> Strategy
```

### 3.1 策略模式 (Strategy Pattern)：统一执行目标路由
定义 `ExecutionTarget` 枚举：`Host` 与 `Container`。
`ITargetExecutionStrategy`：
- `ContainerNativeStrategy`：常规本地子进程执行；
- `HostNsenterStrategy`：自动包裹 `nsenter -t 1 -m -u -i -n -p --` 宿主前缀，并处理登录环境重定向与参数转义。

### 3.2 状态机模式 (State Machine)：环境自适配状态机
环境探测器负责驱动系统状态：
1. `HostOnly`：物理裸机或虚拟机，仅宿主对象可用，UI 隐藏容器切换控件；
2. `DualTargetReady`：检测到为特权容器（`isPrivilegedContainer == true`），UI 激活“宿主 / 容器”双重目标选择器；
3. `ContainerOnly`：普通 Docker 容器，UI 禁用宿主切换并提示“当前容器未启用 --privileged 特权，无法穿透宿主”。

### 3.3 适配器模式 (Adapter Pattern)：单文件虚拟化适配器
`SingleFileWorkerAdapter` 将单个文件（`.js` / `.py` / `.sh`）通过内存元数据自省，透明适配为平台标准的 `WorkerInstance`，对外暴露统一的启停、日志、指标与自愈接口，平台内部核心代码零改动。

---

## 4. 详细实施方案

### 4.1 终端 (Terminal)、指令 (Commands) 与定时任务 (Schedules)

1. **终端 PTY 宿主支持**：
   - `PtyStartOptions` 增加 `string? Target`（`"container"` / `"host"`）；
   - 在 `CrossPlatformPtyEngine.ResolveShell` 中：
     若 `options.Target == "host"` 且检测到特权模式，解析执行可执行程序为 `/usr/bin/nsenter`，参数序列为 `["-t", "1", "-m", "-u", "-i", "-n", "-p", "--", "/bin/bash", "--login"]`；
   - 前端 `TerminalView.js`：新建会话下拉框中增加「🖥️ 宿主终端 (Host)」与「📦 容器终端 (Container)」，切换一目了然。
2. **指令执行器 ShellExecutor**：
   - `ShellRequest` 扩展 `public string Target { get; set; } = "container";`；
   - `CommandEntity` 增加 `target` 字段；
   - 若 `Target == "host"`，自动通过 `HostNsenterStrategy` 封装执行命令与脚本。
3. **定时任务 Schedules**：
   - 定时任务继承关联指令的 `Target`，或支持显式重写；
   - 执行时自动传递目标参数，实现宿主定时任务调度。

---

### 4.2 动态扩展服务 (SSH 卫士等) 与函数服务的通用对象切换

1. **统一环境变量注入契约**：
   框架在运行任何扩展的 hooks 脚本或启动 Worker 进程时，注入以下环境上下文：
   * `LWT_TARGET`: `"host"` 或 `"container"`
   * `LWT_IS_PRIVILEGED`: `"true"` 或 `"false"`
   * `LWT_NSENTER_PREFIX`: 特权可用时为 `"nsenter -t 1 -m -u -i -n -p -- "`，否则为空
2. **SSH 访问安全卫士 (`ssh-access-guard`) 改造**：
   - `manifest.json` 声明支持 Target 切换；
   - 在特权容器下默认将执行目标绑定为 `host`；
   - 脚本通过 `nsenter` 或框架注入环境直接获取宿主机 `/var/log/auth.log`、向宿主 `/etc/hosts.deny` 与宿主 `ufw/iptables` 写入规则，实现真正在容器内守卫宿主机！

---

### 4.3 扩展服务与函数服务安装引导升级

1. **可编辑命令区域**：
   `ExtensionInstallGuideModal.js` 中的推荐命令由静态文本改为可编辑文本输入框（Textarea），用户可自定义修改命令（如镜像源、参数）；
2. **目标切换药丸组**：
   若 `env.isPrivilegedContainer` 为 true，提供单选切换：
   * 🖥️ **宿主机执行 (Host)**（推荐，安装宿主环境工具）；
   * 📦 **容器内执行 (Container)**（安装容器内部组件）；
3. **一键跳转终端执行**：
   新增按钮「💻 在终端中执行」：
   * 点击后自动跳转或打开悬浮终端抽屉；
   * 自动携带目标类型（`host` / `container`）与待执行命令；
   * 终端自动键入命令，由用户直接交互观察安装进度并回车确认。

---

### 4.4 单文件快速模式与 MCP 深度协同优化

1. **单文件 Worker / Extension 解析与管理**：
   - 平台新增单文件专属目录 `workers/quick/` 与 `extensions/quick/`；
   - 支持上传或在线保存单个 `.js`、`.py`、`.sh` 文件；
   - 预编译正则自省提取头部元数据；
   - `WorkersManager` 与 `ScriptExtensionManager` 统一管理单文件生命周期。
2. **MCP 工具接口优化与模板注入**：
   - 新增专用 MCP 工具：`worker_quick_create` 和 `extension_quick_create`，只需传 `filename`、`code`、`target`（可选）；
   - 在所有 Worker/Extension MCP 工具描述中：
     - 详细说明参数类型、有效范围和执行原理；
     - 注入经典开箱即用模板示例（Node.js Minimal HTTP, Python Background Daemon, Bash Cron Probe）；
     - 提供文档访问 URL：`http://localhost:5000/docs/workers-and-extensions-guide`，方便 AI Agent 自主探索与准确调用。

---

## 5. 预期效益与演进路线

1. **运维能力实现质的飞跃**：彻底打破 Docker 特权容器与宿主机的操作壁垒，运维人员无需离开 WebTool 即可无缝穿透宿主完成从底层系统内核到上层业务的全栈治理；
2. **AI 与自动化 Agent 效率倍增**：单文件快速模式使 MCP Agent 创建、修改、排障函数的调用链路缩短 70%，大幅降低 Token 消耗与失败率；
3. **极致质量保证**：通过策略模式与状态机防范误操作与非特权容器越界风险，全流程保持原生 AOT 零警告兼容与前端严格门禁规范。
