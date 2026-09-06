# -----------------------------------------------------------------------------
# 构建上下文 = 仓库根目录：docker build -t linuxwebtool .
# 参考 DNSPodForNETCore(InfiniWeb) 的三阶段结构：
#   base  —— aspnet alpine 运行时 + 时区/ICU + bash/procps（脚本与状态采集依赖）+ 非 root 用户
#   build —— sdk 还原（csproj 先行拷贝利用缓存）与发布
#   final —— base + 发布产物
# -----------------------------------------------------------------------------

# ---------- 阶段 1：运行时基础 ----------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
WORKDIR /app
EXPOSE 8080

ENV TZ=Asia/Shanghai \
    LANG=en_US.UTF-8 \
    LC_ALL=en_US.UTF-8 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# bash      —— 脚本类型执行依赖 /bin/bash
# procps    —— 系统状态页的 ps 采集（alpine 自带 busybox ps 不支持 -eo）
# tzdata/icu —— 时区与中文全球化
RUN apk add --no-cache bash procps tzdata icu-libs && \
    cp /usr/share/zoneinfo/$TZ /etc/localtime && \
    echo $TZ > /etc/timezone

# 非 root 运行；data 为运行期数据目录（SQLite/凭据/jwt 密钥/日志全部聚合于此，单卷持久化）。
# 若需要执行 systemctl/docker 等特权指令，可将下方 USER 改为 root 或部署时覆盖。
RUN addgroup -g 1001 appgroup && \
    adduser -u 1001 -G appgroup -s /bin/bash -D appuser && \
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

# data/ 聚合全部持久化数据：SQLite、admin.json、jwt 密钥、logs/ —— 单卷挂载即可完整持久化
VOLUME ["/app/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget -q --spider http://127.0.0.1:8080/app/ || exit 1

ENTRYPOINT ["dotnet", "LinuxWebTool.WebHost.dll"]
