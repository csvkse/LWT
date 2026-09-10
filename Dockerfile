# -----------------------------------------------------------------------------
# 鏋勫缓涓婁笅鏂?= 浠撳簱鏍圭洰褰曪細docker build -t linuxwebtool .
# 鍙傝€?DNSPodForNETCore(InfiniWeb) 鐨勪笁闃舵缁撴瀯锛?#   base  鈥斺€?aspnet alpine 杩愯鏃?+ 鏃跺尯/ICU + bash/procps锛堣剼鏈笌鐘舵€侀噰闆嗕緷璧栵級+ 闈?root 鐢ㄦ埛
#   build 鈥斺€?sdk 杩樺師锛坈sproj 鍏堣鎷疯礉鍒╃敤缂撳瓨锛変笌鍙戝竷
#   final 鈥斺€?base + 鍙戝竷浜х墿
# -----------------------------------------------------------------------------

# ---------- 闃舵 1锛氳繍琛屾椂鍩虹 ----------
# VENDOR 鎸?GPU 鍘傚晢閫夎 VA 椹卞姩锛坕ntel/amd/nv/all/cpu锛夛紱瑙佷笅鏂?鎸夊巶鍟嗚 VA 椹卞姩"灞傘€?# 椤跺眰 ARG 浠呬綔涓洪粯璁ゅ€硷紝base 闃舵闇€鍐嶆澹版槑鎵嶈兘琚?RUN 寮曠敤銆?ARG VENDOR=all

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine AS base
# 閲嶆柊澹版槑鍏ㄥ眬 ARG锛堝闃舵涓?FROM 涔嬪墠鐨?ARG 闇€鍦ㄥ搴旈樁娈甸噸鏂?ARG 鎵嶈兘琚?RUN 浣跨敤锛?ARG VENDOR
WORKDIR /app
EXPOSE 5270

ENV TZ=Asia/Shanghai \
    LANG=en_US.UTF-8 \
    LC_ALL=en_US.UTF-8 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# bash      鈥斺€?鑴氭湰绫诲瀷鎵ц渚濊禆 /bin/bash
# procps    鈥斺€?绯荤粺鐘舵€侀〉鐨?ps 閲囬泦锛坅lpine 鑷甫 busybox ps 涓嶆敮鎸?-eo锛?# usbutils/pciutils/kmod 鈥斺€?纭欢鏌ョ湅宸ュ叿锛坙susb / lspci / lsmod锛?# util-linux-misc 鈥斺€?nsenter锛氶厤鍚?--privileged --pid=host --user root 鑷姩閲囬泦瀹夸富鍏ㄩ儴纾佺洏
# cifs-utils 鈥斺€?SMB 鎸傝浇绠＄悊锛坢ount -t cifs锛涢渶 --privileged --user root 杩愯锛?# nethogs   鈥斺€?姣忚繘绋嬬綉缁滈€熺巼閲囬泦锛坱racemode锛涗粎 --privileged --user root 杩愯鏃剁敓鏁堬紝闈炵壒鏉冨垯鎺㈡祴璺宠繃锛?# ffmpeg    鈥斺€?濯掍綋杞爜锛堝惈 ffprobe锛屼竴娆?涓€娆℃€ц浆鐮?/ 闃熷垪 / 鐩戝惉鑷姩杞爜鍏ㄤ緷璧栧畠锛?# tzdata/icu 鈥斺€?鏃跺尯涓庝腑鏂囧叏鐞冨寲
# libva/libva-utils 鈥斺€?VA-API 鐢ㄦ埛鎬佸簱涓庤瘖鏂伐鍏凤紙vainfo锛夈€俈A 缂栫爜鑳藉姏鐢辫繍琛屾椂閫忎紶椹卞姩鎺ョ锛?#   ffmpeg 宸插唴缃?h264_vaapi/hevc_vaapi/nvenc 绛夌‖浠剁紪鐮佸櫒鏀寔锛岀己 libva 鏃?--device=/dev/dri 閫忎紶鏍告樉涔熸棤娉曠湡姝ｇ紪鐮併€?#
# 闀滃儚鐦﹁韩锛歏A 椹卞姩锛坕ntel iHD 39.5M / mesa gallium 42M锛変笉鍐嶆棤鏉′欢鍏ㄦ墦鍖咃紝鎸?VENDOR 鍒嗗眰閫夎锛?#   浠呰鎵€鐢?GPU 鍘傚晢鐨勭敤鎴锋€侀┍鍔紝閬垮厤"鍏ㄨ兘闀滃儚"鐧界櫧澧炲锛堣瑙?base 闃舵搴曢儴"鎸夊巶鍟嗚 VA 椹卞姩"灞傦級銆?RUN apk add --no-cache bash procps usbutils pciutils kmod util-linux-misc cifs-utils nethogs \
      ffmpeg libva libva-utils tzdata icu-libs && \
    cp /usr/share/zoneinfo/$TZ /etc/localtime && \
    echo $TZ > /etc/timezone

# 鎸?GPU 鍘傚晢瑁?VA 鐢ㄦ埛鎬侀┍鍔紙缂栫爜鍣ㄥ凡鍦?ffmpeg 鍐咃紝杩欓噷鍙璁?-vaapi_device 鐪熸鍙敤鐨勯┍鍔ㄥ簱锛夛細
#   intel -> iHD锛圓lder Lake-N / N100 绛夋柊鏍告樉蹇呴渶锛屾彁渚?iHD_drv_video.so锛夛紱
#            Alpine 鐨?mesa-va-gallium 浠呭惈 AMD/NVIDIA 绌哄３閾炬帴锛岀己 Intel iHD 鏃?vainfo 鎶?"va_openDriver() -1"
#   amd   -> mesa-va-gallium锛坮adeonsi 瑙ｇ爜锛汚lpine 璇ュ寘浠?VA 瑙ｇ爜涓轰富锛孉MD VA 缂栫爜鍙楅檺锛岃窇涓嶄簡鏃跺簲鐢ㄨ嚜鍔ㄥ洖閫€杞欢缂栫爜锛?#   nv    -> 涓嶈 VA 椹卞姩锛歂VIDIA 璧?NVENC锛岀敱閮ㄧ讲渚?nvidia-container-toolkit + --gpus 閫忎紶椹卞姩锛宖fmpeg 宸插惈 nvenc 鏀寔
#   all   -> 鍏ㄨ锛堝厹搴曪紝涓庡師琛屼负涓€鑷达級
#   cpu/none -> 绾蒋浠剁紪鐮侊紝涓嶈 VA 椹卞姩
RUN echo "==> VENDOR=${VENDOR}" && \
    case "${VENDOR}" in \
      intel) apk add --no-cache intel-media-driver ;; \
      amd)   apk add --no-cache mesa-va-gallium ;; \
      nv|nvidia) echo "nvenc 鐢?nvidia-container-toolkit 閫忎紶椹卞姩锛岄暅鍍忓唴缃?nvenc 缂栫爜鍣ㄦ敮鎸侊紝涓嶈 VA 椹卞姩" ;; \
      all)   apk add --no-cache intel-media-driver mesa-va-gallium ;; \
      cpu|none|*) echo "绾蒋浠剁紪鐮侊紝涓嶈 VA 椹卞姩" ;; \
    esac

# 闈?root 杩愯锛沝ata 涓鸿繍琛屾湡鏁版嵁鐩綍锛圫QLite/鍑嵁/jwt 瀵嗛挜/鏃ュ織鍏ㄩ儴鑱氬悎浜庢锛屽崟鍗锋寔涔呭寲锛夈€?# 鑻ラ渶瑕佹墽琛?systemctl/docker 绛夌壒鏉冩寚浠わ紝鍙皢涓嬫柟 USER 鏀逛负 root 鎴栭儴缃叉椂瑕嗙洊銆?# 鍔犲叆 audio 缁勶細瀹瑰櫒閫忎紶 --device=/dev/dri 鍚庯紝renderD128 灞?audio 缁勶紝闈?root 闇€璇ョ粍鎵嶈兘鐢?VA-API銆?RUN addgroup -g 1001 appgroup && \
    adduser -u 1001 -G appgroup -s /bin/bash -D appuser && \
    addgroup appuser audio 2>/dev/null; \
    mkdir -p /app/data /home/appuser/.aspnet/DataProtection-Keys && \
    chown -R appuser:appgroup /app /home/appuser/.aspnet

USER appuser

# ---------- 闃舵 2锛氭瀯寤轰笌鍙戝竷 ----------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# 鍏堝鍒堕」鐩枃浠讹紝鍏呭垎鍒╃敤渚濊禆杩樺師缂撳瓨銆?COPY ["Directory.Build.props", "Directory.Packages.props", "./"]
COPY ["src/LinuxWebTool.Contracts/LinuxWebTool.Contracts.csproj", "src/LinuxWebTool.Contracts/"]
COPY ["src/LinuxWebTool.Infrastructure/LinuxWebTool.Infrastructure.csproj", "src/LinuxWebTool.Infrastructure/"]
COPY ["src/LinuxWebTool.WebHost/LinuxWebTool.WebHost.csproj", "src/LinuxWebTool.WebHost/"]

RUN dotnet restore "src/LinuxWebTool.WebHost/LinuxWebTool.WebHost.csproj"

COPY src ./src

RUN apk add --no-cache clang build-base zlib-dev
RUN dotnet publish "src/LinuxWebTool.WebHost/LinuxWebTool.WebHost.csproj" \
    -c $BUILD_CONFIGURATION \
    -o /app/publish \
    /p:PublishAot=true \
    -r linux-musl-x64

# 娓呯悊璋冭瘯绗﹀彿锛屽噺灏忛暅鍍忎綋绉?RUN find /app/publish -type f -name "*.pdb" -delete

# ---------- 闃舵 3锛氭渶缁堢敓浜ч暅鍍?----------
FROM base AS final
WORKDIR /app
COPY --from=build --chown=appuser:appgroup /app/publish .

# appsettings.json 宸插皢 Urls 璁句负 http://0.0.0.0:5270锛屽鍣ㄥ唴鐩存帴鐢熸晥锛?# 鏈湴寮€鍙戠敱 launchSettings.json 鐨?applicationUrl 瑕嗙洊锛屼簩鑰呬簰涓嶅共鎵般€?
# data/ 鑱氬悎鍏ㄩ儴鎸佷箙鍖栨暟鎹細SQLite銆乤dmin.json銆乯wt 瀵嗛挜銆乴ogs/ 鈥斺€?鍗曞嵎鎸傝浇鍗冲彲瀹屾暣鎸佷箙鍖?VOLUME ["/app/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD wget -q --spider http://127.0.0.1:5270/app/ || exit 1

ENTRYPOINT ["./LinuxWebTool.WebHost"]

