# -----------------------------------------------------------------------------
# 构建上下文 = 仓库根目录：docker build -t linuxwebtool .
# 参考 DNSPodForNETCore(InfiniWeb) 的三阶段结构：
#   base  —— aspnet alpine 运行时 + 时区/ICU + bash/procps（脚本与状态采集依赖）+ 非 root 用户
#   build —— sdk 还原（csproj 先行拷贝利用缓存）与发布
#   final —— base + 发布产物
# -----------------------------------------------------------------------------

# ---------- 阶段 1：运行时基础 ----------
# VENDOR 按 GPU 厂商选装 VA 驱动（intel/amd/nv/all/cpu）；见下方"按厂商装 VA 驱动"层。
# 顶层 ARG 仅作为默认值，base 阶段需再次声明才能被 RUN 引用。
ARG VENDOR=all

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
# 重新声明全局 ARG（多阶段中 FROM 之前的 ARG 需在对应阶段重新 ARG 才能被 RUN 使用）
ARG VENDOR
WORKDIR /app
EXPOSE 5270

ENV TZ=Asia/Shanghai \
    LANG=en_US.UTF-8 \
    LC_ALL=en_US.UTF-8 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# bash      —— 脚本类型执行依赖 /bin/bash
# procps    —— 系统状态页的 ps 采集（alpine 自带 busybox ps 不支持 -eo）
# usbutils/pciutils/kmod —— 硬件查看工具（lsusb / lspci / lsmod）
# util-linux-misc —— nsenter：配合 --privileged --pid=host --user root 自动采集宿主全部磁盘
# cifs-utils —— SMB 挂载管理（mount -t cifs；需 --privileged --user root 运行）
# nethogs   —— 每进程网络速率采集（tracemode；仅 --privileged --user root 运行时生效，非特权则探测跳过）
# ffmpeg    —— 媒体转码（含 ffprobe，一次 一次性转码 / 队列 / 监听自动转码全依赖它）
# tzdata/icu —— 时区与中文全球化
# libva/libva-utils —— VA-API 用户态库与诊断工具（vainfo）。VA 编码能力由运行时透传驱动接管，
#   ffmpeg 已内置 h264_vaapi/hevc_vaapi/nvenc 等硬件编码器支持，缺 libva 时 --device=/dev/dri 透传核显也无法真正编码。
#
# 镜像瘦身：VA 驱动（intel iHD 39.5M / mesa gallium 42M）不再无条件全打包，按 VENDOR 分层选装，
#   仅装所用 GPU 厂商的用户态驱动，避免"全能镜像"白白增容（详见 base 阶段底部"按厂商装 VA 驱动"层）。
RUN apk add --no-cache bash procps usbutils pciutils kmod util-linux-misc cifs-utils nethogs \
      ffmpeg libva libva-utils tzdata icu-libs && \
    cp /usr/share/zoneinfo/$TZ /etc/localtime && \
    echo $TZ > /etc/timezone

# 按 GPU 厂商装 VA 用户态驱动（编码器已在 ffmpeg 内，这里只装让 -vaapi_device 真正可用的驱动库）：
#   intel -> iHD（Alder Lake-N / N100 等新核显必需，提供 iHD_drv_video.so）；
#            Alpine 的 mesa-va-gallium 仅含 AMD/NVIDIA 空壳链接，缺 Intel iHD 时 vainfo 报 "va_openDriver() -1"
#   amd   -> mesa-va-gallium（radeonsi 解码；Alpine 该包以 VA 解码为主，AMD VA 编码受限，跑不了时应用自动回退软件编码）
#   nv    -> 不装 VA 驱动：NVIDIA 走 NVENC，由部署侧 nvidia-container-toolkit + --gpus 透传驱动，ffmpeg 已含 nvenc 支持
#   all   -> 全装（兜底，与原行为一致）
#   cpu/none -> 纯软件编码，不装 VA 驱动
RUN echo "==> VENDOR=${VENDOR}" && \
    case "${VENDOR}" in \
      intel) apk add --no-cache intel-media-driver ;; \
      amd)   apk add --no-cache mesa-va-gallium ;; \
      nv|nvidia) echo "nvenc 由 nvidia-container-toolkit 透传驱动，镜像内置 nvenc 编码器支持，不装 VA 驱动" ;; \
      all)   apk add --no-cache intel-media-driver mesa-va-gallium ;; \
      cpu|none|*) echo "纯软件编码，不装 VA 驱动" ;; \
    esac

# 非 root 运行；data 为运行期数据目录（SQLite/凭据/jwt 密钥/日志全部聚合于此，单卷持久化）。
# 若需要执行 systemctl/docker 等特权指令，可将下方 USER 改为 root 或部署时覆盖。
# 加入 audio 组：容器透传 --device=/dev/dri 后，renderD128 属 audio 组，非 root 需该组才能用 VA-API。
RUN addgroup -g 1001 appgroup && \
    adduser -u 1001 -G appgroup -s /bin/bash -D appuser && \
    addgroup appuser audio 2>/dev/null; \
    mkdir -p /app/data /home/appuser/.aspnet/DataProtection-Keys && \
    chown -R appuser:appgroup /app /home/appuser/.aspnet

USER appuser

# ---------- 阶段 2：构建与发布 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# 先复制项目文件，充分利用依赖还原缓存。
COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/LinuxWebTool.Contracts/LinuxWebTool.Contracts.csproj", "src/LinuxWebTool.Contracts/"]
COPY ["src/LinuxWebTool.Infrastructure/LinuxWebTool.Infrastructure.csproj", "src/LinuxWebTool.Infrastructure/"]
COPY ["src/LinuxWebTool.WebHost/LinuxWebTool.WebHost.csproj", "src/LinuxWebTool.WebHost/"]

RUN dotnet restore "src/LinuxWebTool.WebHost/LinuxWebTool.WebHost.csproj"

COPY src ./src

RUN dotnet publish "src/LinuxWebTool.WebHost/LinuxWebTool.WebHost.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    --self-contained false \
    --no-restore \
    /p:UseAppHost=false \
    /p:DebugSymbols=false \
    /p:DebugType=None

# 清理调试符号，减小镜像体积
RUN find /app/publish -type f -name "*.pdb" -delete

# ---------- 阶段 3：最终生产镜像 ----------
FROM base AS final
WORKDIR /app
COPY --from=build --chown=appuser:appgroup /app/publish .

# 容器必须绑定 0.0.0.0:5270：appsettings 的 "Urls"(localhost:5270，面向桌面端) 在 .NET 8+ hosting 中
# 优先级高于 ASPNETCORE_URLS 环境变量（实测），因此发布后直接改写该值，否则端口映射完全失效。
RUN sed -i 's|"Urls": "http://localhost:5270"|"Urls": "http://0.0.0.0:5270"|' /app/appsettings.json

# data/ 聚合全部持久化数据：SQLite、admin.json、jwt 密钥、logs/ —— 单卷挂载即可完整持久化
VOLUME ["/app/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget -q --spider http://127.0.0.1:5270/app/ || exit 1

ENTRYPOINT ["dotnet", "LinuxWebTool.WebHost.dll"]
