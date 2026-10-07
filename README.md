# LinuxWebTool · Linux 指令控制台

个人 Linux 运维工具：把常用 Linux 指令沉淀为「可命名、分组、置顶」的功能一键执行，支持 Cron 定时调度、完整执行历史与三类日志。参考 `DNSPodForNETCore(InfiniWeb)` 的分层架构与前后端门禁体系，按个人工具规模做了裁剪。

> ⚠️ **安全提示**：本工具可在网页上远程执行任意 shell，**务必只部署在内网/本机，不要暴露公网**。认证为单管理员 + JWT。

## 功能

| 模块 | 能力 |
|---|---|
| 指令 | 保存 Linux 指令为功能；增删改查；分组 / 命名 / 置顶；一键执行（超时可配、输出截断 64KB、并发上限 4） |
| **交互式终端** | 网页内置终端（xterm.js 与 WebSocket），支持多标签页、全屏和后台会话；Linux 提供原生 PTY，Windows 10 1809+ 使用 ConPTY，原生创建失败时回退管道并提示限制 |
| **Bash 脚本** | 支持多行脚本类型：写临时文件 `bash script.sh $1 $2...` 执行（位置参数、引号感知拆分、不经二次 shell 解释）；Windows 开发机自动探测 Git Bash |
| 快速执行 | 临时指令不保存直接跑，自动记入调用历史 |
| 定时任务 | 引用已保存指令/脚本 + Cron（支持 Unix 5 段 / Quartz 6 段，自动归一化）；启停 / 立即运行 / 下次执行时间；常用 Cron 预设 |
| **系统状态** | 即时查看：CPU / 内存 / 磁盘挂载点 / 网卡速率 / 进程 TOP / 主机内核信息；10s 自动刷新；历史曲线（后台 60s 采样入 SQLite，保留 7 天，uPlot 渲染，1h~7d 区间切换）；Docker 部署加 `--privileged --pid=host --user root` 可自动采集宿主机全部磁盘 |
| **SMB 挂载** | 配置并管理 `mount -t cifs` 网络共享：完整 CRUD、挂载 / 卸载 / 懒卸载、实时状态探测、启动自动重挂（不写 /etc/fstab）、凭据落盘 `data/mount-creds`（600 权限，密码不进命令行）、系统状态页自动展示 SMB 挂载点；需 Linux 特权环境 |
| **WebDAV 挂载** | 通过 rclone/FUSE 挂载 HTTPS WebDAV：配置管理、手动与启动自动挂载、远端与本地双重健康探测、故障恢复；写缓存保留在数据卷。需要 Linux、`/dev/fuse` 和挂载权限 |
| **SFTP / S3 挂载** | 通过 rclone/FUSE 挂载 SFTP 目录、AWS S3 或 HTTPS S3 兼容存储；支持手动/自动挂载、直连远端与本地目录健康探测、故障恢复。SFTP 要求提供可信 SSH 主机公钥 |
| **FFmpeg 转码** | 视频 / 音频格式处理：一次性文件或文件夹批量入队；转码预设（内置 MP4/H.265/MKV 重封装/MP3）+ 自定义 ffmpeg 参数；替换（先写临时文件成功后才删源）与并存两种输出模式；实时进度 / 速度 / 取消 / 重试；监听文件夹自动转码（网络盘轮询 / 本地盘文件事件两种方式）；桌面部署需安装 ffmpeg，Docker 镜像已内置 |
| **EasyTier 虚拟组网** | 去中心化点对点虚拟局域网：基于 Rust 原生内核驱动，支持全互联拓扑、NAT 打洞直连、虚拟 IP、运行时零中断热打补丁；内置内核引擎热升级调度器（支持 GitHub 云端拉取与本地上传双模热重载），内核与节点配置全部收敛于持久化目录 |
| **FRP 内网穿透** | 反向隧道（HTTP-over-WebSocket）：支持多线路反向穿透、302 自动重定向代理与内网代拉，安全穿透内网服务 |
| **家庭智能网关** | 基于 YARP 的高性能反向代理：支持 L7 网站即席代理、白名单控制，以及 L4 TCP/UDP 端口转发 |
| 执行历史 | 手动 / 定时 / 快速三类记录；状态筛选、关键字搜索、分页；失败详情（stdout/stderr/退出码/耗时） |
| 日志 | 操作日志（DB，全行为审计）+ 程序日志（`logs/app-*.txt`）+ 调试日志（`logs/debug-*.txt`，网页 tail 查看） |
| 门禁 | 单管理员登录签发 JWT（HS256，默认 12h）；登录失败 10 次锁 IP 5 分钟；全部 API 需认证 |

## 技术栈

- **后端**：.NET 10 / ASP.NET Core Minimal API（AutoControllers 源码生成，Native AOT 零反射）+ Dapper AOT (DDL SQLite) + Quartz.NET(调度) + JwtBearer + YARP 反向代理 + EasyTier 原生 C ABI / Core 守护进程双模引擎
- **前端**：零构建 Vue3 ESM（本地 vendor 自托管）+ vue-router(hash) + Tailwind（本地 Play 脚本）——**内网零外网依赖**
- **架构**：物理分层 `Contracts(零依赖) → Infrastructure → WebHost(组合根+前端)`，严格遵循功能垂直切片（Features）与基础设施适配（Adapters），128 项 xUnit 架构测试与门禁基线 + Node 前端门禁强制约束

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
├── scripts/publish.ps1                 # 发布 linux-x64 自包含产物（systemd）
├── .github/workflows/ci.yml            # CI：构建+门禁+GHCR 镜像（push/PR 触发）
├── .github/workflows/desktop-release.yml  # 桌面端多平台 Native AOT 发布（tag v* 触发 GitHub Release）
├── Dockerfile                          # Docker 部署（三阶段，alpine）
├── start.bat                           # Windows 开发机一键启动（dotnet run + 自动开浏览器）
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
docker run -e Admin__UserName=ops -e Admin__Password=你的密码 ghcr.io/csvkse/lwt:latest
```

凭据优先级：`Admin__UserName`/`Admin__Password` 环境变量或 appsettings 显式配置 **>** `data/admin.json`（自动生成密码的持久化，记录最后一次生效的凭据）**>** 首次启动随机生成。

**网页修改凭据**：登录后点右上角用户名旁的 ⚙，可修改用户名 / 密码（需验证当前密码，改完自动登出用新凭据重登）。同时支持 `Data__Directory` 指定数据根目录（默认应用根下 `data/`）。

## 部署与使用

## 效果预览

### 首页与指令管理

![首页](docs/imgs/首页.png)

![指令页](docs/imgs/指令页.png)

### 文件、日志与执行历史

![文件管理](docs/imgs/文件管理.png)

![操作日志](docs/imgs/操作日志.png)

![指令执行历史](docs/imgs/指令执行历史.png)

### 系统状态、转码与 SMB 挂载

![系统状态](docs/imgs/系统状态.png)

![系统状态采样点](docs/imgs/系统状态-采样点.png)

![转码页](docs/imgs/转码页.png)

![SMB 挂载](docs/imgs/Smb挂载.png)

三种方式任选：**桌面端（Releases 下载，免装 .NET）** / **Docker** / **systemd 自包含发布**。

### 方式一：桌面端（推荐，开箱即用）

从 [Releases](https://github.com/csvkse/LWT/releases) 下载对应平台压缩包（当前最新版本 `v0.1.20`）。桌面端包为**Native AOT 自包含单文件发布**，目标机无需安装 .NET 运行时，解压即可运行：

| 平台 | 包 | 运行方式 |
|---|---|---|
| Linux x64 / ARM64 | `linuxwebtool-linux-*.tar.gz` | `tar -xzf linuxwebtool-*.tar.gz && ./start.sh`（或直接运行 `LinuxWebTool.WebHost`） |
| Windows x64 | `linuxwebtool-win-x64.zip` | 解压后双击 `start.bat`（或 `LinuxWebTool.WebHost.exe`）；首次运行如遇 SmartScreen 提示，点「更多信息 → 仍要运行」 |

- 默认地址 `http://localhost:5270/app/`（可用 `--urls http://0.0.0.0:5270` 参数或环境变量 `ASPNETCORE_URLS` 修改）
- 首次启动自动生成管理员密码：见**控制台启动日志**或 `data/admin.json` 的 `generatedPassword` 字段
- Linux 注册系统服务：使用包内自带的 `linuxwebtool.service`（`sudo cp linuxwebtool.service /etc/systemd/system/ && sudo systemctl enable --now linuxwebtool`，注意按需修改 `User` 与路径）
- **升级**：下载新版本包覆盖程序文件，**保留 `data/` 文件夹**即可保留全部数据

> 桌面端发布由 `.github/workflows/desktop-release.yml` 在推送 `v*` tag 后自动构建 Linux x64、Linux ARM64 和 Windows x64；Native AOT 包与 Docker AOT 镜像使用同一套编译期序列化/路由约束。

### 方式二：Docker

**直接使用 CI 发布的镜像**（每次推送 main 自动构建发布）：

> 镜像是按 GPU 厂商分 tag 的：默认 `latest` 装全部 VA 驱动（兜底）；`latest-intel`/`latest-amd`/`latest-nv` 只含对应厂商驱动，体积更小。N100 这类 Intel 核显用 `latest-intel`。

```bash
# 更新镜像（已运行过的容器更新方式见下方）
docker pull ghcr.io/csvkse/lwt:latest

# --restart unless-stopped: 容器随 Docker 服务自动启动（即开机自启）；手动 docker stop 后不会被拉起
docker run -d --restart unless-stopped -p 5270:5270 -v linuxwebtool-data:/app/data --name linuxwebtool ghcr.io/csvkse/lwt:latest
# 首次密码: docker exec linuxwebtool cat /app/data/admin.json

# 端口映射 -p 宿主端口:容器端口：容器内固定监听 5270（全链路与桌面端一致），宿主端口可自选（如 -p 80:5270）。
# 已运行容器更新镜像：docker pull 后执行 docker rm -f linuxwebtool，再重新运行上面的 docker run（data 卷保留数据）。

# 按 GPU 厂商选 tag（体积更小；见下方「镜像 tag 说明」）：
#   Intel 核显（N100 等）：docker pull ghcr.io/csvkse/lwt:latest-intel
#   AMD 核显：           docker pull ghcr.io/csvkse/lwt:latest-amd
#   NVIDIA（需 --gpus）: docker pull ghcr.io/csvkse/lwt:latest-nv

# 容器开机自启的前提是宿主机 Docker 服务本身自启：
#   Linux:   sudo systemctl enable docker
#   Windows: Docker Desktop 设置中勾选 "Start Docker Desktop when you sign in"
# 已在运行的容器补加自启策略: docker update --restart unless-stopped linuxwebtool
```

上面的基础命令只用于普通功能。要在容器内使用 **EasyTier 虚拟组网** 或管理 **SMB、WebDAV、SFTP、S3 挂载**，请在 Linux Docker 宿主机上增加对应设备与权限（若同时需要多项，建议直接使用 `--privileged --user root`）：

```bash
# 1. 运行 EasyTier 虚拟组网（必需 /dev/net/tun 设备与 NET_ADMIN 能力；持久化挂载确保内核与节点不丢）
docker run -d --restart unless-stopped \
  --name linuxwebtool \
  -p 5270:5270 \
  -v linuxwebtool-data:/app/data \
  --cap-add=NET_ADMIN --device=/dev/net/tun \
  ghcr.io/csvkse/lwt:latest

# 2. 运行 SMB / WebDAV / SFTP / S3 挂载（WebDAV/SFTP/S3 需 /dev/fuse；仅 SMB 时可省略 --device）
docker run -d --restart unless-stopped \
  --name linuxwebtool \
  -p 5270:5270 \
  -v linuxwebtool-data:/app/data \
  --privileged --user root --device /dev/fuse \
  ghcr.io/csvkse/lwt:latest

# 3. 全能特权模式（同时启用 EasyTier 虚拟组网 + 磁盘挂载 + 宿主硬件控制）
docker run -d --restart unless-stopped \
  --name linuxwebtool \
  -p 5270:5270 \
  -v linuxwebtool-data:/app/data \
  --privileged --user root \
  --device=/dev/net/tun --device=/dev/fuse \
  ghcr.io/csvkse/lwt:latest
```

`--privileged` 权限较大，只在可信宿主机使用。若使用本地尚未发布的挂载或组网代码，先执行下方 `docker build -t linuxwebtool .`，再把镜像名改成 `linuxwebtool`。本机 `wslc` 不支持设备透传，不能用它验证 rclone 或 TUN 虚拟网卡实际工作。

> 注：GHCR 包首次发布默认 private。拉取时先 `docker login ghcr.io`（用户名 GitHub 账号、密码为 PAT，需 `read:packages` 权限）；或将仓库 Packages 页中 lwt 的 visibility 改为 public 后免登录拉取。

**docker compose 示例**（更新镜像：`docker compose pull && docker compose up -d`）：

```yaml
services:
  linuxwebtool:
    image: ghcr.io/csvkse/lwt:latest
    container_name: linuxwebtool
    ports:
      - "5270:5270"
    volumes:
      - ./data:/app/data          # 单卷持久化：数据库+凭据+密钥+日志+EasyTier内核与节点配置
    # 启用 EasyTier 虚拟组网所需权限与设备（无需特权模式即可运行）
    cap_add:
      - NET_ADMIN
    devices:
      - /dev/net/tun:/dev/net/tun
      # - /dev/fuse:/dev/fuse     # 若需 WebDAV/SFTP/S3 FUSE 挂载则取消注释
    # privileged: true           # 若同时使用 SMB 挂载，建议取消注释并配置 user: root
    # user: root
    environment:
      - Admin__UserName=admin
      - Admin__Password=修改我     # 不设则自动生成，见容器日志
      - TZ=Asia/Shanghai
    restart: unless-stopped
```

**本地构建镜像**：

```bash
docker build -t linuxwebtool .
docker run -d -p 5270:5270 -v linuxwebtool-data:/app/data linuxwebtool
```

镜像特性（已实测）：`TZ=Asia/Shanghai`、非 root（appuser）运行、内置 HEALTHCHECK、alpine 内已装 `bash`/`procps`/`usbutils`/`pciutils`/`kmod`（脚本执行、状态采集、lsusb/lspci/lsmod 硬件查看容器内可用）。

**容器内执行特权指令**（systemctl、docker 等）：`run` 加 `--user root`（或改 Dockerfile 的 `USER`）；普通内网运维指令无需特权。

### 进阶：USB / 宿主工具目录 / GPU 直通（按需添加）

容器默认**无法访问宿主外设**。以下配置让网页中的指令能操作宿主硬件（仅标准 Linux 宿主；WSL2 环境见各项备注）。需要多项时直接在 `docker run` 上堆叠参数：

```bash
docker run -d --restart unless-stopped \
  -p 5270:5270 \
  -v linuxwebtool-data:/app/data \
  --name linuxwebtool \
  --user root \
  -v /usr/local/bin:/usr/local/bin:ro \
  -v /dev/bus/usb:/dev/bus/usb \
  --device=/dev/dri \
  ghcr.io/csvkse/lwt:latest
```

| 需求 | 配置 | 说明 |
|---|---|---|
| **USB 控制** | `-v /dev/bus/usb:/dev/bus/usb` + `--user root` | 挂载整个 USB 总线（热插拔设备动态可见）；特定串口/TTY 设备另加 `--device=/dev/ttyUSB0`；`lsusb` 已内置。**WSL2**：先用 [usbipd-win](https://github.com/dorssel/usbipd-win) 把 Windows USB 设备 attach 到 WSL（`usbipd bind` / `usbipd attach --wsl`），容器再按上面配置 |
| **宿主机 /usr/local/bin** | `-v /usr/local/bin:/usr/local/bin:ro` | 宿主安装的工具脚本直接在容器内使用（`:ro` 只读更安全）。⚠ 镜像是 alpine(musl)：宿主 Debian/Ubuntu 编译的**动态链接程序无法运行**，脚本与静态编译的二进制不受影响 |
| **GPU（NVIDIA）** | `--gpus all` | 宿主需已装 NVIDIA 驱动 + [nvidia-container-toolkit](https://docs.nvidia.com/datacenter/cloud-native/container-toolkit/latest/install-guide.html)；**toolkit 装宿主机，非容器**（见下方「宿主机安装 NVIDIA 栈」）；容器内 `nvidia-smi` 可用。WSL2 需 Windows 侧装 NVIDIA 驱动（驱动自带 WSL 支持） |
| **GPU（Intel/AMD 核显）** | `--device=/dev/dri` | 挂载 DRI 设备（VA-API/Vulkan 硬件加速）；`libva` 用户态库已内置，ffmpeg 已启用 vaapi 编码，透传后即可用。**注意**：VA 驱动按 tag 区分——`latest-intel` 只含 Intel iHD、`latest-amd` 只含 AMD gallium；`latest` 全装（更大全能）。请选对应厂商的 tag，否则核显无对应驱动会回退软件编码 |
| **全部要（省事）** | `--privileged --user root` | 接近宿主完整权限，含 USB/所有设备。方便但权限最大，请仅在信任内网使用 |
| **查看宿主机磁盘（自动，推荐）** | `--privileged --pid=host --user root` | 采集器经 `nsenter` 进入宿主挂载命名空间执行 `df`，系统状态页**自动显示宿主全部磁盘与挂载点**，新增磁盘自动出现，无需任何手写。需标准 Linux 宿主 Docker（WSL2 的 wslc 不支持 privileged，见下行） |
| **查看宿主机磁盘（手动）** | 逐盘挂载：`-v /mnt/c:/host-c:ro`（WSL2 的 Windows 盘）等 | 受限运行时（wslc）或不想给特权时的替代：容器文件系统与宿主隔离，`df` 天然只看到容器自身（如 `/dev/loop2` 虚拟盘）；挂进来的盘会出现在磁盘列表（真实容量）。已实测：WSL2 下挂 `/mnt/c` 后容器内 `df` 正确显示 Windows C 盘容量（790GB·70%） |

### 镜像 tag 说明（按 GPU 厂商瘦身）

CI 每次推送 main 会构建并推送 4 个镜像 tag，用 `VENDOR` 构建参数选装对应 VA 用户态驱动（编码器在 ffmpeg 内，这里只装让 `/dev/dri` 真正可用的驱动库）：

| tag | 适用 GPU | 驱动层 | 说明 |
|---|---|---|---|
| `latest` | 通用（兜底） | Intel iHD + AMD/NVIDIA gallium | 与原行为一致，体积最大 |
| `latest-intel` | Intel 核显（N100/Alder Lake-N 等） | 仅 Intel iHD | **推荐**，比 `latest` 省约 42MB |
| `latest-amd` | AMD 核显 | 仅 mesa-va-gallium | 走 VAAPI；编码受限时应用自动回退软件 |
| `latest-nv` | NVIDIA | 无（由 nvidia-container-toolkit 透传） | 需 `--gpus all`，复用宿主机 NVIDIA 驱动 |

> 选 tag 原则：**按宿主机显卡厂商选对应 tag**，避免「全能镜像」白白增容；不确定就先用 `latest`（全装，一定能跑）。

**宿主机安装 NVIDIA 栈（`nvidia-container-toolkit` 装在宿主机，不在容器内）**

> 关键：`nvidia-container-toolkit` 是**宿主机级容器运行时插件**，安装在跑 Docker 的那台宿主机器上（用宿主包管理器 + `sudo`），不是在容器里。它在 Docker 启动 `--gpus all` 时负责把 NVIDIA 驱动与 GPU 设备注入容器。缺它时 `--gpus all` 参数会直接报错。

```bash
# 1. 安装 NVIDIA 驱动（宿主，Ubuntu/Debian 示例；已装可跳过）
sudo apt install -y nvidia-driver-550          # 版本按宿主机显卡与发行版选择
# 重启宿主机使驱动生效，随后 nvidia-smi 应能列出 GPU

# 2. 安装 nvidia-container-toolkit（宿主机）
sudo apt install -y nvidia-container-toolkit
# 或 NVIDIA 官方脚本：  curl -fsSL https://nvidia.github.io/libnvidia-container/gpgkey | sudo gpg --dearmor -o /usr/share/keyrings/nvidia-container-toolkit-keyring.gpg && ...（见官方 install-guide）

# 3. 配置 Docker 运行时（宿主机，改 /etc/docker/daemon.json）
sudo nvidia-ctk runtime configure --runtime=docker
sudo systemctl restart docker

# 4. 验证（宿主机）——容器内 nvidia-smi 应可列出 GPU
docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi
```

带 GPU 透传运行本应用（宿主机上执行）：

```bash
docker run -d --restart unless-stopped \
  -p 5270:5270 \
  -v linuxwebtool-data:/app/data \
  --name linuxwebtool \
  --user root \
  --gpus all \
  ghcr.io/csvkse/lwt:latest
```

compose 等价写法（NVIDIA GPU 透传）：

```yaml
services:
  linuxwebtool:
    image: ghcr.io/csvkse/lwt:latest
    container_name: linuxwebtool
    user: root
    ports:
      - "5270:5270"
    volumes:
      - linuxwebtool-data:/app/data
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: all
              capabilities: [gpu]
    restart: unless-stopped

volumes:
  linuxwebtool-data:
```

**Intel/AMD 核显**（`--device=/dev/dri` 透传）：镜像已内置 VA-API 用户态库（`libva`）与 核显驱动（`mesa-va-gallium`），ffmpeg 已启用 vaapi 编码支持，透传 `/dev/dri` 后即可走 VA-API 硬件编码。核显路径**不需要** nvidia-container-toolkit。若自行裁剪镜像后遇到 "Cannot load libva" / "device not found"，需保留 `apk add libva mesa-va-gallium`。

**应用侧行为（已实现）**：应用会枚举 `/dev/dri/renderD*` 渲染设备，按 PCI vendor 区分 Intel / AMD，并对 NVIDIA/Intel/AMD 硬件编码器执行真实编码探针，同时读取 `nvidia-smi` 或 `vainfo` 的 GPU/驱动信息。VAAPI/QSV 探针成功时记录实际设备路径，真实转码会复用同一设备，避免多显卡环境探 A 卡、实际转码用 B 卡。只有探针通过才显示「⚡ 硬件加速可用」；驱动初始化、设备权限或 VA 用户态库异常时显示「△ 硬件驱动异常」和失败原因，转码自动回退软件编码（libx264/libx265）。AMD 不探测 QSV；系统状态页「硬件」区块会标记 GPU（VGA/3D/Display 类）。

**宿主机磁盘自动采集 —— 完整示例**（三个参数缺一不可，作用：`--privileged` 授予 nsenter 权限；`--pid=host` 让容器看到宿主 PID 1 以定位其命名空间；`--user root` 非 root 无权切换命名空间）：

```bash
# 更新镜像（已有容器：pull 后 docker rm -f linuxwebtool，再重新运行下方 docker run；data 卷数据保留）
docker pull ghcr.io/csvkse/lwt:latest

docker run -d --restart unless-stopped \
  -p 5270:5270 \
  -v linuxwebtool-data:/app/data \
  -v /usr/local/bin:/usr/local/bin:ro \
  --name linuxwebtool \
  --privileged --pid=host --user root \
  ghcr.io/csvkse/lwt:latest
# 打开 http://localhost:5270/app/ 系统状态页，磁盘列表即宿主机全部磁盘与挂载点
# /usr/local/bin 只读映射：宿主安装的工具脚本在容器内直接可用（alpine 容器注意动态链接兼容性）
```

compose 等价完整写法（更新镜像：`docker compose pull && docker compose up -d`）：

```yaml
services:
  linuxwebtool:
    image: ghcr.io/csvkse/lwt:latest
    container_name: linuxwebtool
    privileged: true      # 必需：授予 nsenter 权限
    pid: host             # 必需：共享宿主 PID 命名空间（nsenter -t 1 定位宿主）
    user: root            # 必需：非 root 无权切换命名空间
    # 启用 WebDAV/SFTP/S3 挂载时还需宿主机支持 FUSE，并添加：
    # devices:
    #   - /dev/fuse:/dev/fuse
    ports:
      - "5270:5270"
    volumes:
      - linuxwebtool-data:/app/data
      - /usr/local/bin:/usr/local/bin:ro
    environment:
      - TZ=Asia/Shanghai
    restart: unless-stopped

volumes:
  linuxwebtool-data:
```

USB / GPU 直通的 compose 节选（叠加到上方任一示例的对应位置）：

```yaml
    devices:
      - /dev/bus/usb
      - /dev/dri
    volumes:
      - /usr/local/bin:/usr/local/bin:ro
```

> ⚠️ 这些配置授予容器宿主硬件控制权，与「不暴露公网」原则叠加使用；非 NVIDIA 环境去掉 `--gpus all`（无 toolkit 时该参数会直接报错）。

### 方式三：systemd（常规自包含发布）

```powershell
./scripts/publish.ps1
# 按输出提示上传 /opt/linuxwebtool 并启用 linuxwebtool.service
```

> `scripts/publish.ps1` 面向 Linux x64 的常规自包含部署；桌面端 Native AOT 包请优先从 Releases 下载。systemd 服务文件中的启动路径应与实际发布产物保持一致。

### 持久化与数据目录（三种方式通用）

全部可持久化数据聚合在**数据根目录 `data/`**（Docker 容器内为 `/app/data`，单文件夹备份即可）：
- `linuxweb.db`：SQLite 数据库（指令、执行历史、定时任务、EasyTier 节点元数据、FRP 穿透与网关配置）
- `admin.json`：管理员凭据持久化（首次运行自动生成随机强密码）
- `jwt.key`：JWT 身份签名密钥
- `easytier/bin/`：**EasyTier 原生内核与工具**（`easytier-core`、`easytier-cli`、`libeasytier_ffi.so`/`easytier_ffi.dll`，在线下载升级或本地上传后永久保存在此）
- `easytier/nodes/`：**EasyTier 节点运行时配置**（`<实例名称>.toml`，启动时从数据库加载并持久化生成）
- `easytier/staging/`：在线升级包临时缓冲下载目录
- `logs/`：系统按天滚动日志（`app-*.txt`）与调试日志（`debug-*.txt`）
- `mount-creds/`、`rclone-config/`、`rclone-cache/`：网络挂载凭据与写入缓存

> **单卷挂载优势**：无论通过 Docker 命名卷 `-v linuxwebtool-data:/app/data` 还是本地路径映射 `-v /opt/linuxwebtool/data:/app/data`，所有 EasyTier 核心文件、版本更新、节点网络规则和系统数据全部落在此卷内。容器销毁、重建或镜像升级（`docker pull`）时，**已安装的 EasyTier 内核与网络节点配置 100% 完整保留并自启**，无需重复下载或重新配置。
>
> 自定义路径：可通过配置 `Data:Directory` 或环境变量 `Data__Directory`（如 `Data__Directory=/var/lib/linuxwebtool`）自定义持久化绝对路径。

管理员凭据优先级：`Admin__UserName`/`Admin__Password` 环境变量或 appsettings 显式配置 **>** `data/admin.json`（记录最后一次生效的凭据）**>** 首次启动随机生成（打印在启动日志）。网页右上角 ⚙ 可随时修改用户名 / 密码。

### 挂载 / 转码 / EasyTier 虚拟组网的运行要求

- **EasyTier 虚拟组网**：基于 Rust 原生内核的去中心化全互联 P2P 虚拟局域网。
  - **Linux 宿主 / Docker 容器**：创建 TUN 虚拟网卡需要 Root 用户或 `CAP_NET_ADMIN` 能力。在 Docker 下运行必须透传设备与权限：`--cap-add=NET_ADMIN --device=/dev/net/tun`（或 `--privileged --user root`）；宿主非 root 运行可执行 `sudo setcap cap_net_admin=+ep data/easytier/bin/easytier-core` 赋予网卡管理能力。
  - **Windows 宿主机**：创建 TUN 虚拟网卡需要以管理员身份运行 WebHost（右键“以管理员身份运行”），否则虚拟网卡创建失败，DHCP 将无法分配虚拟 IP。
  - **内核引擎管理与热升级**：Web 端提供【⚙️ 内核管理】界面，支持从 GitHub Releases 官方仓库一键云端自动拉取适配系统的内核包（内置 ghproxy 加速），或手动上传 `libeasytier_ffi.so` / `easytier_ffi.dll`。内核支持双模调度（C ABI Native FFI 高性能直调模式与 Core Binary 独立守护进程模式），更新时自动排空旧实例、解压到持久化 `bin/` 目录并平滑恢复所有网络节点。
  - **P2P 真直连与流量卸载（UDP 监听预设）**：为获得最佳 P2P 穿透效果并卸载中继/Cloudflare Worker 流量，推荐开启本地 UDP 监听端口（UI 提供一键预设：如 `udp://0.0.0.0:11010` 或 IPv4+IPv6 双栈 `udp://0.0.0.0:11010` + `udp://[::]:11010`）。只要路由器开启 UPnP、Full Cone NAT 或具备原生 IPv6，两端节点通过中继完成握手后，所有虚拟内网数据传输将自动切换为点对点 UDP 直连（P2P），流量完全不消耗中继服务器。Docker 部署若需外部节点直连本节点，可映射对应 UDP 端口（如 `-p 11010:11010/udp`）或采用 host 网络模式。
- **SMB 挂载**：仅 Linux 生效。Docker 部署需在以 `--privileged --user root` 运行时挂载（特权不足会返回 EPERM，UI 有明确提示）；镜像已内置 `cifs-utils`。挂载点需在容器内可访问（`/mnt/*`），凭据写入 `data/mount-creds/<id>`（600 权限），密码不经命令行。
- **WebDAV 挂载**：仅 Linux 生效。镜像包含 `rclone` 和 `fuse3`；容器需提供 `/dev/fuse` 及挂载权限，例如可信环境下使用 `--privileged --device=/dev/fuse --user root`。仅接受 HTTPS WebDAV URL。配置写入数据卷中的 `webdav-config`（600 权限），写入缓存保存在 `webdav-cache`；卸载前请确认文件已上传。WebDAV 服务端不提供容量时，系统状态页不显示估算容量。容器内创建的挂载默认只在本容器可见；要供宿主机或其他容器访问，还需配置并验证 Linux 绑定挂载传播。
- **SFTP / S3 挂载**：同样需要 `rclone`、`/dev/fuse` 和挂载权限。SFTP 需要用户名及密码或容器内私钥文件，并须填入从可信渠道获得的完整 SSH 主机公钥；S3 支持 AWS 区域或自定义 HTTPS 端点、存储桶及访问密钥。两者共用 `data/rclone-config`（凭据文件 600 权限）和 `data/rclone-cache`（写缓存），卸载前请确认待上传文件已同步。健康检查每 30 秒直接读取远端目录，并检查本地挂载目录；连续三次本地失败且远端正常时尝试正常卸载重挂。详见 [SFTP/S3 配置说明](docs/rclone-sftp-s3.md)。
- **媒体转码**：依赖 ffmpeg。Docker 镜像已内置；桌面 / systemd 部署需自行安装 ffmpeg（或 `Media__FfmpegPath` 指定路径），转码页顶部显示检测状态。媒体目录建议映射进容器以便网页直接访问（如 SMB 挂载 `/mnt/media` 或 `-v /media:/media:ro`）。

### 终端与目录联动

在文件管理器当前目录或文件夹菜单选择“在终端打开”，会创建以该目录为起点的新终端。Linux Bash 和 Windows PowerShell 返回提示符后会报告当前目录；终端中的“打开当前目录”会先验证文件管理器能访问该目录。Windows 文件管理器首页列出可访问盘符，支持盘符绝对路径及 UNC 共享路径；系统根目录、程序数据目录和 Windows junction/符号链接路径禁止修改。

终端默认以后台会话运行。关闭标签或浏览器断线只断开连接；已输入的会话可在“后台终端”列表接回，重新连接保持同一进程。未输入且空闲的会话断开超过 2 分钟后自动清理，显式“结束会话”会终止进程。后台输出持续读取并保留最近 1 MiB，较早输出截断时页面会提示；全屏程序恢复可能需要重新绘制。Web 服务或容器停止会结束会话。

Linux 使用内置原生 PTY，Windows 使用原生 ConPTY，无需安装额外后台终端工具。终端页面可检测原生交互能力，后台列表按会话显示实际管道回退状态；回退模式的全屏程序、窗口缩放及 Ctrl-C 能力受限。后台续跑仅支持软件运行期间，软件退出或容器停止时会话结束。


## 开发约定（门禁强制）

```powershell
./scripts/verify-fast.ps1          # 提交前必跑
dotnet test LinuxWebTool.slnx -c Release # 运行全部单元/架构/集成测试
./scripts/verify-aot.ps1            # Docker Native AOT 构建 + 61 路由冒烟
```

测试分层：`tests/LinuxWebTool.ArchitectureTests` 包含架构门禁和核心纯单元测试；`tests/LinuxWebTool.IntegrationTests` 使用 TestServer、SQLite 和 Quartz 验证认证、CRUD 及 AOT 敏感接口；`scripts/smoke-aot.ps1` 在 Native AOT Docker 容器中调用全部后端路由，并扫描动态代码生成与 JSON metadata 错误。

- Contracts 零依赖；Infrastructure 不引上层；Controller 禁直接 using SqlSugar（经 `*Store` 访问数据）
- 命名空间 = 物理路径
- 前端：API 字符串只在 `config.js`、fetch 只在 `api/client.js`、Storage 只在 `client.js/auth.js`、禁跨层 import、**模板事件必须 `method()`**（裸标识符会被 Vue 运行时编译错误提升导致 handler 丢失）
- 规则详见 `docs/dev/architecture-gates.md`；前端存量债务用 `frontend-gate-baseline.json` 冻结（只减不增）

## 关键配置（appsettings.json）

| 配置 | 默认 | 说明 |
|---|---|---|
| `Urls` | `http://localhost:5270` | 监听地址（appsettings.json 顶层；Docker 镜像内已改写为 `0.0.0.0:5270`，勿在容器内依赖此值） |
| `Data:Directory` | `data` | 数据根目录（相对路径锚定应用根；环境变量 `Data__Directory`） |
| `Shell:DefaultTimeoutSeconds` | 60 | 指令默认超时 |
| `Shell:MaxOutputBytes` | 65536 | stdout/stderr 截断上限 |
| `Shell:MaxConcurrent` | 4 | 并发执行上限（排队等待） |
| `Shell:WorkingDirectory` | 空 | 指令执行工作目录（空=继承进程目录） |
| `Terminal:MaxSessions` | 16 | 同时保留的终端会话上限 |
| `Terminal:BufferBytes` | 1048576 | 每个会话的最近输出缓存字节数 |
| `Terminal:UnusedGraceSeconds` | 120 | 未输入且空闲的终端断开后的清理宽限秒数 |
| `Terminal:ExitedRetentionSeconds` | 300 | 已退出会话在列表中保留的秒数 |
| `Jwt:ExpireHours` | 12 | 登录有效期 |
| `FileLog:Directory` | `logs` | 日志目录（相对路径锚定到数据目录） |
| `Logging:LogFile:LinuxWebTool` | Debug | 调试日志开关（写入 debug-*.txt） |
| `Retention:FileLogDays` | 30 | 程序日志和调试日志保留天数 |
| `Media:LogRetentionDays` | 7 | 转码日志保留天数 |
| `Retention:ExecutionHistoryDays` | 90 | 指令执行历史保留天数 |
| `Retention:OperationLogDays` | 180 | 操作审计日志保留天数 |
| `Retention:CleanupIntervalHours` | 24 | 自动清理执行间隔（小时） |
