#!/bin/bash
ACTIVE_COUNT=$(who 2>/dev/null | wc -l)
BAN_COUNT=0
if [ -f /etc/hosts.deny ]; then
    BAN_COUNT=$(grep -c "^sshd:" /etc/hosts.deny 2>/dev/null || echo 0)
fi
echo "{\"status\":\"Running\",\"message\":\"在线会话: ${ACTIVE_COUNT} 个 | 封禁黑名单: ${BAN_COUNT} 条\",\"pid\":null}"
