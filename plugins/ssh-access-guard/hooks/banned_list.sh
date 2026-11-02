#!/bin/bash
ALL_IPS=""

# 1. 从 /etc/hosts.deny 读取
if [ -f /etc/hosts.deny ]; then
    H_IPS=$(grep "^sshd:" /etc/hosts.deny 2>/dev/null | awk '{print $2}' | tr '\n' ' ')
    ALL_IPS="${ALL_IPS} ${H_IPS}"
fi

# 2. 从 iptables 读取
if command -v iptables >/dev/null 2>&1; then
    I_IPS=$(iptables -L INPUT -n 2>/dev/null | grep "LWT_SSH_BAN" | awk '{print $4}' | tr '\n' ' ')
    ALL_IPS="${ALL_IPS} ${I_IPS}"
fi

# 3. 从 ufw 读取
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    U_IPS=$(ufw status 2>/dev/null | grep "22/tcp" | grep "DENY" | awk '{print $3}' | tr '\n' ' ')
    ALL_IPS="${ALL_IPS} ${U_IPS}"
fi

# 去重并格式化为 JSON 数组
LIST="[]"
DEDUP_IPS=$(echo "$ALL_IPS" | tr ' ' '\n' | grep -v '^$' | sort -u | tr '\n' ' ')
if [ -n "$DEDUP_IPS" ]; then
    LIST=$(echo "$DEDUP_IPS" | awk '{printf "["; for(i=1;i<=NF;i++) printf "\"%s\"%s", $i, (i==NF?"":","); printf "]" }')
fi

echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":${LIST},\"message\":\"获取封禁黑名单成功\"}"
