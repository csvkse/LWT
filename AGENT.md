# LinuxWebTool (LWT) 智能体协作指南 (AGENT.md)

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
