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
| **系统状态** | 即时查看：CPU / 内存 / 磁盘挂载点 / 网卡速率 / 进程 TOP / 主机内核信息；10s 自动刷新；历史曲线（后台 60s 采样入 SQLite，保留 7 天，uPlot 渲染，1h~7d 区间切换）；Docker 部署加 `--privileged --pid=host --user root` 可自动采集宿主机全部磁盘 |
| **SMB 挂载** | 配置并管理 `mount -t cifs` 网络共享：完整 CRUD、挂载 / 卸载 / 懒卸载、实时状态探测、启动自动重挂（不写 /etc/fstab）、凭据落盘 `data/mount-creds`（600 权限，密码不进命令行）、系统状态页自动展示 SMB 挂载点；需 Linux 特权环境 |
| **FFmpeg 转码** | 视频 / 音频格式处理：一次性文件或文件夹批量入队；转码预设（内置 MP4/H.265/MKV 重封装/MP3）+ 自定义 ffmpeg 参数；替换（先写临时文件成功后才删源）与并存两种输出模式；实时进度 / 速度 / 取消 / 重试；监听文件夹自动转码（网络盘轮询 / 本地盘文件事件两种方式）；桌面部署需安装 ffmpeg，Docker 镜像已内置 |
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

**应用侧行为（已实现）**：转码页顶部会检测当前环境的硬件编码器——若容器成功拿到 GPU（`--gpus all` 或 `--device=/dev/dri` 且用户态库齐全），显示「⚡ 硬件加速可用」并列出 `h264_nvenc`/`h264_vaapi` 等；若未透传或库缺失，显示「未检测到硬件加速」，转码自动回退软件编码（libx264/libx265），任务仍能正常执行，仅在日志提示。系统状态页「硬件」区块会标记 GPU（VGA/3D/Display 类）。

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

### 方式三：systemd（自包含发布）

```powershell
./scripts/publish.ps1
# 按输出提示上传 /opt/linuxwebtool 并启用 linuxwebtool.service
```

### 持久化与数据目录（三种方式通用）

全部可持久化数据聚合在**数据目录 `data/`**（单文件夹备份即可）：`linuxweb.db`（SQLite）、`admin.json`（管理员凭据）、`jwt-secret.key`（签名密钥）、`logs/`（按天滚动日志）。位置可用 `Data__Directory` 环境变量修改（绝对路径或相对应用根的路径）。

管理员凭据优先级：`Admin__UserName`/`Admin__Password` 环境变量或 appsettings 显式配置 **>** `data/admin.json`（记录最后一次生效的凭据）**>** 首次启动随机生成（打印在启动日志）。网页右上角 ⚙ 可随时修改用户名 / 密码。

### 挂载 / 转码的运行要求

- **SMB 挂载**：仅 Linux 生效。Docker 部署需在以 `--privileged --user root` 运行时挂载（特权不足会返回 EPERM，UI 有明确提示）；镜像已内置 `cifs-utils`。挂载点需在容器内可访问（`/mnt/*`），凭据写入 `data/mount-creds/<id>`（600 权限），密码不经命令行。
- **媒体转码**：依赖 ffmpeg。Docker 镜像已内置；桌面 / systemd 部署需自行安装 ffmpeg（或 `Media__FfmpegPath` 指定路径），转码页顶部显示检测状态。媒体目录建议映射进容器以便网页直接访问（如 SMB 挂载 `/mnt/media` 或 `-v /media:/media:ro`）。


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
| `Jwt:ExpireHours` | 12 | 登录有效期 |
| `FileLog:Directory` | `logs` | 日志目录（相对路径锚定到数据目录） |
| `Logging:LogFile:LinuxWebTool` | Debug | 调试日志开关（写入 debug-*.txt） |
