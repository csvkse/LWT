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

# 核心安全守卫：禁止封禁本地回环与无效地址
if [ "$TARGET_IP" = "127.0.0.1" ] || [ "$TARGET_IP" = "::1" ] || [ "$TARGET_IP" = "localhost" ] || [ "$TARGET_IP" = "0.0.0.0" ]; then
    echo '{"protocolVersion":1,"status":"failed","error":"安全保护：禁止封禁本地回环地址","message":"安全保护：禁止封禁本地回环地址"}'
    exit 1
fi

BANNED_SOMETHING=0

# 1. 尝试 UFW 防火墙拦截
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw insert 1 deny from "${TARGET_IP}" to any port 22 comment 'LWT_SSH_BAN' >/dev/null 2>&1
    BANNED_SOMETHING=1
fi

# 2. 尝试 Iptables 防火墙拦截
if command -v iptables >/dev/null 2>&1; then
    iptables -C INPUT -s "${TARGET_IP}" -p tcp --dport 22 -m comment --comment "LWT_SSH_BAN" -j DROP 2>/dev/null || \
    iptables -I INPUT 1 -s "${TARGET_IP}" -p tcp --dport 22 -m comment --comment "LWT_SSH_BAN" -j DROP 2>/dev/null
    if [ $? -eq 0 ]; then
        BANNED_SOMETHING=1
    fi
fi

# 3. 兜底写入 /etc/hosts.deny
if [ -f /etc/hosts.deny ] || touch /etc/hosts.deny 2>/dev/null; then
    if ! grep -q "^sshd:[[:space:]]*${TARGET_IP}" /etc/hosts.deny 2>/dev/null; then
        echo "sshd: ${TARGET_IP}" >> /etc/hosts.deny 2>/dev/null
        if [ $? -eq 0 ]; then
            BANNED_SOMETHING=1
        fi
    else
        BANNED_SOMETHING=1
    fi
fi

if [ "$BANNED_SOMETHING" -eq 1 ]; then
    echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"ip\":\"${TARGET_IP}\",\"banned\":true},\"message\":\"成功将 ${TARGET_IP} 加入 SSH 防护黑名单\"}"
    exit 0
else
    echo "{\"protocolVersion\":1,\"status\":\"failed\",\"error\":\"未能将 IP 加入防火墙或 hosts.deny (权限不足或系统无可用防火墙)\",\"message\":\"封禁失败：无可用防火墙\"}"
    exit 1
fi
