using LinuxWebTool.Contracts.Features.Mcp.Contracts;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine;

/// <summary>
/// MCP 二级树状目录注册中心（组合模式 Composite Pattern）
/// 统一管理一级领域分类与二级叶子工具元数据，支持前端交互勾选与权限矩阵判定
/// </summary>
public static class McpCatalogRegistry
{
    private static readonly List<McpCategoryCatalog> Categories = new()
    {
        new McpCategoryCatalog(
            "commands",
            "指令管理 (Commands)",
            "预定义 Linux 指令模版管理、即时执行与批量调度",
            new List<McpToolCatalogItem>
            {
                new("command_list", "查询系统中已配置的指令模版列表", "Read", "commands"),
                new("command_get", "根据 ID 查询指令模版详细内容与配置", "Read", "commands"),
                new("command_run", "执行指定的指令模版（支持覆盖目标主机与动态参数）", "Execute", "commands"),
                new("command_batch_run", "批量执行一组指令（支持串行/并行模式与熔断策略）", "Execute", "commands"),
                new("command_create", "创建新的 Linux 指令模版", "Dangerous", "commands"),
                new("command_delete", "删除指定的 Linux 指令模版", "Dangerous", "commands")
            },
            ModuleCode: "terminal",
            SupportsApi: true,
            SupportsMcp: true),

        new McpCategoryCatalog(
            "workflows",
            "工作流编排 (Workflows)",
            "多阶段与多活动流水线编排、长时执行与状态回溯",
            new List<McpToolCatalogItem>
            {
                new("workflow_list", "查询系统中已配置的工作流流水线列表", "Read", "workflows"),
                new("workflow_get", "查询指定工作流的完整阶段与步骤定义", "Read", "workflows"),
                new("workflow_run", "触发执行指定的工作流流水线（支持覆盖目标主机）", "Execute", "workflows"),
                new("workflow_get_batch", "根据批次号查询工作流执行详情、各步骤耗时与日志", "Read", "workflows"),
                new("workflow_toggle", "启用或暂停指定的工作流流水线", "Execute", "workflows")
            },
            ModuleCode: "schedules",
            SupportsApi: true,
            SupportsMcp: true),

        new McpCategoryCatalog(
            "schedules",
            "计划调度 (Schedules)",
            "Cron 表达式定时任务调度、启停与即时触发",
            new List<McpToolCatalogItem>
            {
                new("schedule_list", "查询系统中配置的定时调度任务列表", "Read", "schedules"),
                new("schedule_get", "获取指定定时任务的详细配置与绑定目标", "Read", "schedules"),
                new("schedule_trigger_now", "立即触发一次指定的定时任务执行", "Execute", "schedules"),
                new("schedule_toggle", "启用或暂停指定的定时任务", "Execute", "schedules"),
                new("schedule_create", "创建新的定时调度任务（支持绑定指令或工作流）", "Dangerous", "schedules"),
                new("schedule_delete", "删除指定的定时调度任务", "Dangerous", "schedules")
            },
            ModuleCode: "schedules",
            SupportsApi: true,
            SupportsMcp: true),

        new McpCategoryCatalog(
            "terminal",
            "终端与命令行 (Terminal)",
            "Linux 原生 Shell 命令行执行与交互式 PTY 会话",
            new List<McpToolCatalogItem>
            {
                new("terminal_execute_command", "执行 Linux Shell 命令行或 Bash 脚本，获取输出及耗时", "Dangerous", "terminal"),
                new("terminal_list_sessions", "列出当前活跃的交互式 PTY 虚拟终端会话", "Read", "terminal"),
                new("terminal_create_session", "创建新的 PTY 虚拟终端交互会话", "Execute", "terminal"),
                new("terminal_close_session", "强制终止指定的 PTY 虚拟终端会话", "Dangerous", "terminal")
            },
            ModuleCode: "terminal",
            SupportsApi: false,
            SupportsMcp: true),

        new McpCategoryCatalog(
            "remote_hosts",
            "远程主机 (Remote Hosts)",
            "SSH/SFTP 远端节点管理、连接探活与远端操作",
            new List<McpToolCatalogItem>
            {
                new("remote_list_hosts", "查询已纳管的远程受控主机列表（自动根据 API Key 授权白名单进行范围投影）", "Read", "remote_hosts"),
                new("remote_test_connection", "对指定远程受控主机发起网络连通与认证探测，返回往返延迟(ms)", "Execute", "remote_hosts"),
                new("remote_execute_command", "在指定远程受控主机上执行非交互式 Shell 指令，获取 stdout/stderr、退出码与执行耗时", "Dangerous", "remote_hosts"),
                new("remote_list_directory", "浏览指定远程受控主机的 SFTP 目录，列出文件与子目录的名称、大小、权限与更新时间", "Read", "remote_hosts"),
                new("remote_read_file", "读取指定远程受控主机上的文本文件内容（单次读取上限 2MB）", "Read", "remote_hosts"),
                new("remote_write_file", "向指定远程受控主机写入或覆盖保存文本文件内容", "Dangerous", "remote_hosts")
            },
            ModuleCode: "remote_hosts",
            SupportsApi: true,
            SupportsMcp: true),

        new McpCategoryCatalog(
            "files",
            "文件系统 (Files)",
            "宿主机本地文件浏览、读取、写入与目录管理",
            new List<McpToolCatalogItem>
            {
                new("file_list_directory", "获取指定绝对路径下的文件与子目录列表", "Read", "files"),
                new("file_read_text", "读取指定文本文件的内容（保护区核心数据库文件拒绝读取）", "Read", "files"),
                new("file_write_text", "保存或覆写文本文件内容（保护区核心数据库文件拒绝写入）", "Dangerous", "files"),
                new("file_create_directory", "在指定路径创建多级目录", "Execute", "files"),
                new("file_delete", "删除文件或空目录（根目录受保护禁止删除）", "Dangerous", "files"),
                new("file_get_storage_status", "查询磁盘容量、使用率及 SMB/WebDAV/Rclone 存储挂载健康状态", "Read", "files")
            },
            ModuleCode: "files",
            SupportsApi: true,
            SupportsMcp: true),

        new McpCategoryCatalog(
            "transcode",
            "媒体转码 (Transcode)",
            "FFmpeg 视频转码队列、预设查询与任务管理",
            new List<McpToolCatalogItem>
            {
                new("transcode_get_capabilities", "探查系统 FFmpeg 版本及显卡硬件加速（VAAPI/NVENC/QSV）支持状态", "Read", "transcode"),
                new("transcode_list_presets", "查询已配置的音视频转码规格预设列表", "Read", "transcode"),
                new("transcode_list_jobs", "分页查询转码排队队列与历史任务执行状态", "Read", "transcode"),
                new("transcode_submit_job", "提交单个媒体文件转码任务至异步处理队列", "Execute", "transcode"),
                new("transcode_cancel_job", "取消正在排队或执行中的转码任务", "Dangerous", "transcode")
            },
            ModuleCode: "transcode",
            SupportsApi: true,
            SupportsMcp: true),

        new McpCategoryCatalog(
            "gateway",
            "反向代理网关 (Gateway)",
            "反向代理站点查询、启停与流量管控",
            new List<McpToolCatalogItem>
            {
                new("gateway_list_routes", "查询家庭网关中已配置的 L7 HTTP 反向代理路由与网站代理", "Read", "gateway")
            },
            ModuleCode: "gateway",
            SupportsApi: true,
            SupportsMcp: true)
    };

    public static List<McpCategoryCatalog> GetCatalog() => Categories;
}
