# LinuxWebTool (LWT) 智能体协作指南 (AGENTS.md)

> 本文档专为后续跨会话的 AI Coding Agent 及开发者编写，旨在提供统一、标准化、即开即用的环境指引、双轨制部署流程、解耦推送策略与内置浏览器验收规范。

---

## 1. 核心架构与技术栈速览

- **后端**：.NET 10 WebHost (C# 14)，标准生产目标为 Alpine Linux Native AOT 独立无解释器 ELF 二进制。
- **前端**：纯原生 ESM 架构（Vanilla JS + CSS Variables），零构建打包工具（无需 Webpack/Vite/npm run build），按需动态路由加载。
- **系统底座**：宿主特权 Docker 容器，托管 PTY 伪终端动态库（C 语言原生 `liblinuxwebtool_pty.so`）、FRP、EasyTier、端口转发、Samba 共享、定时任务与插件微服务。
- **凭据隔离**：敏感环境参数（主机 IP、端口、SSH 私钥、管理员初始密码等）一律保存在 `env/.envdev` 中，由 `.gitignore` 严格忽略，**严禁提交至 Git**。

---

## 2. 部署双轨制（Dual-Track Deployment Pipeline）

为了彻底解决“正式 Native AOT 构建耗时过长（8~15 分钟）”与“日常迭代快速验证”的矛盾，系统提供 **双轨制并行架构**：

| 维度 | 方案 A：极速开发部署管线 (`-Mode Dev`) | 方案 B：官方正式发布管线 (`-Mode Official`) |
| :--- | :--- | :--- |
| **定位** | 日常开发、功能调试、Bug 修复、敏捷验收 | 正式发版、合并交付、生产环境发布 |
| **容器实例** | `linuxwebtool-dev` | `linuxwebtool` |
| **对外端口** | **5271** (`-p 5271:5270`) | **5270** (`--net=host`) |
| **构建方式** | 远程 Linux 本地 JIT 快速构建 (`Dockerfile.dev`) | GitHub Actions 云端 Multi-Arch Native AOT 编译 |
| **镜像标签** | `lwt:dev` (Alpine .NET 10 ASP.NET JIT) | `ghcr.io/csvkse/lwt:latest` (Alpine AOT) |
| **代码同步** | **解耦 Git 依赖**，本地工作区 SSH 流式直推 | Git Commit + Push Tag 触发云端 CI/CD |
| **发布耗时** | **约 40 ~ 50 秒**（增量编译 ~15 秒，前端热更 ~5 秒） | **约 8 ~ 15 分钟** |
| **生产影响** | **完全零影响**（5270 生产容器与业务全程保持运行） | 重建生产容器 |
| **数据目录** | 独立隔离卷 `linuxwebtool-dev-data` | 生产数据卷 `linuxwebtool-data` |
| **凭据同步** | 首次拉起自动从 5270 生产容器热同步 `admin.json` | 读取宿主初始配置或自动生成 |

### 敏捷开发部署决策矩阵 (Agile Decision Matrix)

AI Agent 在日常开发与修复排障时，**必须依据修改范围精准选择最小粒度的更新策略**，严禁过度构建或盲目重启：

| 变更范围 | 典型场景 | 推荐操作 | 耗时 | 容器停机时间 |
| :--- | :--- | :--- | :--- | :--- |
| **插件微服务逻辑/代码/配置** (`plugins/<id>/`) | MCP 智能安全网关、SOCKS5 代理、插件专属 webview 页面、`plugin.json`、Go/Node 业务代码修复 | **插件在线热更新** (`-HotUpdatePlugin <id>` 或 [`scripts/hot-update-mcp-bridge.ps1`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/scripts/hot-update-mcp-bridge.ps1)) | **~5-8 秒** | **0 秒（零容器停机）** |
| **主系统前端静态资源** (`src/.../wwwroot/`) | 页面样式、前端 Vue/ESM 组件、主系统视图布局 | **前端静态热更** (`-FrontendOnly`) | **~4-5 秒** | **0 秒（零容器停机）** |
| **主系统后端与底层底座** (`src/.../Application`, `Infrastructure`, `WebHost`) | C# 控制器、中间件、PtyNative、EasyTier 核心调度、主数据库结构变更 | **开发镜像快速重建** (`-Mode Dev`) | **~40-50 秒** | 重启 `linuxwebtool-dev` |
| **正式交付生产** | 功能全量回归、准备发布正式 Tag | **官方生产部署** (`-Mode Official`) | **~8-15 分钟** | 重启 `linuxwebtool` |

---

## 3. 两套方案具体使用方法

统一使用根目录自动化脚本 [`scripts/deploy-remote.ps1`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/scripts/deploy-remote.ps1) 执行所有操作：

### 方案 A：Linux 极速本地开发验证管线

#### 1. 全量工作区快速验证（默认推荐：Auto 智能自适应）
直接执行开发部署，无需手动指定参数：
```powershell
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -Mode Dev
```
> **自适应行为 (Auto)**：
> - **若 Git 工作区干净（Clean）**：自动选用 `GitArchive` 策略，打包最新的 HEAD Commit，享受 100% 纯净度与版本溯源标签。
> - **若存在未提交/未暂存修改（Dirty）**：自动切换为 `Direct` 策略，使用原生 `tar.exe` 毫秒级打包当前实时工作区（严格排除 `.git`、`bin`、`obj`、`env`、`node_modules`），确保本地刚刚修改的实时代码立即生效。

#### 2. 代码同步策略显式指定（备选方案）
支持通过 `-SyncMethod` 参数显式覆盖自适应逻辑：
```powershell
# 策略 1: Auto (默认) - 干净则 GitArchive，脏工作区则 Direct
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -Mode Dev -SyncMethod Auto

# 策略 2: Direct - 强制直推本地工作区（完全解耦 Git，包含未提交代码）
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -Mode Dev -SyncMethod Direct

# 策略 3: GitArchive - 强制仅打包本地最新的 Git 已提交 HEAD 节点
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -Mode Dev -SyncMethod GitArchive

# 策略 4: GitPull - 远程 Linux 主机直接从 GitHub 执行 git fetch & reset
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -Mode Dev -SyncMethod GitPull
```

#### 3. 前端静态资源秒级热更（Frontend HotSync - 5 秒极速）
如果仅修改了 HTML、CSS、JavaScript 视图或组件（`src/LinuxWebTool.WebHost/wwwroot` 目录下），**无需重新构建 Docker 镜像，无需重启容器**：
```powershell
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -FrontendOnly
```
> **效果**：约 4~5 秒完成双层热补丁（同时更新远程宿主机目录与容器内 `/app/wwwroot`）。完成后在浏览器直接按 `Ctrl + F5` 刷新页面即可查看改动！

#### 4. 微服务插件在线热更新（Plugin HotUpdate - 零容器停机，统一强制规范）

> [!IMPORTANT]
> **插件更新铁律（AI Agent 强制遵从准则）**：
> 凡涉及插件微服务本身的问题（例如 Go/Node 逻辑修复、OpenAI 隧道超时预算调整、SOCKS5 访问控制列表、manifest / [`plugin.json`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/plugins/mcp-bridge/plugin.json) 配置微调、插件专属 webview 页面修改等），**只要不是插件底座平台（Plugin Platform 自身架构抽象、控制器管线）的问题，统一且必须使用【插件热更新方案】！严禁滥用 `-Mode Dev` 重启/重建开发容器！**

- **为什么必须使用插件热更新**：
  1. **零容器停机（Zero Downtime）**：`linuxwebtool-dev` 容器全程保持运行，网络命名空间、FRP 穿透会话、PTY 终端以及其他已加载的常驻微服务状态绝不丢失。
  2. **极致秒级反馈**：耗时仅需 **5 ~ 8 秒**，相比全量镜像编译提速 80% 以上。
  3. **消除系统级抖动**：频繁重建 Docker 容器容易引发端口重新分配冲突、TUN/FUSE 驱动句柄泄露及守护进程竞态。

- **标准执行命令**：
  ```powershell
  # 方式 1: 专属一键热更脚本（针对高频更新的 MCP Bridge 智能安全网关）
  powershell -ExecutionPolicy Bypass -File scripts/hot-update-mcp-bridge.ps1

  # 方式 2: 统一脚本指定任意插件 ID（如 mcp-bridge, socks5-proxy 等）
  powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -HotUpdatePlugin mcp-bridge
  ```

- **底层四重保障机制 (Quadruple-Guarantee)**：
  1. **Linux 静态 ELF 预编译**：自动检测源文件 LastWriteTime，优先本地交叉编译 Linux 架构的 ELF 静态二进制（`CGO_ENABLED=0 GOOS=linux GOARCH=amd64`），彻底避免上传 Windows PE 造成远程无法执行。
  2. **原子 Inode 替换（Atomic Inode Swap）**：在容器运行卷 `/app/data/plugins/<id>/` 内，通过 `cp -> chmod +x -> mv -f` 实现原子覆盖，避免覆盖正在运行的二进制进程而遭遇 Linux 内核的 `Text file busy` (ETXTBSY) 崩溃。
  3. **中枢统一 REST API 协同重拉**：自动提取开发卷凭据，直接向插件平台发送 `POST /api/Plugins/<id>/Restart` 指令，状态机执行平滑退出、解绑旧 PID、重配监听并拉起新进程（耗时通常 < 800ms）。
  4. **终端控制序列清洗 (OSC 133 防御)**：自动化管线内置终端 ANSI / OSC 转义序列清洗器，阻断远程 Shell Prompt 字符干扰，确保新旧 PID 与 SHA256 校验 100% 精准无误。

---

### 方案 B：官方正式生产部署管线

当功能在开发环境验证完毕，准备正式发版交付时：
1. **本地门禁校验**：
   ```powershell
   node tests/frontend/plugin-filter-and-state-machine.test.mjs
   node src/LinuxWebTool.WebHost/wwwroot/frontend-gate.cjs
   ```
2. **提交代码并推送 Tag**：
   ```powershell
   git add .
   git commit -m "feat: your new feature"
   git tag vX.Y.Z
   git push origin main --tags
   ```
3. **等待 GitHub Actions 构建完成**（约 8~15 分钟，生成并发布 `ghcr.io/csvkse/lwt:latest`）。
4. **触发远程生产环境拉取与重建**：
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -Mode Official
   ```
   > 脚本将自动拉取官方最新 AOT 多架构镜像、平滑重建 `linuxwebtool` 容器、探测 `/health` 就绪，并自动核验/加速安装 5 套多语言插件运行时环境（Node, Python, .NET SDK, PowerShell, Caddy）。

---

## 4. 远程主机诊断与辅助命令

脚本内置了适配器辅助工具，可直接穿透执行诊断操作：

```powershell
# 检查远程主机连通性与容器运行状态（不执行任何写操作）
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -CheckOnly

# 在远程主机上安全执行任意 Shell 命令
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -RemoteExec "docker ps"
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -RemoteExec "docker logs --tail 30 linuxwebtool-dev"
powershell -ExecutionPolicy Bypass -File scripts/deploy-remote.ps1 -RemoteExec "curl -I http://127.0.0.1:5271/health"
```

---

## 5. 内置浏览器自动化验收规范 (Chrome DevTools MCP)

所有修改交付前，Agent 应优先使用内置浏览器进行自动化回归验收：

1. **连接与登录**：
   - 开发实例入口：`http://10.126.1.70:5271/app/#/login`
   - 生产实例入口：`http://10.126.1.70:5270/app/#/login`
   - 用户名：`admin`
   - 密码：从 `env/.envdev` 读取 `WEB_ADMIN_PASSWORD`（开发容器已单向同步此凭据）。
2. **核心巡检页面与路径**：
   - `#/plugins`：插件中心（验证常驻微服务与脚本扩展的卡片渲染、策略过滤搜索框、清空 `✕` 按钮与状态保护守卫）。
   - `#/system`：系统负载与硬件资源监控。
   - `#/easytier`：EasyTier 组网状态与 DHCP/静态 IP 呈现。
   - `#/gateway`：TCP/UDP 四层端口转发规则。
   - `#/terminal`：在线 PTY 终端交互。
3. **质量红线**：
   - 页面不得产生未捕获的 Console Error（`pageerror`）。
   - 列表容器不得突破逻辑视口高度限制（遵循 `FE-CONTAINER-HEIGHT-LIMIT` 门禁）。
   - 搜索框输入与一键清空不得破坏插件原始列表状态。

---

## 6. 容器运行时与终端环境防御规范

### 1. 终端 Shell 脚本换行符防御（CRLF vs LF）
- **背景**：Windows 环境下检出代码或直推时，若脚本携带 `\r\n` (CRLF)，Linux Bash 在加载 `--rcfile /app/terminal-bashrc.sh` 时会抛出 `syntax error near unexpected token $'{\r''` 语法错误。
- **三重防御机制**：
  1. **Git 属性约束**：根目录 [`.gitattributes`](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/.gitattributes) 强制声明 `*.sh text eol=lf`。
  2. **镜像构建清理**：`Dockerfile` 与 `Dockerfile.dev` 阶段在生成最终镜像时统一执行 `sed -i 's/\r$//' /app/terminal-bashrc.sh`。
  3. **C# 运行时自愈**：`CrossPlatformPtyEngine.DefaultLinuxArguments()` 在拉起终端前主动校验并清洗 `\r` 字符，彻底免疫外部文件写入污染。

### 2. 插件生态与 .NET 10 SDK 原生支持
- **统一目标版本**：插件中心微服务（如 `demo-dotnet-service`）全面升级至 `<TargetFramework>net10.0</TargetFramework>`，与主工程 .NET 10 架构对齐。
- **开箱即用（Zero-Config）**：极速开发镜像 `Dockerfile.dev` 直接从官方 build 层无缝提取完整的 **.NET 10 SDK (10.0.403)**（预置于 `/usr/share/dotnet` 并配置 PATH），容器内开发插件完全无需手动执行 `apt` 或 `apk` 安装。
- **发行版自适应引导**：若在无 SDK 环境运行，后端 `PluginManager.DetectCurrentDistroKey()` 会自动解析当前操作系统（Alpine / Debian / RHEL / Windows），精准输出该发行版的 .NET 10 安装指引，彻底消除在 Alpine 容器中误报 `apt: command not found` 的问题。

---

## 7. 凭据安全红线与脱敏规范 (Security & Credential Governance)

在后续所有任务、功能开发、排障文档编写以及测试用例编写中，**必须严格遵守以下凭据安全红线**：

### 1. 文档规范 (`docs/` 目录严禁真实凭据)
- **严禁真实密钥与密码**：编写技术方案、排障复盘（Lessons Learned）、API 调研或 curl 示例时，绝对禁止将真实的 OpenAI Key、平台管理员密码（如 `WEB_ADMIN_PASSWORD`）、隧道 ID、EasyTier 组网密钥 (`network_secret`) 或公网对端域名写入文档。
- **强制使用标准占位符**：
  - **Tunnel ID**：统一使用符合格式校验的虚拟占位符 `tunnel_0123456789abcdef0123456789abcdef`。
  - **API Key / Token**：使用 `sk-proj-test-secret-key-1234567890`、`sk-test-super-secret-12345` 或 `lwt_live_8f3a9e...`。
  - **管理员/系统密码**：使用 `your_secure_password_here` 或 `<ADMIN_PASSWORD>`。
  - **EasyTier 组网配置**：使用 `network_name = "demo_net"`, `network_secret = "demo_secret_token"`, `peers = ["wss://easytier-peer.example.com"]`。
  - **网络地址**：使用 RFC 5737 示例域名 `example.com` 或 `127.0.0.1`。

### 2. 测试用例规范 (`tests/` 与 `plugins/**/tests`)
- **单元与集成测试夹具**：默认使用纯内存/本地 Mock 凭据（如 `mockPrivateKey = "-----BEGIN OPENSSH PRIVATE KEY-----\nMOCK_KEY_DATA\n-----END OPENSSH PRIVATE KEY-----"`）。
- **动态密码防泄露**：端到端集成测试若需生成真实密码，必须动态随机生成（如 `[guid]::NewGuid().ToString('N')`），并在测试日志持久化时显式执行脱敏过滤（如 `.Replace($password, '[REDACTED]')`）。

### 3. 真实密钥与环境连接加载规范 (`/env/.envdev`)
- **唯一合法来源**：若本地联调、端到端冒烟测试或远程诊断**确需使用真实密钥与宿主连接信息**，必须且只能从项目根目录的 `/env/.envdev` 文件（或读取系统当前环境变量）中动态提取。
- **Git 隔离防护**：`/env/.envdev` 已被根目录 `.gitignore` 全局严格忽略（`env/`, `.envdev`, `*.envdev`），绝对禁止将其提交到版本库中。
- **环境回退机制**：测试脚本在未检测到 `/env/.envdev` 中的特定凭据时，应设计优雅降级或跳过远程鉴权逻辑，绝不允许通过在代码中硬编码真实密钥来“临时跑通”测试。

### 4. 自动化提交门禁与验证机制
- **Git 预提交钩子 (Pre-commit Hook)**：仓库通过 `git config core.hooksPath .githooks` 启用了 `.githooks/pre-commit`。每次 `git commit` 时会自动调用 `.githooks/security-gate.cjs --staged` 对暂存区增量行进行香农熵与凭据规则检测，毫秒级阻断违规提交。
- **架构测试与 CI 兜底**：后端架构门禁 `LinuxArch068`、本地验证脚本 `./scripts/verify-fast.ps1` 与 GitHub Actions CI 会执行 `node .githooks/security-gate.cjs --full` 进行全仓深度核验，确保即使绕过本地钩子亦无法合入主干。

