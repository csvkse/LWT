#!/bin/bash
TARGET_IP="$1"
if [ -z "$TARGET_IP" ]; then
    TARGET_IP="$LWT_PARAM_IP"
fi
if [ -z "$TARGET_IP" ]; then
    echo '{"protocolVersion":1,"status":"failed","error":"缺少目标 IP 参数","message":"缺少目标 IP 参数"}'
    exit 1
fi

if [ -f /etc/hosts.deny ]; then
    sed -i "/^sshd:[[:space:]]*${TARGET_IP}/d" /etc/hosts.deny 2>/dev/null
fi
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw delete deny from "${TARGET_IP}" to any port 22 >/dev/null 2>&1
fi

echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"ip\":\"${TARGET_IP}\"},\"message\":\"成功解除 ${TARGET_IP} 的 SSH 封禁黑名单\"}"
