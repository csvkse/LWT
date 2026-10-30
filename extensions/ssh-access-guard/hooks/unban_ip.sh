#!/bin/bash
TARGET_IP="$1"
if [ -z "$TARGET_IP" ]; then
    echo '{"success":false,"message":"缺少目标 IP 参数"}'
    exit 1
fi

if [ -f /etc/hosts.deny ]; then
    sed -i "/^sshd:[[:space:]]*${TARGET_IP}/d" /etc/hosts.deny 2>/dev/null
fi

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw delete deny from "${TARGET_IP}" to any port 22 >/dev/null 2>&1
fi

echo "{\"success\":true,\"message\":\"成功解封 IP ${TARGET_IP}\"}"
