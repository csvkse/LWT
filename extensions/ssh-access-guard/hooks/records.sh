#!/bin/bash
echo "=== 最近成功登录记录 (TOP 15) ==="
last -n 15 2>/dev/null || echo "无法读取 last 历史"
echo ""
echo "=== 最近登录失败与密码错误尝试 (TOP 15) ==="
lastb -n 15 2>/dev/null || grep "Failed password" /var/log/auth.log 2>/dev/null | tail -n 15 || echo "无失败记录或无权限读取"
echo ""
echo "=== 当前活跃在线会话 ==="
who -u 2>/dev/null || echo "无在线用户"
