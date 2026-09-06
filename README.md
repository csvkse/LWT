# LinuxWebTool · Linux 指令控制台

个人 Linux 运维工具：把常用 Linux 指令沉淀为「可命名、分组、置顶」的功能一键执行，支持 Cron 定时调度、完整执行历史与三类日志。参考 `DNSPodForNETCore(InfiniWeb)` 的分层架构与前后端门禁体系，按个人工具规模做了裁剪。

> ⚠️ **安全提示**：本工具可在网页上远程执行任意 shell，**务必只部署在内网/本机，不要暴露公网**。认证为单管理员 + JWT。

## 功能

| 模块 | 能力 |
|---|---|
| 指令 | 保存 Linux 指令为功能；增删改查；分组 / 命名 / 置顶；一键执行（超时可配、输出截断 64KB、并发上限 4） |
| **Bash 脚本** | 支持多行脚本类型：写临时文件 `bash script.sh $1 $2...` 执行（位置参数、引号感知拆分、不经二次 shell 解释）；Windows 开发机自动探测 Git Bash |
| 快速执行 | 临时指令不保存直接跑，自动记入调用历史 |
| 定时任务 | 引用已保存指令/脚本 + Cron（支持 Unix 5 段 / Quartz 6 段，自动归一化）；启停 / 立即运行 / 下次执行时间；常用 Cron 预设 |
| **系统状态** | 即时查看：CPU / 内存 / 磁盘挂载点 / 网卡速率 / 进程 TOP / 主机内核信息；10s 自动刷新；历史曲线（后台 60s 采样入 SQLite，保留 7 天，uPlot 渲染，1h~7d 区间切换） |
| 执行历史 | 手动 / 定时 / 快速三类记录；状态筛选、关键字搜索、分页；失败详情（stdout/stderr/退出码/耗时） |
| 日志 | 操作日志（DB，全行为审计）+ 程序日志（`logs/app-*.txt`）+ 调试日志（`logs/debug-*.txt`，网页 tail 查看） |
| 门禁 | 单管理员登录签发 JWT（HS256，默认 12h）；登录失败 10 次锁 IP 5 分钟；全部 API 需认证 |

## 技术栈

- **后端**：.NET 10 / ASP.NET Core Controller + SqlSugar(CodeFirst, SQLite) + Quartz.NET(内存调度) + JwtBearer
- **前端**：零构建 Vue3 ESM（本地 vendor 自托管）+ vue-router(hash) + Tailwind（本地 Play 脚本）——**内网零外网依赖**
- **架构**：`Contracts(零依赖) → Infrastructure → WebHost(组合根+前端)`，xUnit 架构测试 + Node 前端门禁强制约束

## 目录结构

```
LinuxWebTool.slnx
├── Directory.Build.props / Directory.Packages.props   # 统一 TFM 与包版本（CPM）
├── src/
│   ├── LinuxWebTool.Contracts/        # DTO / 枚举 / IShellExecutor（零依赖，门禁强制）
│   ├── LinuxWebTool.Infrastructure/    # Persistence(SQLite)/Shell/Logging/Scheduling/Security
│   └── LinuxWebTool.WebHost/           # Program + Composition + Routes(7 组 API) + wwwroot/app(前端)
├── tests/LinuxWebTool.ArchitectureTests/  # 后端架构门禁（6 条规则）
├── scripts/verify-fast.ps1             # 一键门禁：build + test + 前端 gate
├── scripts/publish.ps1                 # 发布 linux-x64 自包含产物
├── .github/workflows/ci.yml            # CI：构建+门禁+GHCR 镜像（push/PR 触发）
├── .github/workflows/desktop-release.yml  # 桌面端多平台构建发布（tag v* 触发 GitHub Release）
├── Dockerfile                          # Docker 部署（三阶段，alpine）
└── docs/dev/architecture-gates.md      # 门禁规则文档
```

## 快速开始（Windows 开发机）

```powershell
dotnet build LinuxWebTool.slnx
dotnet run --project src/LinuxWebTool.WebHost
# 浏览器打开 http://localhost:5270/app/
```

首次启动自动生成管理员：用户名 `admin`，随机密码打印在程序日志并写入 `src/LinuxWebTool.WebHost/data/admin.json`。也可在 `appsettings.json` 预置：

```json
{ "Admin": { "UserName": "admin", "Password": "你的密码" } }
```

**支持环境变量注入凭据**（优先级最高，每次启动生效，修改后重启即换号/换密码）：

```powershell
# Windows（PowerShell: $env:Admin__Password="xxx"）/ Linux systemd: Environment=Admin__Password=xxx
Admin__UserName=ops Admin__Password=你的密码 dotnet run
docker run -e Admin__UserName=ops -e Admin__Password=你的密码 linuxwebtool
```

凭据优先级：`Admin__UserName`/`Admin__Password` 环境变量或 appsettings 显式配置 **>** `data/admin.json`（自动生成密码的持久化，记录最后一次生效的凭据）**>** 首次启动随机生成。

**网页修改凭据**：登录后点右上角用户名旁的 ⚙，可修改用户名 / 密码（需验证当前密码，改完自动登出用新凭据重登）。同时支持 `Data__Directory` 指定数据根目录（默认应用根下 `data/`）。

## 部署与使用

三种方式任选：**桌面端（Releases 下载，免装 .NET）** / **Docker** / **systemd 自包含发布**。

### 方式一：桌面端（推荐，开箱即用）

从 [Releases](https://github.com/csvkse/LWT/releases) 下载对应平台压缩包——均为**自包含发布，目标机无需安装 .NET 运行时**，解压即可运行：

| 平台 | 包 | 运行方式 |
|---|---|---|
| Linux x64 / ARM64 | `linuxwebtool-linux-*.tar.gz` | `tar -xzf linuxwebtool-*.tar.gz && ./start.sh`（或直接运行 `LinuxWebTool.WebHost`） |
| Windows x64 | `linuxwebtool-win-x64.zip` | 解压后双击 `start.bat`（或 `LinuxWebTool.WebHost.exe`）；首次运行如遇 SmartScreen 提示，点「更多信息 → 仍要运行」 |

- 默认地址 `http://localhost:5270/app/`（可用 `--urls http://0.0.0.0:5270` 参数或环境变量 `ASPNETCORE_URLS` 修改）
- 首次启动自动生成管理员密码：见**控制台启动日志**或 `data/admin.json` 的 `generatedPassword` 字段
- Linux 注册系统服务：使用包内自带的 `linuxwebtool.service`（`sudo cp linuxwebtool.service /etc/systemd/system/ && sudo systemctl enable --now linuxwebtool`，注意按需修改 `User` 与路径）
- **升级**：下载新版本包覆盖程序文件，**保留 `data/` 文件夹**即可保留全部数据

### 方式二：Docker

**直接使用 CI 发布的镜像**（每次推送 main 自动构建发布）：

```bash
# --restart unless-stopped: 容器随 Docker 服务自动启动（即开机自启）；手动 docker stop 后不会被拉起
docker run -d --restart unless-stopped -p 8080:8080 -v linuxwebtool-data:/app/data --name linuxwebtool ghcr.io/csvkse/lwt:latest
# 首次密码: docker exec linuxwebtool cat /app/data/admin.json

# 容器开机自启的前提是宿主机 Docker 服务本身自启：
#   Linux:   sudo systemctl enable docker
#   Windows: Docker Desktop 设置中勾选 "Start Docker Desktop when you sign in"
# 已在运行的容器补加自启策略: docker update --restart unless-stopped linuxwebtool
```

> 注：GHCR 包首次发布默认 private。拉取时先 `docker login ghcr.io`（用户名 GitHub 账号、密码为 PAT，需 `read:packages` 权限）；或将仓库 Packages 页中 lwt 的 visibility 改为 public 后免登录拉取。

**docker compose 示例**：

```yaml
services:
  linuxwebtool:
    image: ghcr.io/csvkse/lwt:latest
    container_name: linuxwebtool
    ports:
      - "8080:8080"
    volumes:
      - ./data:/app/data          # 单卷持久化：数据库+凭据+密钥+日志
    environment:
      - Admin__UserName=admin
      - Admin__Password=修改我     # 不设则自动生成，见容器日志
      - TZ=Asia/Shanghai
    restart: unless-stopped
```

**本地构建镜像**：

```bash
docker build -t linuxwebtool .
docker run -d -p 8080:8080 -v linuxwebtool-data:/app/data linuxwebtool
```

镜像特性（已实测）：`TZ=Asia/Shanghai`、非 root（appuser）运行、内置 HEALTHCHECK、alpine 内已装 `bash`/`procps`（脚本执行与状态采集容器内可用）。

**容器内执行特权指令**（systemctl、docker 等）：`run` 加 `--user root`（或改 Dockerfile 的 `USER`）；普通内网运维指令无需特权。

### 方式三：systemd（自包含发布）

```powershell
./scripts/publish.ps1
# 按输出提示上传 /opt/linuxwebtool 并启用 linuxwebtool.service
```

### 持久化与数据目录（三种方式通用）

全部可持久化数据聚合在**数据目录 `data/`**（单文件夹备份即可）：`linuxweb.db`（SQLite）、`admin.json`（管理员凭据）、`jwt-secret.key`（签名密钥）、`logs/`（按天滚动日志）。位置可用 `Data__Directory` 环境变量修改（绝对路径或相对应用根的路径）。

管理员凭据优先级：`Admin__UserName`/`Admin__Password` 环境变量或 appsettings 显式配置 **>** `data/admin.json`（记录最后一次生效的凭据）**>** 首次启动随机生成（打印在启动日志）。网页右上角 ⚙ 可随时修改用户名 / 密码。

## 开发约定（门禁强制）

```powershell
./scripts/verify-fast.ps1          # 提交前必跑
```

- Contracts 零依赖；Infrastructure 不引上层；Controller 禁直接 using SqlSugar（经 `*Store` 访问数据）
- 命名空间 = 物理路径
- 前端：API 字符串只在 `config.js`、fetch 只在 `api/client.js`、Storage 只在 `client.js/auth.js`、禁跨层 import、**模板事件必须 `method()`**（裸标识符会被 Vue 运行时编译错误提升导致 handler 丢失）
- 规则详见 `docs/dev/architecture-gates.md`；前端存量债务用 `frontend-gate-baseline.json` 冻结（只减不增）

## 关键配置（appsettings.json）

| 配置 | 默认 | 说明 |
|---|---|---|
| `Shell:DefaultTimeoutSeconds` | 60 | 指令默认超时 |
| `Shell:MaxOutputBytes` | 65536 | stdout/stderr 截断上限 |
| `Shell:MaxConcurrent` | 4 | 并发执行上限（排队等待） |
| `Jwt:ExpireHours` | 12 | 登录有效期 |
| `Logging:LogFile:LinuxWebTool` | Debug | 调试日志开关（写入 debug-*.txt） |
