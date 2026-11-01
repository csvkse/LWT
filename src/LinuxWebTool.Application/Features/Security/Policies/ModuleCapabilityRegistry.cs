using LinuxWebTool.Contracts.Features.Security.Contracts;

namespace LinuxWebTool.Application.Features.Security.Policies;

/// <summary>
/// 统一业务能力注册表：统一维护各模块子功能，同时映射 REST 路由与 MCP 工具
/// </summary>
public static class ModuleCapabilityRegistry
{
    private static readonly IReadOnlyList<ModuleCapabilityItem> AllCapabilities =
    [
        // 1. 远程主机运维 (RemoteHosts)
        new("remote_list_hosts", "查询远程受控主机清单与状态", "RemoteHosts", RiskLevel.ReadOnly, "GET /api/RemoteHosts", "remote_list_hosts", true, true),
        new("remote_test_connection", "远程主机网络连通与认证探测", "RemoteHosts", RiskLevel.ReadOnly, "POST /api/RemoteHosts/{id}/Test", "remote_test_connection", true, true),
        new("remote_list_directory", "远程 SFTP 目录浏览", "RemoteHosts", RiskLevel.ReadOnly, "GET /api/RemoteHosts/{id}/Files", "remote_list_directory", true, true),
        new("remote_read_file", "远程 SFTP 文件内容读取与下载", "RemoteHosts", RiskLevel.ReadOnly, "GET /api/RemoteHosts/{id}/Files/Content", "remote_read_file", true, true),
        new("remote_write_file", "远程 SFTP 文件写入与上传保存", "RemoteHosts", RiskLevel.Write, "PUT /api/RemoteHosts/{id}/Files/Content", "remote_write_file", true, true),
        new("remote_file_action", "远程 SFTP 文件新建/重命名/删除", "RemoteHosts", RiskLevel.Dangerous, "POST /api/RemoteHosts/{id}/Files/Action", "remote_file_action", true, false),
        new("remote_execute_command", "远程受控主机非交互 Shell 指令执行", "RemoteHosts", RiskLevel.Dangerous, "POST /api/RemoteHosts/{id}/Terminal/Exec", "remote_execute_command", true, true),
        new("remote_terminal_session", "建立远程主机交互式 SSH PTY 终端会话", "RemoteHosts", RiskLevel.Critical, "POST /api/RemoteHosts/{id}/Terminal/Session", "remote_terminal_session", true, false),

        // 2. 宿主文件管理 (Files)
        new("file_list_directory", "宿主文件系统目录浏览", "Files", RiskLevel.ReadOnly, "GET /api/Files/List", "file_list_directory", true, true),
        new("file_read", "宿主文本文件读取与二进制下载", "Files", RiskLevel.ReadOnly, "GET /api/Files/Content", "file_read", true, true),
        new("file_write", "宿主文本文件保存与新建", "Files", RiskLevel.Write, "PUT /api/Files/Content", "file_write", true, true),
        new("file_upload", "宿主文件上传", "Files", RiskLevel.Write, "POST /api/Files/Upload", "file_upload", true, false),
        new("file_action", "宿主文件删除、重命名与移动", "Files", RiskLevel.Dangerous, "POST /api/Files/Action", "file_action", true, false),

        // 3. 宿主终端与指令 (Terminal / Commands)
        new("command_list", "查阅系统已录入的预设指令列表", "Terminal", RiskLevel.ReadOnly, "GET /api/Commands", "command_list", true, true),
        new("command_run", "执行系统预设已审核指令", "Terminal", RiskLevel.Write, "POST /api/Commands/{id}/Execute", "command_run", true, true),
        new("terminal_execute_command", "执行动态输入的任意 Shell 命令行", "Terminal", RiskLevel.Dangerous, "POST /api/Commands/QuickExecute", "terminal_execute_command", true, true),
        new("terminal_create_session", "创建宿主交互式 PTY 伪终端会话", "Terminal", RiskLevel.Critical, "POST /api/Terminal/Session", "terminal_create_session", true, false),

        // 4. 定时调度与工作流 (Schedules)
        new("schedule_list", "查阅定时任务与工作流编排", "Schedules", RiskLevel.ReadOnly, "GET /api/Schedules", "schedule_list", true, true),
        new("schedule_run", "手动触发执行定时任务或工作流", "Schedules", RiskLevel.Write, "POST /api/Schedules/{id}/Run", "schedule_run", true, true),

        // 5. 媒体转码 (Transcode)
        new("transcode_list", "查阅媒体转码作业与预设", "Transcode", RiskLevel.ReadOnly, "GET /api/Transcode/Jobs", "transcode_list", true, true),
        new("transcode_submit", "提交媒体转码作业", "Transcode", RiskLevel.Write, "POST /api/Transcode/Jobs", "transcode_submit", true, true),

        // 6. 扩展服务与插件 (Services / Workers)
        new("service_list", "查阅扩展服务、守护进程与 Worker 状态", "Services", RiskLevel.ReadOnly, "GET /api/Services", "service_list", true, true),
        new("service_operate", "控制扩展服务与 Worker 启停与重载", "Services", RiskLevel.Dangerous, "POST /api/Services/{id}/Action", "service_operate", true, true)
    ];

    public static IReadOnlyList<ModuleCapabilityItem> GetAll() => AllCapabilities;

    public static IReadOnlyList<ModuleCapabilityItem> GetByModule(string module) =>
        AllCapabilities.Where(c => string.Equals(c.Module, module, StringComparison.OrdinalIgnoreCase)).ToList();

    public static ModuleCapabilityItem? FindByCapabilityId(string capabilityId) =>
        AllCapabilities.FirstOrDefault(c => string.Equals(c.CapabilityId, capabilityId, StringComparison.OrdinalIgnoreCase) ||
                                           string.Equals(c.McpToolName, capabilityId, StringComparison.OrdinalIgnoreCase));
}
