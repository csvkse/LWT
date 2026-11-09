# 剩余核心模块全流程审计与优化方案

本文档针对 LinuxWebTool 剩余核心功能模块（Shell 指令执行器、指令管理库、Quartz 定时任务调度、系统指标采样与清理、存储挂载管理、管理员认证与日志审计）进行全流程代码审计，梳理存在的逻辑与性能问题，并制定实施优化方案。

---

## 一、审计发现与问题清单

### 1. 【P0 严重】Shell 执行器超时机制完全失效导致并发死锁
- **位置**：`src/LinuxWebTool.Infrastructure/Shell/ShellExecutor.cs`
- **现象**：`RunProcessAsync` 中定义了 `timeoutCts` 并传入超时参数 `TimeSpan timeout`，但在启动 `WaitForExitAsync` 之前**漏掉了 `timeoutCts.CancelAfter(timeout)`**。
- **影响**：任何阻塞命令（如未带 `-c` 的 ping、挂起脚本、输入等待等）将永远不会超时退出，持续占用 `_gate`（信号量并发槽）。当挂起进程达到默认并发上限（4个）时，后续所有的系统命令、快速执行、脚本及定时任务全线死锁。
- **优化**：在 `using (var timeoutCts = ...)` 代码块中加入 `timeoutCts.CancelAfter(timeout)`，确保命令严格在限定时间内退出并杀灭进程树。

### 2. 【P1 逻辑】Bash 脚本执行后「最近执行时间」从不更新
- **位置**：`src/LinuxWebTool.WebHost/Routes/CommandsController.cs`
- **现象**：在 `Execute` 接口中，代码显式判断 `if (command.ScriptType != (int)ScriptType.BashScript)`，唯独跳过了 Bash 脚本的 `UpdateLastExecTimeAsync`。
- **影响**：前端指令列表中，任何保存的 Bash 脚本无论执行过多少次，「最近执行时间」始终显示为空 `—`，与命令行模式指令表现分裂。
- **优化**：移除该过滤分支，所有类型的已保存指令在手动或执行成功后均同步更新 `LastExecTime`。

### 3. 【P1 逻辑/审计】定时任务手动触发源标记混淆与异常状态丢失
- **位置**：`src/LinuxWebTool.Infrastructure/Scheduling/ScheduledCommandJob.cs`
- **现象**：
  1. 通过“立即执行”手动触发的任务（走 `LinuxCommandOnce` 组），写执行历史时的 `TriggerBy` 被硬编码为 `"schedule"`，无法在历史审计中分辨是定时轮询触发还是管理员手动点选；
  2. 当引用的底层指令被删除（`command is null`）时，直接 `return` 退出，漏掉了调用 `scheduleStore.UpdateRunInfoAsync` 更新 `LastRunTime`。
- **优化**：判断 `context.JobDetail.Key.Group == "LinuxCommandOnce"` 动态设置 `TriggerBy = "manual"` 或 `"schedule"`；在指令缺失异常分支补充回写 `LastRunTime`。

### 4. 【P1 性能/存储】系统监控快照历史数据无限膨胀
- **位置**：`src/LinuxWebTool.Infrastructure/SystemInfo/SystemStatusSampleService.cs`
- **现象**：`ClearOlderThanAsync` 仅在服务启动时调用了一次，在常驻运行的 30 秒 PeriodicTimer 循环中再未触发。
- **影响**：在长期 7x24 小时运行环境下，系统每分钟采样一次全量整机状态（每天 1,440 条），`system_status` 等四张监控表将累积上百万行无用历史数据，导致 SQLite 数据库体积急剧膨胀，拖慢控制台历史趋势图查询速度。
- **优化**：引入 `_lastRetentionCleanup` 时间戳，在后台循环中每 24 小时执行一次过期历史指标自动清理。

### 5. 【P1 体验】存储挂载新建时状态机延迟感知
- **位置**：`SmbMountsController.cs`、`WebDavMountsController.cs`、`RcloneMountsController.cs`
- **现象**：在 `Update` 方法中均调用了 `mountHealth.ConfigurationChanged(desc)` 唤醒状态机，但在 `Create`（新建挂载）方法中遗漏了通知与 `ManagedMountPoints` 登记。
- **影响**：用户在 Web 端新增挂载配置后，状态机必须等待 30 秒后的定时全量同步才会开始探活与自动挂载，产生明显的 UI 状态滞后。
- **优化**：在各挂载控制器的 `Create` 接口持久化成功后，立即调用 `mountHealth.ConfigurationChanged(desc)` 并登记受控挂载点。

### 6. 【P2 内存】认证失败 IP 锁定字典缺乏淘汰
- **位置**：`src/LinuxWebTool.WebHost/Routes/AuthController.cs`
- **现象**：`Failures` 并发字典对公网恶意扫描或失败 IP 记录无定期清理机制，若遇到分布式暴力探测，字典容量将持续累加。
- **优化**：在记录失败或字典超长（> 100 项）时，主动清理已过期的 IP 锁定条目。

### 7. 【P2 性能】文件日志热路径冗余系统调用
- **位置**：`src/LinuxWebTool.Infrastructure/Logging/FileLoggerProvider.cs`
- **现象**：每次输出日志时均在持锁状态下调用 `Directory.CreateDirectory(_options.Directory)`，产生大量冗余的文件系统元数据检查。
- **优化**：增加目录已初始化标记，避免每次高频日志写入都触发系统调用。

---

## 二、实施计划与测试验证

1. **Shell 执行器超时机制修复**：
   - 在 `ShellExecutor.cs` 中添加 `timeoutCts.CancelAfter(timeout)`。
2. **指令库最近执行时间更新修复**：
   - 在 `CommandsController.cs` 中统一调用 `UpdateLastExecTimeAsync`。
3. **调度任务触发源与状态记录修复**：
   - 在 `ScheduledCommandJob.cs` 中根据 Quartz 分组区分 `"manual"` / `"schedule"` 并完善执行时间回写。
4. **监控采样自动过期清理机制增强**：
   - 在 `SystemStatusSampleService.cs` 中加入 24 小时周期清理逻辑。
5. **存储挂载实时响应增强**：
   - 在 SMB / WebDAV / Rclone 控制器的 `Create` 接口通知 `mountHealth.ConfigurationChanged`。
6. **安全与日志细节优化**：
   - 修剪 `AuthController.cs` 中的失败 IP 记录，并在 `FileLoggerProvider.cs` 中消除冗余目录创建。
7. **自动化测试验证**：
   - 执行 `dotnet test`，确保架构测试与集成测试 100% 绿色通过。
