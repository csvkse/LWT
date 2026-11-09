# 终端连接、指令与定时任务执行问题分析及修复方案

## 1. 问题背景与现象

在日常使用和本地/容器环境中，用户报告并出现了以下三个紧密关联的执行与连接故障：

1. **终端连接失败并卡死**：
   - 终端页面打开或页面刷新后，终端输出：
     ```text
     [正在连接后台终端...]
     [会话建立失败: 会话不存在或服务已重启]
     ```
   - 界面弹出错误 Toast，终端停留在报错状态，刷新页面依然无法自愈。
2. **指令列表存在但无法执行**：
   - 在“指令管理”列表中能正常展示已保存的指令（如 `list-dir` 等）；
   - 但点击“执行”时，后端直接返回 HTTP 404：`{"message": "指令不存在"}`，导致指令无法运行。
3. **定时任务无法触发与执行**：
   - 定时任务在列表正常展示且状态为“启用”；
   - 后台调度日志（每周期）持续报错：
     `[WARN] ScheduledCommandJob - 定时任务不存在，跳过执行：<taskId>`；
   - 在前端点击“立即运行”时同样返回 HTTP 404：`{"message": "定时任务不存在"}`。

---

## 2. 根本原因分析 (Root Causes)

经过代码审查与实时环境诊断，上述问题由以下三个层面的机制与缺陷导致：

### 2.1 终端会话：前端缓存与后端内存生命周期脱节，缺少自愈重试

- **前端行为**：前端 `TerminalView.js` 通过浏览器的 `sessionStorage`（键 `linuxwebtool.terminalTabs`）缓存了打开过的终端标签页信息，其中包括上次由后端颁发的 `sessionId`。页面加载或组件挂载时，`onMounted` 会自动读取该历史 ID，并通过 `GET /api/terminal/sessions/{sessionId}` 尝试恢复会话。
- **后端行为**：后端的 `PtySessionManager` 采用内存字典（`ConcurrentDictionary`）维护会话和 PTY 进程：
  1. 当服务发生过重启（如重新发布、`start.bat` 重启等），内存中的所有终端进程及会话对象全被销毁；
  2. 未连接的闲置会话默认在 120 秒后由后台 `CleanupAsync` 自动清理并销毁；
  3. 终端进程已退出超过 300 秒后也会被销毁。
- **缺陷暴露**：当旧会话被销毁后，前端请求返回 404（附带“会话不存在或服务已重启”）。前端代码直接 `throw new Error()` 中断，**没有自动废弃失效 ID 并创建新终端**，导致用户卡死在报错红字，刷新亦无法解决。

### 2.2 核心致命 Bug：SQLite 大小写比对与 .NET Guid 序列化冲突

这是导致**指令与定时任务在数据库里明明存在，但单条按 ID 查询必报 404** 的根本原因：

1. **数据库存储小写**：
   在 SQLite 数据库（`linuxweb.db`）中，`linux_command` 与 `schedule_task` 等表的主键定义为 `Id TEXT PRIMARY KEY`。数据插入或从前端传入时，`Id` 字段实际存储为**全小写字符串**（如 `'0bd99077-13be-430c-a71f-45c003ce33bd'`）。
2. **列表查询能读出**：
   在获取列表（`GetAllAsync`）时，SQL 中没有 `WHERE Id = @Id` 过滤条件，Dapper 从 SQLite 读出小写字符串并反序列化为 C# `Guid`，输出为 JSON 小写字符串给前端，因此列表上能够正常展示。
3. **按 ID 查询必定查空（False）**：
   - 在执行指令（`commandStore.GetByIdAsync(id)`）、定时任务调度触发（`scheduleStore.GetByIdAsync(taskId)`）以及立即运行时：
     ```sql
     SELECT * FROM linux_command WHERE Id = @Id
     SELECT * FROM schedule_task WHERE Id = @Id LIMIT 1
     ```
   - 底层驱动 `Microsoft.Data.Sqlite` 在向 SQLite 传递 `Guid` 参数 `@Id` 时，默认将其转换为**全大写字符串**（如 `'0BD99077-13BE-430C-A71F-45C003CE33BD'`）；
   - SQLite 的 `TEXT` 字段在默认排序规则下，`=` 比较是**严格区分大小写的二进制比较（Binary Collation）**；
   - 结果：全小写存储值与全大写参数比较永远为 `false`，`GetByIdAsync` **必然返回 null**！
   - 由此引发一系列连锁反应：
     * 指令执行接口响应 404 `指令不存在`；
     * 定时任务每 5 分钟由 Quartz 调度触发时，第一步读取任务即返回 null，警告 `定时任务不存在，跳过执行`；
     * 定时任务点击“立即运行”返回 404 `定时任务不存在`；
     * 定时任务即使执行，读取关联指令 `task.CommandId` 同样查空，报错 `任务引用的指令已被删除`。

### 2.3 执行环境与错误信息透传问题

1. **Shell 执行器环境限制**：
   - 在 Windows 环境下，`ShellExecutor` 普通指令默认调用 `cmd.exe /c` 执行，若指令包含 Linux 专有命令（如 `df -h`, `top`, `ls` 等）会报 `'xxx' 不是内部或外部命令`；
   - 脚本模式（`BashScript`）依赖检测特定路径的 Git Bash，未安装或路径不对时直接拒绝；
   - 在 Docker 容器中，以非 root 用户（`USER appuser`）运行在容器命名空间内，若执行宿主机管理指令（如 `systemctl`, `mount`, `reboot`）会因缺少宿主机特权与工具而失败。
2. **启动失败信息丢失**：
   - 当 `ShellExecutor` 启动进程抛出异常或未找到 bash 时，只设置了 `StartFailure`，但未将错误写入 `ErrorOutput`，导致上层控制器或执行记录中的 `ErrorOutput` 为空，前端展示空白错误。

### 2.4 Minimal API 路由匹配：`[FromBody]` 约束导致空 Body 请求 404

在用户或脚本通过 `curl` 发送 POST 执行指令请求时：
```bash
curl 'http://localhost:5270/api/Commands/0bd99077-13be-430c-a71f-45c003ce33bd/Execute' \
  -X 'POST' -H 'Content-Length: 0' ...
```
- **现象**：服务端直接响应 HTTP 404 `{"message":"API endpoint not found"}`。
- **原因**：在 Minimal API 映射中，`group_CommandsController.MapPost("{id:guid}/Execute", async (..., [FromBody] ExecuteCommandRequest? request) => ...)` 显式声明了 `[FromBody]` 委托参数。在 ASP.NET Core Minimal API 内部，若请求头未包含 `Content-Type: application/json` 或请求体为空（`Content-Length: 0`），终结点路由约束会认为该请求不满足契约而拒绝匹配，进而直接滑落到底部的 `app.MapFallback("/api/{*path}")` 全局兜底中间件，向客户端返回 404 Not Found。

---

## 3. 技术解决方案 (Solutions)

### 方案 1：SQLite 仓储层全面增加 `COLLATE NOCASE` 忽略大小写比对
为所有根据 Guid 或文本 ID 匹配的 SQL 语句增加 `COLLATE NOCASE` 排序规则，确保无论数据库存储是小写还是驱动传入大写均能准确命中：
1. `CommandStore.cs`：
   - `GetByIdAsync`: `WHERE Id = @Id COLLATE NOCASE`
   - `ExistsNameAsync`: `AND Id != @ExcludeId COLLATE NOCASE`
   - `UpdateAsync`: `WHERE Id = @Id COLLATE NOCASE`
   - `DeleteAsync`: `WHERE Id = @Id COLLATE NOCASE`
   - `CountByGroupAsync`: `WHERE GroupId = @GroupId COLLATE NOCASE`
   - `UpdateLastExecTimeAsync`: `WHERE Id = @Id COLLATE NOCASE`
2. `ScheduleStore.cs`：
   - `GetByIdAsync`: `WHERE Id = @Id COLLATE NOCASE LIMIT 1`
   - `UpdateAsync`: `WHERE Id = @Id COLLATE NOCASE`
   - `DeleteAsync`: `WHERE Id = @Id COLLATE NOCASE`
   - `CountByGroupAsync`: `WHERE GroupId = @GroupId COLLATE NOCASE`
   - `UpdateRunInfoAsync`: `WHERE Id = @Id COLLATE NOCASE`
3. `GroupStore.cs`、`ExecutionStore.cs`、`ApiKeyStore.cs`、`GatewayStore.cs`、`TranscodeJobStore.cs`、`TranscodePresetStore.cs`、`WatchRuleStore.cs`：
   - 相应主键与外键关联统一添加 `COLLATE NOCASE`。
4. `DbSetup.cs`：
   - 在建表 DDL 中，所有 `Id TEXT PRIMARY KEY` 与外键 Guid 字段声明 `COLLATE NOCASE`。

### 方案 2：前端终端会话自动容错自愈
在 `TerminalView.js` 的 `initTabSession` 中：
- 当通过历史 `runtime.sessionId` 请求详情返回 404（即后端会话已销毁或服务已重启）时；
- 自动清空 `runtime.sessionId` 与 `tab.sessionId`；
- 输出提示：`[历史会话已结束或服务已重启，正在自动创建新终端...]`；
- 自动降级为向 `POST /api/terminal/sessions` 发起请求创建新会话；
- 重连逻辑（`reconnectCurrent`）在检测到会话已退出或失效时，一并重置 `sessionId` 以支持快速重连。

### 方案 3：Shell 执行器启动失败信息透传
在 `ShellExecutor.cs` 中：
- 当进程启动失败（例如找不到 bash 或系统拒绝）时，将错误消息同步赋给 `ErrorOutput`；
- 保证历史执行记录和前端模态框能够看到具体的失败原因。

### 方案 4：执行历史与操作日志分页查询参数绑定容错
在 `RequestModels.cs`、`ExecutionStore.cs` 与 `OperationLogStore.cs` 中：
- 将 `ExecuteHistoryQuery` 与 `OperationLogQuery` 中的 `Page`、`PageSize` 设置为可空 `int?` 并提供默认回退（`Page ?? 1`, `PageSize ?? 20`）；
- 避免在 Minimal API 路由绑定时因未传 query string 导致抛出 500 异常（`Required parameter "int Page" was not provided`）。

### 方案 5：Minimal API 移除硬性 `[FromBody]` 约束，动态安全读取请求体
在 `EndpointsMapper.g.cs`、`CommandsController.cs` 以及前端 `CommandsView.js` 中：
1. `EndpointsMapper.g.cs`：移除委托参数上的 `[FromBody]`，改用 `ctx.Request.HasJsonContentType() && ctx.Request.ContentLength is not (null or 0)` 动态判断；若有合法的 JSON 请求体则调用 `ReadFromJsonAsync`，无请求体则传 `null`；
2. 同步适配 `SmbMounts` 与 `WebDavMounts` 的 `/Unmount` 接口；
3. `CommandsView.js`：调用 `execute` 时显式附带 `body: {}`；
4. 架构门禁增加 `LinuxArch017`，禁止任何可选 `[FromBody]` 直接出现在 Minimal API 路由参数签名中；集成测试增加 `Command_execute_supports_empty_body_and_json_body`。

---

## 4. 实施与验证清单

- [x] 归档技术方案至 [terminal-command-schedule-execution-fix-plan.md](file:///e:/WorkProject/CSharp/PrivateProject/Web/WebToolForLinux/docs/terminal-command-schedule-execution-fix-plan.md)
- [x] 更新 `CommandStore.cs`、`ScheduleStore.cs`、`GroupStore.cs`、`ExecutionStore.cs`、`ApiKeyStore.cs`、`GatewayStore.cs`、`FrpTunnelLineStore.cs`、`TranscodeJobStore.cs`、`TranscodePresetStore.cs`、`WatchRuleStore.cs` 的 SQL 查询大小写兼容（`COLLATE NOCASE`）
- [x] 更新 `DbSetup.cs` 表主键与外键定义
- [x] 更新 `ShellExecutor.cs` 启动失败错误信息透传
- [x] 更新 `TerminalView.js` 404 自愈新建终端逻辑及退出重连重置状态
- [x] 更新 `RequestModels.cs`、`ExecutionStore.cs`、`OperationLogStore.cs` 分页参数可空绑定
- [x] 修复 `EndpointsMapper.g.cs` 的 `Execute` 与 `Unmount` 空 Body 路由约束（支持空 Body、无 Content-Type、curl 调用与带有参数的 POST 请求）
- [x] 增加架构门禁 `LinuxArch017` 与 API 集成测试 `Command_execute_supports_empty_body_and_json_body`
- [x] 运行单元测试与集成测试验证：
  - `LinuxWebTool.ArchitectureTests`: **119 通过，0 失败**
  - `LinuxWebTool.IntegrationTests`: **54 通过，0 失败**
- [x] 运行期真实 API 验证：
  - 指令执行 `POST /api/commands/{id}/execute`（含用户原始 curl 命令测试）-> **HTTP 200 OK**，执行成功输出 `hello-linuxwebtool`
  - 定时任务 `POST /api/schedules/{id}/runNow` -> HTTP 200，触发成功并在后台完成
  - 定时任务自动调度 `ScheduledCommandJob` -> 每 5 分钟自动执行成功，耗时 ~45ms，不再报 404
  - 终端新建与获取 `POST /api/terminal/sessions` -> HTTP 200，会话建立成功
  - 终端断线自愈：当 `sessionId` 对应后台会话过期或重启被清理后，前端自动新建新终端，不再卡死报错
  - 执行历史 `GET /api/History` 与 `GET /api/Logs/Operations` 默认无参请求正常返回 200 数据列表

