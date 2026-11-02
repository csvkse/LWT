#!/bin/bash
TARGET_IP="$1"
if [ -z "$TARGET_IP" ]; then
    TARGET_IP="$LWT_PARAM"
fi
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

if command -v iptables >/dev/null 2>&1; then
    iptables -D INPUT -s "${TARGET_IP}" -p tcp --dport 22 -m comment --comment "LWT_SSH_BAN" -j DROP 2>/dev/null
fi

echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"ip\":\"${TARGET_IP}\",\"unbanned\":true},\"message\":\"成功解除 ${TARGET_IP} 的 SSH 封禁\"}"
