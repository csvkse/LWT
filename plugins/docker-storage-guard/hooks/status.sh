#!/bin/bash
set -e

echo "=== [DOCKER STORAGE STATUS AUDIT] ==="

# 1. 根分区指标
ROOT_DISK_LINE=$(df -P / | tail -n 1)
ROOT_TOTAL=$(echo "$ROOT_DISK_LINE" | awk '{print $2}')
ROOT_USED=$(echo "$ROOT_DISK_LINE" | awk '{print $3}')
ROOT_AVAIL=$(echo "$ROOT_DISK_LINE" | awk '{print $4}')
ROOT_PCT=$(echo "$ROOT_DISK_LINE" | awk '{print $5}' | tr -d '%')

echo "Root Partition: Used ${ROOT_PCT}%, Available $(awk "BEGIN {printf \"%.2f\", $ROOT_AVAIL/1024/1024}") GB"

# 2. 检查宿主机 /etc/docker/daemon.json 中 builder.gc 配置
DAEMON_GC_CONFIGURED=false
if [ -f /etc/docker/daemon.json ]; then
    if grep -q "builder" /etc/docker/daemon.json 2>/dev/null && grep -q "defaultKeepStorage" /etc/docker/daemon.json 2>/dev/null; then
        DAEMON_GC_CONFIGURED=true
    fi
fi
echo "Daemon BuildKit GC Configured: $DAEMON_GC_CONFIGURED"

# 3. 统计悬空镜像 (<none>:<none>)
DANGLING_COUNT=$(docker images -f "dangling=true" -q 2>/dev/null | wc -l || echo "0")
echo "Dangling Images Count (<none>): $DANGLING_COUNT"

# 4. 执行 docker system df
echo "--- Docker System DF Breakdown ---"
docker system df 2>/dev/null || true

# 5. 输出机器可读结构化 JSON (便于 UI 解析)
cat << EOF

__JSON_START__
{
  "rootDiskPct": $ROOT_PCT,
  "rootDiskAvailKb": $ROOT_AVAIL,
  "danglingCount": $DANGLING_COUNT,
  "daemonGcConfigured": $DAEMON_GC_CONFIGURED,
  "status": "$([ "$ROOT_PCT" -lt 80 ] && echo "Healthy" || echo "Warning")"
}
__JSON_END__
EOF
