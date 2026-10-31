using System.Globalization;
using System.Security;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;

namespace LinuxWebTool.Application.Features.Plugins;

/// <summary>
/// 原生命令行参数与标准输入绑定解析器：
/// - 基于声明的 InputSchema 与 ArgumentBindings 进行类型安全的 argv 结构化绑定；
/// - 杜绝 Shell 拼接与注入，防路径穿越与参数混淆；
/// - 生成受信可执行的 NativeProgramSpec。
/// </summary>
public sealed class CommandBindingResolver : ICommandBindingResolver
{
    private readonly INativeProgramResolver _programResolver;

    public CommandBindingResolver(INativeProgramResolver programResolver)
    {
        _programResolver = programResolver ?? throw new ArgumentNullException(nameof(programResolver));
    }

    public ValueTask<NativeProgramSpec> ResolveAsync(
        NativeCommandDefinition definition,
        CommandInvocation invocation,
        string workingDir,
        string operationId,
        CancellationToken ct)
    {
        // 1. 基础校验
        if (definition == null) throw new ArgumentNullException(nameof(definition));
        if (invocation == null) throw new ArgumentNullException(nameof(invocation));

        // 2. 解析可执行目标文件路径
        var executablePath = _programResolver.ResolveExecutable("package", definition.ProgramRef, workingDir);

        // 3. 构建结构化 argv
        var argv = new List<string>();
        var input = invocation.Input;

        foreach (var binding in definition.ArgumentBindings)
        {
            // 条件字段检查
            if (!string.IsNullOrEmpty(binding.ConditionField))
            {
                if (!IsConditionMet(input, binding.ConditionField))
                {
                    continue;
                }
            }

            var token = ResolveTokenValue(binding, input, workingDir, operationId);
            if (token != null)
            {
                argv.Add(token);
            }
        }

        // 4. 标准输入 (Stdin) 解析
        var stdinMode = definition.StdinBinding?.Mode ?? "closed";
        string? stdinPayload = null;
        string? stdinFilePath = null;

        if (string.Equals(stdinMode, "text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stdinMode, "json", StringComparison.OrdinalIgnoreCase))
        {
            var field = definition.StdinBinding?.SourceField;
            if (!string.IsNullOrEmpty(field) && input.ValueKind == JsonValueKind.Object && input.TryGetProperty(field, out var elem))
            {
                stdinPayload = elem.ValueKind == JsonValueKind.String ? elem.GetString() : elem.GetRawText();
            }
            else if (input.ValueKind == JsonValueKind.Object)
            {
                stdinPayload = input.GetRawText();
            }
        }
        else if (string.Equals(stdinMode, "file", StringComparison.OrdinalIgnoreCase))
        {
            var field = definition.StdinBinding?.SourceField;
            if (!string.IsNullOrEmpty(field) && input.ValueKind == JsonValueKind.Object && input.TryGetProperty(field, out var elem))
            {
                var relPath = elem.GetString();
                if (!string.IsNullOrEmpty(relPath))
                {
                    stdinFilePath = PluginSecurityGuard.EnsureSafeArtifactPath(workingDir, relPath);
                }
            }
        }

        var spec = new NativeProgramSpec
        {
            Source = "package",
            ProgramRef = definition.ProgramRef,
            ExecutablePath = executablePath,
            ArgumentList = argv,
            WorkingDirectory = workingDir,
            StdinMode = stdinMode,
            StdinPayload = stdinPayload,
            StdinFilePath = stdinFilePath
        };

        return ValueTask.FromResult(spec);
    }

    private static bool IsConditionMet(JsonElement input, string conditionField)
    {
        if (input.ValueKind != JsonValueKind.Object) return false;
        if (!input.TryGetProperty(conditionField, out var prop)) return false;

        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(prop.GetString()),
            JsonValueKind.Number => prop.GetInt64() != 0,
            _ => true
        };
    }

    private static string? ResolveTokenValue(
        CommandArgumentBinding binding,
        JsonElement input,
        string workingDir,
        string operationId)
    {
        var prefix = binding.Prefix ?? "";

        switch (binding.Type.ToLowerInvariant())
        {
            case "literal":
                return prefix + binding.Value;

            case "inputscalar":
            {
                if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(binding.Value, out var elem))
                {
                    return null;
                }

                var rawStr = elem.ValueKind switch
                {
                    JsonValueKind.String => elem.GetString() ?? "",
                    JsonValueKind.Number => elem.GetRawText(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    _ => elem.GetRawText()
                };

                // 阻断危险换行符与参数注入
                if (rawStr.Contains('\n') || rawStr.Contains('\r'))
                {
                    throw new SecurityException($"参数值含非法换行符: '{binding.Value}'");
                }

                return prefix + rawStr;
            }

            case "inputpath":
            {
                if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty(binding.Value, out var elem))
                {
                    return null;
                }

                var pathStr = elem.GetString();
                if (string.IsNullOrEmpty(pathStr)) return null;

                var safePath = PluginSecurityGuard.EnsureSafeArtifactPath(workingDir, pathStr);
                return prefix + safePath;
            }

            case "artifactpath":
            {
                var filename = binding.Value;
                if (string.IsNullOrEmpty(filename)) return null;

                var safePath = PluginSecurityGuard.EnsureSafeArtifactPath(workingDir, filename);
                return prefix + safePath;
            }

            default:
                return null;
        }
    }
}
