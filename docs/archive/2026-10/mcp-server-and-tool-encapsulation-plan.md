# MCP 服务与核心功能工具封装技术调研与实施方案

## 1. 目标与背景

为了让大语言模型（如 Claude Desktop、Cursor、Windsurf、OpenWebUI 及各类自主 Agent）能够直接理解并操作 Linux 服务器，`LinuxWebTool` 需要基于标准 **MCP (Model Context Protocol)** 协议对外暴露安全受控的工具集。

本方案针对以下四大核心业务模块进行工具化深度封装：
1. **终端 (Terminal & Commands)**：非交互式指令安全执行、交互式 PTY 会话探查与调度。
2. **定时任务 (Schedules)**：定时任务列表检索、动态增删改查、即时触发执行。
3. **文件管理 (File Manager & Mounts)**：文件与目录遍历、文本安全读写、文件系统管理与挂载点健康度感知。
4. **媒体转码 (Media Transcode)**：FFmpeg 硬件编解码探针、转码预设查询、异步任务入队与状态跟踪。

---

## 2. MCP 协议与通信通道调研

参考原型：`E:\WorkProject\C#\【个人项目】\MCP\McpSseSkill`。

### 2.1 传输通道与端点规范

MCP 官方标准定义了基于 JSON-RPC 2.0 的传输协议，主要支持两类 Web 传输通道：
1. **SSE (Server-Sent Events) 通道**：
   - 握手端点：`GET /mcp/sse`
   - 服务端返回长连接事件流：`event: endpoint\ndata: /mcp/message?sessionId={guid}\n\n`
   - 消息端点：`POST /mcp/message?sessionId={guid}`，承载客户端发来的 JSON-RPC 请求。
2. **Streamable HTTP 通道**：
   - 统一端点：`POST /mcp`（单端点处理双向流式或轮询交互）。

在 `LinuxWebTool` 中，推荐同时开放 **SSE 传输**与 **HTTP 传输**，以最大化兼容各类 AI 客户端生态。

### 2.2 Native AOT 兼容性分析与技术选型

#### 实验验证结果
经对 `ModelContextProtocol.AspNetCore` (0.2.0-preview.1) 进行 Native AOT 实测编译（`dotnet publish -p:PublishAot=true`）：
- **构建结果**：成功产出原生二进制，无致命构建错误。
- **潜在风险**：原库在动态反射绑定工具方法及非强类型 JSON 序列化时触发了 `IL2026` / `IL3050` 裁剪告警。

#### 落地双轨架构保障策略

- **主选路线（官方 SDK + 强类型 AOT 强化）**：
  - 引入 `ModelContextProtocol.AspNetCore`；
  - 所有的 Tool 入参类（Arguments）与返回值对象全部声明为显式强类型 DTO，严禁使用匿名对象（Anonymous Types）；
  - 将所有 MCP 涉及的数据模型完整注册至 `AppJsonSerializerContext`；
  - 在 `rd.xml` 或项目属性中补充 MCP 元数据保护描述，确保零 AOT 裁剪异常。
- **备选保障（轻量原生 Native AOT JSON-RPC 派发器）**：
  - 若官方预览版 SDK 在 Linux AOT 生产环境下存在隐式反射回退，直接利用现有的 `EndpointsMapper.g.cs` 实现一套仅 200 行的零依赖轻量 JSON-RPC 派发器（处理 `initialize`, `tools/list`, `tools/call`），确保 100% 绝对稳定。

---

## 3. 四大核心模块工具封装详表 (Tool Specifications)

### 3.1 终端模块 (Terminal & Shell)

| MCP 工具名称 | 描述 | 入参规范 (Schema) | 返回值说明 |
|---|---|---|---|
| `terminal_execute_command` | 执行单条 Linux Shell 指令并捕获完整输出与执行耗时 | `command` (string, 必填): 待执行指令<br>`working_directory` (string, 可选): 执行目录<br>`timeout_seconds` (int, 可选, 默认 60, 最大 300) | JSON: `{ exit_code, stdout, stderr, duration_ms, is_timed_out, is_truncated }` |
| `terminal_list_sessions` | 列出当前所有活动的后台 PTY 终端会话 | 无入参 | JSON: 会话数组 `[ { session_id, process_id, title, working_dir, is_alive, created_at } ]` |
| `terminal_create_session` | 新建一个后台交互式 PTY 虚拟终端会话 | `executable` (string, 可选, 默认 bash/sh)<br>`working_directory` (string, 可选)<br>`cols` (int, 默认 80)<br>`rows` (int, 默认 24) | JSON: `{ session_id, process_id, working_directory }` |
| `terminal_close_session` | 强制终止并关闭指定的交互式终端会话 | `session_id` (string, 必填) | JSON: `{ success, message }` |

### 3.2 定时任务模块 (Schedules)

| MCP 工具名称 | 描述 | 入参规范 (Schema) | 返回值说明 |
|---|---|---|---|
| `schedule_list` | 查询已配置的定时调度任务列表 | `enabled_only` (bool, 可选, 默认 false)<br>`group_id` (string, 可选) | JSON: 任务数组 `[ { id, name, command_name, command_text, cron_expression, enabled, next_run_time, last_run_time } ]` |
| `schedule_get_detail` | 获取指定定时任务的配置与最近执行记录 | `schedule_id` (string, 必填) | JSON: `{ task_info, recent_executions: [...] }` |
| `schedule_create` | 创建一条新的定时调度任务 | `name` (string, 必填)<br>`command_text` (string, 必填): 待调度的脚本/指令<br>`cron_expression` (string, 必填): 5/6位 Cron 表达式<br>`arguments` (string, 可选)<br>`timeout_seconds` (int, 默认 300)<br>`enabled` (bool, 默认 true) | JSON: `{ id, name, cron_expression, next_run_time }` |
| `schedule_toggle` | 快速启用或暂停指定的定时任务 | `schedule_id` (string, 必填)<br>`enabled` (bool, 必填) | JSON: `{ success, enabled, next_run_time }` |
| `schedule_trigger_now` | 立即触发一次定时任务的异步执行 | `schedule_id` (string, 必填) | JSON: `{ success, message, triggered_time }` |
| `schedule_delete` | 删除指定的定时任务及 Quartz 内存调度 | `schedule_id` (string, 必填) | JSON: `{ success, message }` |

### 3.3 文件管理模块 (Files & Mounts)

| MCP 工具名称 | 描述 | 入参规范 (Schema) | 返回值说明 |
|---|---|---|---|
| `file_list_directory` | 浏览并列出指定绝对路径下的子目录与文件信息 | `path` (string, 必填): 目标目录绝对路径 | JSON: `{ current_path, is_root, entries: [ { name, path, is_dir, size_bytes, modified_time, extension } ] }` |
| `file_read_text` | 安全读取文本文件内容（内置保护机制与截断防护） | `path` (string, 必填): 文本文件路径<br>`max_bytes` (int, 可选, 默认 512KB, 最大 2MB) | JSON: `{ path, name, size_bytes, content, is_truncated }` |
| `file_write_text` | 创建新文本文件或覆盖已有文件 | `path` (string, 必填)<br>`content` (string, 必填)<br>`overwrite` (bool, 可选, 默认 true) | JSON: `{ success, path, written_bytes }` |
| `file_create_directory`| 递归创建目录（支持多级目录） | `path` (string, 必填) | JSON: `{ success, path }` |
| `file_rename_or_move` | 重命名文件/目录或在同文件系统间移动路径 | `from_path` (string, 必填)<br>`to_path` (string, 必填) | JSON: `{ success, new_path }` |
| `file_delete` | 删除指定的文件或目录（目录非空需显式确认） | `path` (string, 必填)<br>`recursive` (bool, 可选, 默认 false) | JSON: `{ success, message }` |
| `file_get_storage_status` | 获取系统磁盘容量与 SMB / WebDAV / Rclone 挂载健康状态 | 无入参 | JSON: `{ disks: [...], mounts: [ { name, type, local_path, remote, health_status } ] }` |

### 3.4 媒体转码模块 (Media Transcode)

| MCP 工具名称 | 描述 | 入参规范 (Schema) | 返回值说明 |
|---|---|---|---|
| `transcode_get_capabilities`| 检测系统 FFmpeg 可用性及显卡硬件加速支持 | 无入参 | JSON: `{ is_available, ffmpeg_version, hardware_devices: [ { name, type, is_supported } ] }` |
| `transcode_list_presets` | 获取已配置的转码规格预设（如 1080p, HEVC 等） | 无入参 | JSON: 预设数组 `[ { id, name, target_format, is_builtin, description } ]` |
| `transcode_list_jobs` | 分页查询转码任务队列与实时进度 | `page` (int, 默认 1)<br>`page_size` (int, 默认 10)<br>`status` (string, 可选: Pending, Running, Success, Failed, Canceled) | JSON: `{ total, items: [ { id, source_path, output_path, preset_name, status, progress, speed, error } ] }` |
| `transcode_submit_job` | 提交单个音视频转码任务至异步处理队列 | `source_path` (string, 必填)<br>`preset_id` (string, 可选)<br>`custom_args` (string, 可选)<br>`output_dir` (string, 可选)<br>`use_hardware_accel` (bool, 默认 true) | JSON: `{ success, job_id, message }` |
| `transcode_cancel_job` | 取消正在排队或执行中的转码任务 | `job_id` (string, 必填) | JSON: `{ success, message }` |

---

## 4. 安全防护与沙箱防御准则 (Guardrails)

AI Agent 操作具备极高的自动化能力，因此在暴露 MCP 工具时必须植入不可逾越的防御底线：

1. **核心数据保护区阻断**：
   `file_write_text`、`file_delete`、`file_rename_or_move` 坚决拒绝操作程序核心数据目录（如 `data/linuxweb.db`、`admin.json`、JWT 私钥目录），违规调用直接阻断并记录审计日志。
2. **终端防挂死超时守卫**：
   所有非交互式指令强制附加 `CancellationToken`，单条执行默认最长 60 秒，绝对上限 300 秒，超时即发送 `SIGTERM` / `SIGKILL` 强制回收。
3. **输出体积防御与 OOM 防护**：
   单次文本读取和指令输出严格截断在 2MB 以内，超出部分返回 `is_truncated = true` 并附带截断声明，杜绝超大文件造成 LLM 上下文爆炸或客户端堆内存崩溃。
4. **系统关键根目录防误删保护**：
   文件删除工具严格拦截针对系统根目录（`/`、`/etc`、`/bin`、`/usr`）的递归删除请求。

---

## 5. 客户端集成指南与配置示例

### 5.1 Cursor / Windsurf 集成 (`mcp.json`)

```json
{
  "mcpServers": {
    "linux-web-tool": {
      "url": "http://127.0.0.1:8080/mcp/sse",
      "headers": {
        "X-Api-Key": "lwt_live_8f3a9e72bc140d5a..."
      }
    }
  }
}
```

### 5.2 Claude Desktop 集成 (`claude_desktop_config.json`)

配合 MCP 官方提供的 `mcp-remote` 转接器：

```json
{
  "mcpServers": {
    "linux-server": {
      "command": "npx",
      "args": [
        "-y",
        "@modelcontextprotocol/server-sse",
        "--url",
        "http://127.0.0.1:8080/mcp/sse",
        "--header",
        "X-Api-Key=lwt_live_8f3a9e72bc140d5a..."
      ]
    }
  }
}
```

### 5.3 搭配 ProxyByCF FRP 穿透后的外网全托管配置

当配合 FRP 反向穿透时，AI Agent 可以在世界的任何角落安全连接家庭或机房中的 Linux 主机：

```json
{
  "mcpServers": {
    "home-nas": {
      "url": "https://frp.example.com/tunnel/nas-tool/mcp/sse",
      "headers": {
        "X-Api-Key": "lwt_live_8f3a9e72bc140d5a..."
      }
    }
  }
}
```
