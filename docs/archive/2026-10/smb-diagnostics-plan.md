# SMB 生命周期与故障诊断方案

## 范围与实施顺序

1. 异常终止前采集有限进程快照，接入现有程序与调试日志。
2. 启动记录运行 ID、版本、挂载命名空间和 CIFS 挂载库存；仅输出安全选项白名单。
3. 在数据目录维护 run-state.txt，ApplicationStopped 时记录正常完成；不新增退出卸载行为。
4. 汇总远端、旧访问、卸载、新挂载及新访问耗时。
5. 错误文案描述观察结果，不将超时直接归因于 SMB 会话退化。
6. 提供宿主机只读诊断步骤。

## 查看日志

程序日志搜索 `SMB process snapshot`，使用 SnapshotId 到调试日志匹配 `snapshot detail` 和 `snapshot child`。快照在 kill 前采集，不读取 cmdline/environ；单项读取最多 2048 字符，总异步读取预算 500 毫秒，最多采集四个直接子进程。/proc 文件打开与同步状态读取不具备硬超时保证。权限不足或已退出记录 unavailable，不影响恢复。

启动日志搜索 `Application run start`、`Startup CIFS inventory`；退出搜索 `Application stopping`、`Application stopped`；恢复搜索 `Mount recovery summary`。阶段值按同一任务累加，整体时间还包括排队与协调锁等待。远端、目录、容量细分沿用 SMB probe 日志。

运行标记只证明未记录正常完成，不能证明崩溃或 SMB 故障；多个实例共用数据目录、写入失败都可能产生同样提示。仅支持单实例诊断用途。挂载 ID 必须结合运行 ID、命名空间和来源解释，不能直接跨容器比较。

## 宿主机证据

故障期间在宿主机只读采集，保留与应用相同时间范围：

```bash
date -Is
awk '$0 ~ / - cifs / && $5 ~ /^\/lwt(\/|$)/ {print}' /proc/self/mountinfo
docker inspect --format '{{.State.StartedAt}} {{.State.FinishedAt}} {{.State.ExitCode}} {{.State.OOMKilled}}' linuxwebtool
docker events --since 20m --until "$(date -Is)" --filter container=linuxwebtool
dmesg -T | tail -n 300
```

内核日志可搜索 CIFS/SMB，容器已删除时 inspect 不能回溯旧实例，应在删除前保存。日志分享前移除地址、路径和敏感内容。SIGKILL 无法由应用记录退出完成。不要在异常共享上用 ls/df 采集宿主机证据，避免额外阻塞。

## 验证边界

本批执行编译与差异检查，不运行测试或生产故障注入。Windows 编译无法验证 Linux /proc 权限、强制退出标记与真实 CIFS 故障，需要后续现场核对。
