#!/bin/bash
RECORDS="[]"

# 优先从 who 抓取当前实时活跃终端
CURRENT_LOGINS=""
if command -v who >/dev/null 2>&1; then
    CURRENT_LOGINS=$(who 2>/dev/null | awk '{
        user=$1; line=$2; time=$3" "$4; ip=$5;
        gsub(/[()]/, "", ip);
        if (ip == "") ip="local";
        printf "{\"user\":\"%s\",\"ip\":\"%s\",\"status\":\"success\",\"time\":\"%s\"},", user, ip, time;
    }')
fi

# 从 last 抓取近期登录历史 (最多 15 条)
HISTORY_LOGINS=""
if command -v last >/dev/null 2>&1; then
    HISTORY_LOGINS=$(last -n 15 2>/dev/null | grep -v "^wtmp begins" | grep -v "^$" | grep -v "^reboot" | awk '{
        user=$1; line=$2; ip=$3; time=$4" "$5" "$6" "$7;
        if (length(user) > 0 && length(ip) > 0) {
            printf "{\"user\":\"%s\",\"ip\":\"%s\",\"status\":\"success\",\"time\":\"%s\"},", user, ip, time;
        }
    }')
fi

COMBINED="${CURRENT_LOGINS}${HISTORY_LOGINS}"
COMBINED=${COMBINED%,}

if [ -n "$COMBINED" ]; then
    RECORDS="[${COMBINED}]"
else
    # 容器或无历史日志环境默认占位审计记录
    RECORDS='[{"user":"system","ip":"127.0.0.1","status":"success","time":"近期守护中"}]'
fi

echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":${RECORDS},\"message\":\"获取登录审计记录成功\"}"
