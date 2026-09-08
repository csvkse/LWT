# 开发经验与运行时缺陷复盘

> 本文沉淀 LinuxWebTool 本轮功能（SMB 挂载管理 + FFmpeg 媒体转码）开发与 **容器运行时验证** 中遇到的可复用经验。
> 重点不是"做了什么"，而是"踩了哪些编译期/CI 看不见、只有在真实运行时才暴露的坑"，以及对应的验证方法。

## 一、三个"编译通过但运行时崩"的缺陷

这三处缺陷本地 Debug 编译、架构测试、CI 全部通过，但容器一到就崩。**共同点：问题出在 DI 生命周期与数据库资源管理上，静态类型检查无法捕获。**

### 1. `AddHostedService<T>()` 只注册 `IHostedService`，不注册 `T` 本身

**现象**：Host 启动即崩，`System.InvalidOperationException: Unable to resolve service for type 'TranscodeQueueService' while attempting to activate 'WatchFolderService'`。

**根因**：`builder.Services.AddHostedService<TranscodeQueueService>()` 等价于：

```csharp
services.AddSingleton<IHostedService, TranscodeQueueService>();
```

它只让 DI 把该类型当作 `IHostedService` 解析（供 Host 启动），**并不会注册 `TranscodeQueueService` 这个具体类型**。而 `WatchFolderService` 构造函数依赖的是具体类型 `TranscodeQueueService` 而非接口，DI 在构建 `WatchFolderService` 时找不到 `TranscodeQueueService` 的注册，直接抛异常。

**修复**：

```csharp
builder.Services.AddSingleton<TranscodeQueueService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TranscodeQueueService>());
```

**经验**：
- `AddHostedService<T>` 的注册键是 `IHostedService`。若其他服务要注入 `T` 的类型本身，必须额外 `AddSingleton<T>()`。
- 凡是"A 注入 B、B 是后台服务"的结构，都要检查 B 是否同时以具体类型注册。
- 这类缺陷**编译期 0 警告、CI 全绿**，只有真正 `Build()` 启动 Host 才炸 —— 本地只有 `dotnet run` 或容器才暴露。

### 2. SQLite + `IsAutoCloseConnection=true` 的多连接共享竞争

**现象**：Host 能启动，但两个后台服务（`SmbMountStartupService`、`WatchFolderService`）启动阶段各自报错：

```
SmbMountStartupService → System.NullReferenceException ... SqliteConnection.Close()
WatchFolderService     → System.ObjectDisposedException ... Object name: 'SQLitePCL.sqlite3_stmt'
```

**根因**：`SqlSugarScope` 是单例注入所有 Store，配合 `IsAutoCloseConnection=true` 时，SqlSugar 会让**多个挂起的 async 操作共享同一个 `SqliteConnection`**。当其中一个操作结束并 `Close()` 该连接时，另一个仍挂起操作持有的 `sqlite3_stmt`（原生语句句柄）已被释放，于是读到 disposed 对象。

多个 `BackgroundService` 在 Host 启动时**并行**执行 `ExecuteAsync`，同一时刻并发查询不同表（smb_mount / watch_rule），正好触发这个竞争。

**修复**：

```csharp
IsAutoCloseConnection = false,
// 连接串追加 Default Timeout=30（写锁忙等，避免 database is locked）
```

关闭自动关闭后，SqlSugar 每次操作独立打开/关闭连接，`SqlSugarScope` 内部会 Dispose 每次操作的连接上下文，**不会有句柄泄漏**，天然隔离并发。

**经验**：
- `IsAutoCloseConnection=true` 适合"单线程/串行请求"模型（如逐请求的一个 controller action），**不适合多个 background service 共享单例 db 并发 async**。
- 判定是否泄漏的方法：跑高并发前后对比进程句柄数。本地验证 800 次并发读写，句柄仅 +2（正常抖动），证明 `false` 方案无泄漏。
- SQLite 本身是**单写者模型**，并发读安全但共享连接有隐藏竞争；遇到"connection is locked" / "stmt disposed" / "SqliteConnection.Close() NRE" 这三种错，先考虑连接隔离而非加锁。

### 3. 提交转码未携带预设实体（业务层数据丢失）

**现象**：提交任务成功返回 `已加入转码队列`，但任务立即 `status=3（失败）`，`errorOutput="未指定预设或自定义参数"`。

**根因**：`TranscodeController.Submit` 调用 `CreateJobAsync` 时传的是 `preset: null` 而非按 `request.PresetId` 查出来的预设实体，导致 `TranscodeJob.PresetId / PresetName` 落库为 `null`。执行器 `RunJobCoreAsync` 读到 `job.PresetId == null`，走"未指定预设或参数"失败分支。

**修复**：按 `request.PresetId` 查询预设实体传入，文件中/文件夹分支共用同一个预设实例。

**经验**：
- 分层传参时，避免"查询-组装"分离：调用方查出的实体要真正传给下层，而不是传 `null` 让下层再查或落空。
- 任务提交"成功返回"不等于"任务会成功"。**凡是异步队列任务，必须在任务表里记录足够上下文（PresetId/PresetName/OutputMode/Trigger）**，否则执行器无法凭 `job.Id` 还原意图。

## 二、验证方法的进化：为什么容器验证不可少

本轮验证路线：本地 Debug 编译 → 架构测试（6/6）→ 前端门禁 → CI（Build & Gates + Docker Image）——**全绿后依然在容器里崩**。后续每次都通过"重建镜像 → 容器跑真实链路 → 看日志"才定位根因。

**结论**：对于"多个后台服务 + 数据库 + 外部进程（ffmpeg/mount）"这种组合，**编译与单元门禁无法替代真实运行验证**。有价值的手段：

1. **看启动日志而非只看"进程起来了"**：`Application started` 不代表后台服务健康。要 grep `fail:` / `error:` / `exception`，尤其那些被 `BackgroundService` 内部 try/catch 吞掉、不阻断主进程的错误。
2. **端到端走 API 而非手动点 UI**：在容器内脚本化登录 → 查列表 → 提交任务 → 轮询状态 → 校验落盘文件。例如转码验证用 `ffprobe` 确认输出的 `format_name` / `duration` 与源一致。
3. **本地可复现纯逻辑**：转码的 `FfmpegArgsBuilder` / `OutputPathPlanner`（参数构造、输出规划、引号拆分）可在无 ffmpeg 的机器上单元验证；但**真实 ffmpeg 执行必须在有 ffmpeg 的环境（容器）**。
4. **句柄/资源泄漏**：`IsAutoCloseConnection` 之类改动，用并发前后句柄数对比来判泄漏与否。

## 三、本次沉淀的可复用设计取舍

- **转码不能复用 ShellExecutor**：ShellExecutor 是"整段跑完拿结果"模型（64KB 截断、超时判失败、`ReadToEnd` 全量收流）。转码是小时级任务，需要实时进度、取消、崩溃恢复，故用独立 `TranscodeQueueService`（`Process` + `-progress pipe:1` 解析 + 队列持久化 + 重启恢复）。
- **替换模式必须"先临时文件、成功才替换"**：先写 `.lwt-tmp`，`exit 0` 且输出非空 → `File.Move` → 删源；失败/取消清理临时文件，源文件永不损毁。
- **网络挂载盘禁用 inotify**：SMB/NFS 服务器端变更收不到事件，`WatchFolderService` 默认轮询（快照 + 两段稳定判定），本地盘才切 `FileSystemWatcher`。
- **监听自循环三重排除**：临时文件后缀、队列活动路径、转码完成登记（24h），否则 mp4→mp4 压缩类规则会无限自触发。
- **SMB 密码不进命令行**：`mount -t cifs` 的 `password=` 会暴露在 `/proc`，改用 `credentials=` 文件（`data/mount-creds/<id>`，600 权限）。
- **不写 `/etc/fstab`**：改为应用启动重放"AutoMount"条目，不碰宿主机引导配置、不破坏开机流程。
- **状态页磁盘白名单联动**：应用内 SMB 挂载点注册到 `SystemStatusProvider.ManagedMountPoints`，绕过虚拟文件系统过滤，`df` 采集才能展示。

## 四、环境验证的限制（wslc）

本次在 WSL Container CLI（wslc）验证，需注意其能力边界：

- **不支持**：`--privileged`、`--device`、`--cap-add`、`--mount`、rslave 三段落卷。
- 因此 **SMB 挂载**（需要 `--privileged --user root` + `SYS_ADMIN`）在 wslc 无法端到端验证，只能标准 Linux Docker。**转码**（不需特权）可以完整验证。
- 容器镜像体积因加入 ffmpeg + cifs-utils 增至 389MB（基础曾 251MB），属可接受。

## 五、给后续开发的检查清单

提交前除了编译 + 门禁，额外自查：

- [ ] 凡"服务 A 注入服务 B 且 B 是 `BackgroundService`"，B 是否以 **具体类型** 注册（而不只是 `IHostedService`）？
- [ ] 单例 `ISqlSugarClient` 被多个后台服务并发使用吗？`IsAutoCloseConnection` 是否因此该为 `false`？
- [ ] 异步队列任务落库时，是否带齐执行所需上下文（预设 / 参数 / 模式 / 触发源）？
- [ ] 替换/删除类操作是否"先临时后原子"，失败不损源？
- [ ] 监听/扫描类功能是否排除自身产生的输出，避免自循环？
- [ ] 是否在容器环境跑过"启动日志无 error + 端到端走一次关键 API + 校验落盘产物"？
