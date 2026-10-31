#!/bin/bash
LIST="[]"
if [ -f /etc/hosts.deny ]; then
    IPS=$(grep "^sshd:" /etc/hosts.deny 2>/dev/null | awk '{print $2}' | tr '\n' ' ')
    if [ -n "$IPS" ]; then
        LIST=$(echo "$IPS" | awk '{printf "["; for(i=1;i<=NF;i++) printf "\"%s\"%s", $i, (i==NF?"":","); printf "]" }')
    fi
fi
echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":${LIST},\"message\":\"获取封禁黑名单成功\"}"
