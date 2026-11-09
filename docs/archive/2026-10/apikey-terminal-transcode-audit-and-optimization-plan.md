# ApiKey 授权、终端页面与转码流程全链路审计与优化实施方案

## 1. 方案背景与目标

针对 LinuxWebTool 系统的三大核心模块——**ApiKey 授权与权限矩阵**、**终端页面与后台 PTY 会话**、以及 **FFmpeg 媒体转码与文件夹监听**，进行了深度全流程代码走查与并发性能压力分析。

本方案旨在解决走查中发现的**死循环重复转码**、**首轮基线误触发**、**ApiKey 后缀校验穿透**、**终端正常退出误弹错误**以及**高频写库造成的 SQLite 锁争用**等关键逻辑与性能瓶颈。

---

## 2. 审计问题清单与整改设计

### 2.1 P0 级严重逻辑缺陷与安全漏洞

1. **转码流程：`WatchFolderService` 轮询模式成功入队后未更新快照，导致死循环重复入队**
   * **位置**：`src/LinuxWebTool.Infrastructure/Transcode/WatchFolderService.cs:211-221`
   * **问题**：在并存模式（Coexist）下，源文件不被删除。当首次发现文件并成功入队后，代码直接 `continue`，导致 `state.Snapshot` 中从未记录该文件。任务一旦转码成功结束，`activePaths` 不再包含该文件，下一轮扫描再次将其作为“新文件”入队，形成无限生成 `video-1.mp4`、`video-2.mp4`……的死循环。
   * **整改**：入队成功后必须立即记录 `state.Snapshot[path] = (size, lwt ?? DateTime.MinValue, null)`，并记录已处理签名，后续仅在文件发生真实大小或修改时间变化时才允许重新入队。

2. **转码流程：`WatchFolderService` 未兑现“首轮扫描仅建立基线、不触发历史文件”的设计**
   * **位置**：`src/LinuxWebTool.Infrastructure/Transcode/WatchFolderService.cs:189-245`
   * **问题**：代码注释承诺“首轮扫描仅建立基线、不触发历史文件”，并设置了 `state.LastScan = DateTime.MinValue`，但在 `ScanPollingOnceAsync` 中未对 `LastScan == DateTime.MinValue` 增加基线识别。新增监控目录时，若包含上千个存量视频，会在第一轮扫描时全量并发推入队列。
   * **整改**：当 `state.LastScan == DateTime.MinValue` 时，只将现有文件记入 `state.Snapshot` 作为基线快照，不触发入队操作；从第二轮扫描开始才检测新增与变更。

3. **ApiKey 鉴权：`ApiKeyMiddleware.IsPublicRoute` 静态后缀检查误伤 API 路由**
   * **位置**：`src/LinuxWebTool.WebHost/Middleware/ApiKeyMiddleware.cs:220-226`
   * **问题**：静态扩展名检查（`.js`, `.html`, `.png` 等）未排除 `/api` 和 `/mcp` 路径。若客户端请求带文件后缀的 API 接口（如文件管理器内容预览接口 `/api/Files/content/test.js`），中间件会将其误判为公开静态文件放行，导致跳过 ApiKey 模块权限检查或因缺失 `context.User` 引发 401。
   * **整改**：将静态文件扩展名检查严格限定在 `!path.StartsWith("/api", ...) && !path.StartsWith("/mcp", ...)`。

---

### 2.2 P1 级体验与并发性能优化

1. **终端页面：Shell 正常 `exit` 被误判为连接断开并死循环重试弹错**
   * **位置**：`src/LinuxWebTool.WebHost/wwwroot/app/views/TerminalView.js:372-382`
   * **问题**：用户键入 `exit` 退出 Shell 后，WebSocket 关闭。前端 `onclose` 盲目触发 `reconnectTimer` 重连该会话，查询返回 `state: 'Exited'` 时抛出异常弹窗：“终端初始化失败: 会话已退出，退出码：0”。
   * **整改**：在 `onclose` 中识别退出状态，正常退出（或退出码为 0）不再触发重连，也不弹出错误 Toast。

2. **终端页面：`ResizeObserver` 缺少防抖导致高频 resize 轰炸后端**
   * **位置**：`src/LinuxWebTool.WebHost/wwwroot/app/views/TerminalView.js:408-419`
   * **问题**：调整窗口或全屏切换时每秒触发数十次，频繁调用 `fit()` 并向 WebSocket 发送 resize 报文，造成伪终端窗口尺寸反复抖动。
   * **整改**：添加 80ms 防抖函数。

3. **ApiKey 鉴权：`TouchLastUsed` 无节制高频异步写库**
   * **位置**：`src/LinuxWebTool.Infrastructure/Security/ApiKeyService.cs:91-104`
   * **问题**：每个携带 ApiKey 的请求都触发一次后台 `UPDATE api_key SET LastUsedAt`，高频 API 或 MCP 请求下会引发 SQLite 并发锁盘争用。
   * **整改**：引入内存时间戳节流缓存，同一 Key 距离上次写库不足 1 分钟时跳过 DB 更新。

---

### 2.3 P2 级健壮性与数据库开销优化

1. **转码流程：`ConsumeLogAsync` 关联 CancellationToken 避免 Disposed 异常**
   * **位置**：`src/LinuxWebTool.Infrastructure/Transcode/TranscodeQueueService.cs:513-528`
   * **问题**：任务取消中断时 `logStream` 被释放，但 `ConsumeLogAsync` 仍在后台尝试读取并向已释放的文件流写入。
   * **整改**：为 `ConsumeLogAsync` 引入 CancellationToken 并安全处理取消。

2. **转码流程：进度回写轻量化 SQL**
   * **位置**：`src/LinuxWebTool.Infrastructure/Persistence/TranscodeJobStore.cs` & `TranscodeQueueService.cs:500`
   * **问题**：每 2 秒一次的进度回写每次都全量更新 30 个字段。
   * **整改**：在 `TranscodeJobStore` 增加 `UpdateProgressAsync(id, progress, speed)` 仅更新 3 个字段。

---

## 3. 执行步骤

1. **Step 1**: 修改 `WatchFolderService.cs`，实现首轮扫描基线跳过与入队后快照持久化，根治无限重复转码。
2. **Step 2**: 修改 `ApiKeyMiddleware.cs` 与 `ApiKeyService.cs`，修复静态扩展名误判，添加 `TouchLastUsed` 1 分钟内存节流。
3. **Step 3**: 修改 `TranscodeQueueService.cs` 与 `TranscodeJobStore.cs`，修复日志流取消释放问题，并实现轻量进度回写。
4. **Step 4**: 修改 `TerminalView.js`，优化正常退出逻辑与 `ResizeObserver` 防抖。
5. **Step 5**: 运行全量单元与集成测试（`dotnet test`），确保所有测试用例 100% 通过。
