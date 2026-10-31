using System.Text.Json;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

public sealed partial class WorkersMcpToolProvider
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static readonly JsonElement EmptyObjectSchema = Schema("""{"type":"object","properties":{}}""");

    private static readonly JsonElement WorkerControlSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker 唯一标识 ID" },
            "action": { "type": "string", "description": "控制动作：start | stop | restart", "enum": ["start", "stop", "restart"] }
        },
        "required": ["worker_id", "action"]
    }
    """);

    private static readonly JsonElement WorkerInvokeSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker 唯一标识 ID" },
            "sub_path": { "type": "string", "description": "请求子路径，如 api/v1/data，默认为根路径" },
            "method": { "type": "string", "description": "HTTP 请求方法，如 GET, POST, PUT, DELETE（默认 GET）" },
            "body": { "type": "string", "description": "请求体 JSON 或文本内容（可选）" }
        },
        "required": ["worker_id"]
    }
    """);

    private static readonly JsonElement WorkerGetLogsSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker 唯一标识 ID" },
            "lines": { "type": "integer", "description": "读取最新日志行数（默认 50，最大 500）" }
        },
        "required": ["worker_id"]
    }
    """);

    private static readonly JsonElement WorkerImportSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "repo_url": { "type": "string", "description": "Git 仓库克隆地址" },
            "module_ids": {
                "type": "array",
                "items": { "type": "string" },
                "description": "待导入的 Worker 模块 ID 列表"
            },
            "overwrite": { "type": "boolean", "description": "若已存在是否覆盖（默认 true）" }
        },
        "required": ["repo_url", "module_ids"]
    }
    """);

    private static readonly JsonElement WorkerSetEnabledSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker 唯一标识 ID" },
            "enabled": { "type": "boolean", "description": "true 为启用，false 为禁用（禁用时停止进程并暂停守护）" }
        },
        "required": ["worker_id", "enabled"]
    }
    """);

    private static readonly JsonElement WorkerCheckUpdateSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker ID（可选，为空则检查全部）" }
        }
    }
    """);

    private static readonly JsonElement WorkerHotUpdateSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker ID" },
            "force": { "type": "boolean", "description": "是否强制覆盖（默认 false）" }
        },
        "required": ["worker_id"]
    }
    """);

    private static readonly JsonElement WorkerGetCodeSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker 唯一标识 ID" },
            "file": { "type": "string", "description": "目标文件相对路径（如 worker.js, worker.json，默认为入口脚本）" }
        },
        "required": ["worker_id"]
    }
    """);

    private static readonly JsonElement WorkerEditCodeSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker 唯一标识 ID" },
            "file": { "type": "string", "description": "目标文件相对路径（如 worker.js, worker.json）" },
            "content": { "type": "string", "description": "待写入的新代码或配置文本" },
            "restart": { "type": "boolean", "description": "保存后是否自动热重启 Worker（默认 true）" }
        },
        "required": ["worker_id", "content"]
    }
    """);

    
    private static readonly JsonElement WorkerQuickCreateSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "file_name": { "type": "string", "description": "单文件名（含后缀，如 speedtest.js, monitor.py, backup.sh）" },
            "code_content": { "type": "string", "description": "完整脚本代码文本。支持在顶部头部注释中自省声明元数据：\n/**\n * @name 节点测速\n * @description 实时探测上下行网速\n * @runtime node\n * @mode service\n * @port 8920\n * @target container\n */" },
            "auto_start": { "type": "boolean", "description": "创建后是否自动启动（默认 true）" }
        },
        "required": ["file_name", "code_content"]
    }
    """);

    private static readonly JsonElement WorkerCreateSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker 唯一标识 ID" },
            "name": { "type": "string", "description": "Worker 显示名称" },
            "runtime": { "type": "string", "description": "运行时环境：node | python | dotnet | bash | powershell | batch", "enum": ["node", "python", "dotnet", "bash", "powershell", "batch"] },
            "entrypoint": { "type": "string", "description": "入口执行文件名，如 worker.js, worker.py" },
            "script_content": { "type": "string", "description": "初始代码脚本内容" },
            "description": { "type": "string", "description": "Worker 描述信息" },
            "mode": { "type": "string", "description": "服务模式：service (HTTP 微服务) | daemon (常驻后台脚本)", "enum": ["service", "daemon"] }
        },
        "required": ["worker_id", "name", "runtime"]
    }
    """);

    private static readonly JsonElement WorkerDebugRunSchema = Schema("""
    {
        "type": "object",
        "properties": {
            "worker_id": { "type": "string", "description": "Worker 唯一标识 ID" },
            "args": { "type": "string", "description": "附加执行参数命令行字符串" },
            "timeout_seconds": { "type": "integer", "description": "超时秒数（默认 8 秒，最大 30 秒）" }
        },
        "required": ["worker_id"]
    }
    """);
}
