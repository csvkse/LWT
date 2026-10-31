using System.Text;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Mcp.Contracts;
using LinuxWebTool.Contracts.Features.Services.Contracts;

namespace LinuxWebTool.WebHost.Features.Mcp.Engine.Providers;

public sealed partial class ServicesMcpToolProvider
{
    private async Task<McpToolExecutionResult> HandleExtensionGetCodeAsync(JsonElement args)
    {
        if (!args.TryGetProperty("extension_id", out var eProp) || string.IsNullOrWhiteSpace(eProp.GetString()))
            return McpToolExecutionResult.Error("缺少 extension_id 参数");

        var extensionId = eProp.GetString()!.Trim();
        var file = args.TryGetProperty("file", out var fProp) ? fProp.GetString() : null;

        try
        {
            var codeDto = await extensionManager.GetCodeAsync(extensionId, file);
            var sb = new StringBuilder();
            sb.AppendLine($"=== 运维扩展 [{extensionId}] 文件: {codeDto.SelectedFile} ===");
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
            return McpToolExecutionResult.Error($"获取扩展 [{extensionId}] 代码失败: {ex.Message}");
        }
    }

    private async Task<McpToolExecutionResult> HandleExtensionEditCodeAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("extension_id", out var eProp) || string.IsNullOrWhiteSpace(eProp.GetString()))
            return McpToolExecutionResult.Error("缺少 extension_id 参数");
        if (!args.TryGetProperty("content", out var cProp))
            return McpToolExecutionResult.Error("缺少 content 参数");

        var extensionId = eProp.GetString()!.Trim();
        var file = args.TryGetProperty("file", out var fProp) ? fProp.GetString() : null;
        var content = cProp.GetString() ?? "";

        try
        {
            var success = await extensionManager.SaveCodeAsync(extensionId, new ExtensionCodeSaveRequest
            {
                FilePath = file,
                Content = content
            });

            await operationLogger.LogAsync(
                "MCP 扩展代码编辑", "服务扩展", extensionId,
                $"文件: {file ?? "(默认入口)"}, 成功: {success}",
                success: success, clientIp: context.ClientIp);

            return success
                ? McpToolExecutionResult.Success($"扩展 '{extensionId}' 文件 '{file ?? "(默认)"}' 已安全保存。已自动生成 .bak 备忘录快照。")
                : McpToolExecutionResult.Error($"扩展 '{extensionId}' 保存失败。");
        }
        catch (Exception ex)
        {
            return McpToolExecutionResult.Error($"保存扩展 '{extensionId}' 代码异常: {ex.Message}");
        }
    }

    private async Task<McpToolExecutionResult> HandleExtensionCreateAsync(JsonElement args, McpRequestContext context)
    {
        if (!args.TryGetProperty("extension_id", out var eProp) || string.IsNullOrWhiteSpace(eProp.GetString()))
            return McpToolExecutionResult.Error("缺少 extension_id 参数");
        if (!args.TryGetProperty("name", out var nProp) || string.IsNullOrWhiteSpace(nProp.GetString()))
            return McpToolExecutionResult.Error("缺少 name 参数");

        var extensionId = eProp.GetString()!.Trim();
        var name = nProp.GetString()!.Trim();
        var description = args.TryGetProperty("description", out var dProp) ? dProp.GetString() : null;
        var runner = args.TryGetProperty("runner", out var rProp) && !string.IsNullOrWhiteSpace(rProp.GetString())
            ? rProp.GetString()!.Trim()
            : "bash";
        var startScript = args.TryGetProperty("start_script", out var sProp) ? sProp.GetString() : null;

        var result = await extensionManager.CreateExtensionAsync(new ExtensionCreateRequest
        {
            Id = extensionId,
            Name = name,
            Description = description,
            Runner = runner,
            StartScript = startScript
        });

        await operationLogger.LogAsync(
            "MCP 扩展创建", "服务扩展", extensionId,
            $"名称: {name}, Runner: {runner}, 结果: {result.Success}",
            success: result.Success, clientIp: context.ClientIp);

        return result.Success
            ? McpToolExecutionResult.Success($"运维扩展 '{extensionId}' ({name}) 创建成功！解释器: {runner}")
            : McpToolExecutionResult.Error($"创建扩展 '{extensionId}' 失败: {result.Message}");
    }

    private async Task<McpToolExecutionResult> HandleExtensionGetLogsAsync(JsonElement args)
    {
        if (!args.TryGetProperty("extension_id", out var eProp) || string.IsNullOrWhiteSpace(eProp.GetString()))
            return McpToolExecutionResult.Error("缺少 extension_id 参数");

        var extensionId = eProp.GetString()!.Trim();
        var lines = args.TryGetProperty("lines", out var lProp) && lProp.TryGetInt32(out var l) ? l : 100;

        var logs = await extensionManager.GetExtensionLogsAsync(extensionId, lines);
        return McpToolExecutionResult.Success(logs);
    }

    private async Task<McpToolExecutionResult> HandleExtensionDebugRunAsync(JsonElement args)
    {
        if (!args.TryGetProperty("extension_id", out var eProp) || string.IsNullOrWhiteSpace(eProp.GetString()))
            return McpToolExecutionResult.Error("缺少 extension_id 参数");

        var extensionId = eProp.GetString()!.Trim();
        var hookName = args.TryGetProperty("hook_name", out var hProp) ? hProp.GetString() : "status";
        var timeout = args.TryGetProperty("timeout_seconds", out var tProp) && tProp.TryGetInt32(out var t) ? t : 8;

        Dictionary<string, string>? paramDict = null;
        if (args.TryGetProperty("params", out var pProp) && pProp.ValueKind == JsonValueKind.Object)
        {
            paramDict = new Dictionary<string, string>();
            foreach (var prop in pProp.EnumerateObject())
            {
                paramDict[prop.Name] = prop.Value.GetString() ?? "";
            }
        }

        var res = await extensionManager.DebugRunAsync(extensionId, new ExtensionDebugRunRequest
        {
            HookName = hookName,
            Params = paramDict,
            TimeoutSeconds = timeout
        });

        var sb = new StringBuilder();
        sb.AppendLine($"=== 运维扩展 [{extensionId}] 瞬态调试执行 ({hookName}) ===");
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
}
