using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Contracts.Features.Workers.Contracts;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

public sealed partial class WorkersMcpToolProvider
{
    private async Task<McpToolExecutionResult> HandleWorkerGetCodeAsync(JsonElement args)
    {
        if (!args.TryGetProperty("worker_id", out var wProp) || string.IsNullOrWhiteSpace(wProp.GetString()))
            return McpToolExecutionResult.Error("缺少 worker_id 参数");

        var workerId = wProp.GetString()!.Trim();
        var file = args.TryGetProperty("file", out var fProp) ? fProp.GetString() : null;

        try
        {
            var codeDto = await workersManager.GetCodeAsync(workerId, file);
            var sb = new StringBuilder();
            sb.AppendLine($"=== Worker [{workerId}] 文件: {codeDto.SelectedFile} ===");
            sb.AppendLine($"可选文件列表: {string.Join(", ", codeDto.AvailableFiles)}");
            if (codeDto.Backups.Count > 0)
            {
                sb.AppendLine($"历史备忘录快照: {string.Join(", ", codeDto.Backups)}");
            }
            sb.AppendLine("--- 文件内容 ---");
            sb.AppendLine(codeDto.Content);
            return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Error($"获取 Worker [{workerId}] 代码失败: {ex.Message}");
        }
    }

    private async Task<McpToolExecutionResult> HandleWorkerEditCodeAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("worker_id", out var wProp) || string.IsNullOrWhiteSpace(wProp.GetString()))
            return McpToolExecutionResult.Error("缺少 worker_id 参数");
        if (!args.TryGetProperty("content", out var cProp))
            return McpToolExecutionResult.Error("缺少 content 参数");

        var workerId = wProp.GetString()!.Trim();
        var file = args.TryGetProperty("file", out var fProp) ? fProp.GetString() : null;
        var content = cProp.GetString() ?? "";
        var restart = !args.TryGetProperty("restart", out var rProp) || rProp.GetBoolean();

        try
        {
            var success = await workersManager.SaveCodeAsync(workerId, new WorkerCodeSaveRequest
            {
                FilePath = file,
                Content = content,
                RestartAfterSave = restart
            });

            await operationLogger.LogAsync(
                "MCP Worker代码编辑", "函数服务", workerId,
                $"文件: {file ?? "(默认入口)"}, 自动重启: {restart}, 成功: {success}",
                success: success, clientIp: context.ClientIp);

            return success
                ? McpToolExecutionResult.Success($"Worker '{workerId}' 文件 '{file ?? "(默认入口)"}' 已安全保存{(restart ? "，并已触发热重载" : "")}。已自动生成 .bak 备忘录快照。")
                : McpToolExecutionResult.Error($"Worker '{workerId}' 保存失败。");
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Error($"保存 Worker '{workerId}' 代码异常: {ex.Message}");
        }
    }

    private async Task<McpToolExecutionResult> HandleWorkerCreateAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("worker_id", out var wProp) || string.IsNullOrWhiteSpace(wProp.GetString()))
            return McpToolExecutionResult.Error("缺少 worker_id 参数");
        if (!args.TryGetProperty("name", out var nProp) || string.IsNullOrWhiteSpace(nProp.GetString()))
            return McpToolExecutionResult.Error("缺少 name 参数");
        if (!args.TryGetProperty("runtime", out var rProp) || string.IsNullOrWhiteSpace(rProp.GetString()))
            return McpToolExecutionResult.Error("缺少 runtime 参数");

        var workerId = wProp.GetString()!.Trim();
        var name = nProp.GetString()!.Trim();
        var runtimeStr = rProp.GetString()!.Trim();

        var runtime = runtimeStr.ToLowerInvariant() switch
        {
            "node" or "js" or "javascript" => WorkerRuntimeType.Node,
            "python" or "py" or "python3" => WorkerRuntimeType.Python,
            "dotnet" or "csharp" or "cs" => WorkerRuntimeType.DotNet,
            "bash" or "sh" => WorkerRuntimeType.Bash,
            "powershell" or "pwsh" => WorkerRuntimeType.PowerShell,
            "batch" or "bat" => WorkerRuntimeType.Batch,
            _ => WorkerRuntimeType.Python
        };

        var entrypoint = args.TryGetProperty("entrypoint", out var epProp) && !string.IsNullOrWhiteSpace(epProp.GetString())
            ? epProp.GetString()!.Trim()
            : runtime switch
            {
                WorkerRuntimeType.Node => "worker.js",
                WorkerRuntimeType.Python => "worker.py",
                WorkerRuntimeType.DotNet => "Program.cs",
                WorkerRuntimeType.Bash => "worker.sh",
                WorkerRuntimeType.PowerShell => "worker.ps1",
                WorkerRuntimeType.Batch => "worker.bat",
                _ => "worker.py"
            };

        var mode = args.TryGetProperty("mode", out var mProp) && string.Equals(mProp.GetString(), "daemon", StringComparison.OrdinalIgnoreCase)
            ? WorkerServiceMode.Daemon
            : WorkerServiceMode.Service;

        var scriptContent = args.TryGetProperty("script_content", out var sProp) ? sProp.GetString() : null;
        var description = args.TryGetProperty("description", out var dProp) ? dProp.GetString() : null;

        try
        {
            var created = await workersManager.CreateOrUpdateAsync(new WorkerCreateOrUpdateRequest
            {
                Id = workerId,
                Name = name,
                Description = description,
                Mode = mode,
                Runtime = runtime,
                Entrypoint = entrypoint,
                ScriptContent = scriptContent,
                AutoRestart = true
            });

            await operationLogger.LogAsync(
                "MCP Worker创建", "函数服务", workerId,
                $"名称: {name}, Runtime: {runtime}, 模式: {mode}",
                success: true, clientIp: context.ClientIp);

            return McpToolExecutionResult.Success($"Worker '{workerId}' ({name}) 创建成功！运行时: {runtime}, 入口文件: {entrypoint}, 状态: {created.State}");
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Error($"创建 Worker '{workerId}' 失败: {ex.Message}");
        }
    }

    private async Task<McpToolExecutionResult> HandleWorkerDebugRunAsync(JsonElement args)
    {
        if (!args.TryGetProperty("worker_id", out var wProp) || string.IsNullOrWhiteSpace(wProp.GetString()))
            return McpToolExecutionResult.Error("缺少 worker_id 参数");

        var workerId = wProp.GetString()!.Trim();
        var cmdArgs = args.TryGetProperty("args", out var aProp) ? aProp.GetString() : null;
        var timeout = args.TryGetProperty("timeout_seconds", out var tProp) && tProp.TryGetInt32(out var t) ? t : 8;

        var res = await workersManager.DebugRunAsync(workerId, new WorkerDebugRunRequest
        {
            Args = cmdArgs,
            TimeoutSeconds = timeout
        });

        var sb = new StringBuilder();
        sb.AppendLine($"=== Worker [{workerId}] 瞬态调试执行结果 ===");
        sb.AppendLine($"执行状态: {(res.Success ? "✅ 成功" : "❌ 失败")} | 退出码: {res.ExitCode} | 耗时: {res.ElapsedMilliseconds} ms");
        if (!string.IsNullOrWhiteSpace(res.StandardOutput))
        {
            sb.AppendLine("--- 标准输出 (stdout) ---");
            sb.AppendLine(res.StandardOutput.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(res.StandardError))
        {
            sb.AppendLine("--- 标准错误 (stderr) ---");
            sb.AppendLine(res.StandardError.TrimEnd());
        }

        return res.Success
            ? McpToolExecutionResult.Success(sb.ToString().TrimEnd())
            : McpToolExecutionResult.Error(sb.ToString().TrimEnd());
    }

    private async Task<McpToolExecutionResult> HandleWorkerQuickCreateAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("file_name", out var fProp) || string.IsNullOrWhiteSpace(fProp.GetString()))
            return McpToolExecutionResult.Error("缺少 file_name 参数");
        if (!args.TryGetProperty("code_content", out var cProp) || string.IsNullOrWhiteSpace(cProp.GetString()))
            return McpToolExecutionResult.Error("缺少 code_content 参数");

        var fileName = fProp.GetString()!.Trim();
        var codeContent = cProp.GetString()!;
        var autoStart = !args.TryGetProperty("auto_start", out var aProp) || aProp.GetBoolean();

        try
        {
            var worker = await workersManager.DeploySingleFileAsync(new WorkerQuickDeployRequest
            {
                FileName = fileName,
                Content = codeContent,
                AutoStart = autoStart
            });

            await operationLogger.LogAsync(
                "MCP Worker单文件极速部署", "函数服务", worker.Id,
                $"文件名: {fileName}, 名称: {worker.Name}, 运行时: {worker.Runtime}, 模式: {worker.Mode}, 状态: {worker.State}",
                success: true, clientIp: context.ClientIp);

            var sb = new StringBuilder();
            sb.AppendLine($"=== Worker [{worker.Id}] 单文件极速部署成功 ===");
            sb.AppendLine($"显示名称: {worker.Name}");
            sb.AppendLine($"运行时: {worker.Runtime} | 模式: {worker.Mode} | 入口: {worker.Entrypoint}");
            if (worker.BoundPort.HasValue) sb.AppendLine($"网关代理端口: {worker.BoundPort.Value}");
            sb.AppendLine($"当前运行状态: {worker.State}");
            sb.AppendLine("💡 提示：可通过 worker_invoke 测试 HTTP 响应，或通过 worker_get_logs 查看输出。");

            return McpToolExecutionResult.Success(sb.ToString().TrimEnd());
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Error($"极速部署 Worker 失败: {ex.Message}");
        }
    }

}
