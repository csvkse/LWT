#!/bin/bash
if [ ! -f /etc/hosts.deny ]; then
    echo "未配置 /etc/hosts.deny (封禁黑名单为空)"
    exit 0
fi
echo "=== /etc/hosts.deny SSH 封禁名单 ==="
grep "^sshd:" /etc/hosts.deny 2>/dev/null || echo "暂无已封禁 IP"
