#!/bin/bash
TARGET="${LWT_TARGET:-host}"
WHO_COUNT=$(who 2>/dev/null | wc -l)
SS_COUNT=0
if command -v ss >/dev/null 2>&1; then
    SS_COUNT=$(ss -tnp '( sport = :22 )' 2>/dev/null | grep -c 'ESTAB' || echo 0)
fi

ACTIVE_COUNT=$WHO_COUNT
if [ "$SS_COUNT" -gt "$ACTIVE_COUNT" ]; then
    ACTIVE_COUNT=$SS_COUNT
fi

BAN_COUNT=0
if [ -f /etc/hosts.deny ]; then
    H_COUNT=$(grep -c "^sshd:" /etc/hosts.deny 2>/dev/null || echo 0)
    BAN_COUNT=$((BAN_COUNT + H_COUNT))
fi

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    U_COUNT=$(ufw status 2>/dev/null | grep "22/tcp" | grep -c "DENY" || echo 0)
    BAN_COUNT=$((BAN_COUNT + U_COUNT))
fi

if command -v iptables >/dev/null 2>&1; then
    I_COUNT=$(iptables -L INPUT -n 2>/dev/null | grep -c "LWT_SSH_BAN" || echo 0)
    BAN_COUNT=$((BAN_COUNT + I_COUNT))
fi

echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"status\":\"Active\",\"target\":\"${TARGET}\",\"activeCount\":${ACTIVE_COUNT},\"bannedCount\":${BAN_COUNT},\"message\":\"在线会话: ${ACTIVE_COUNT} 个 | 封禁黑名单: ${BAN_COUNT} 条\"},\"message\":\"SSH 守卫监控就绪\"}"
