#!/bin/bash
TARGET_IP="$1"
if [ -z "$TARGET_IP" ]; then
    echo '{"success":false,"message":"缺少目标 IP 参数"}'
    exit 1
fi
if [ "$TARGET_IP" = "127.0.0.1" ] || [ "$TARGET_IP" = "::1" ] || [ "$TARGET_IP" = "localhost" ]; then
    echo '{"success":false,"message":"安全保护：禁止封禁本地回环地址"}'
    exit 1
fi

touch /etc/hosts.deny 2>/dev/null
if grep -q "^sshd:[[:space:]]*${TARGET_IP}" /etc/hosts.deny 2>/dev/null; then
    echo "{\"success\":true,\"message\":\"IP ${TARGET_IP} 已经在封禁黑名单中\"}"
    exit 0
fi

echo "sshd: ${TARGET_IP}" >> /etc/hosts.deny 2>/dev/null
if [ $? -ne 0 ]; then
    echo '{"success":false,"message":"写入 /etc/hosts.deny 失败 (权限不足)"}'
    exit 1
fi

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw insert 1 deny from "${TARGET_IP}" to any port 22 comment 'LWT SSH Guard' >/dev/null 2>&1
fi

echo "{\"success\":true,\"message\":\"成功将 ${TARGET_IP} 加入 SSH 封禁黑名单\"}"
