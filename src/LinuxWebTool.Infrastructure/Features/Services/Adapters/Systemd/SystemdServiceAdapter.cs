using System.Diagnostics;
using System.Text.RegularExpressions;
using LinuxWebTool.Contracts.Features.Services.Contracts;
using LinuxWebTool.Contracts.Features.Shell.Contracts;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Systemd;

/// <summary>
/// Systemd 服务适配器实现：
/// - 支持 Linux 原生环境直接调用 systemctl / journalctl；
/// - 支持在特权 Docker 容器（--privileged --pid=host）中通过 nsenter 穿透到宿主机 PID 1 命名空间执行；
/// - Windows 开发环境自动降级为 PowerShell Get-Service / sc.exe，保障开发与测试流程顺畅。
/// </summary>
public sealed class SystemdServiceAdapter : ISystemdServiceAdapter
{
    private readonly IShellExecutor _shellExecutor;
    private readonly IServiceEnvironmentDetector _envDetector;
    private readonly ILogger<SystemdServiceAdapter> _logger;

    public SystemdServiceAdapter(
        IShellExecutor shellExecutor,
        IServiceEnvironmentDetector envDetector,
        ILogger<SystemdServiceAdapter> logger)
    {
        _shellExecutor = shellExecutor;
        _envDetector = envDetector;
        _logger = logger;
    }

    private async Task<string> BuildPrefixAsync(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows()) return "";
        var env = await _envDetector.DetectAsync(cancellationToken);
        if (env.IsDockerContainer && env.SystemdPierced)
        {
            return "nsenter -t 1 -m -u -i -n -- ";
        }
        return "";
    }

    public async Task<IReadOnlyList<SystemdUnitItem>> ListUnitsAsync(SystemdQueryCriteria criteria, CancellationToken cancellationToken = default)
    {
        if (OperatingSystem.IsWindows())
        {
            return await ListWindowsServicesAsync(criteria, cancellationToken);
        }

        var prefix = await BuildPrefixAsync(cancellationToken);
        var unitType = string.IsNullOrWhiteSpace(criteria.UnitType) ? "service" : criteria.UnitType.Trim();

        // 优先使用 --output=json 尝试 (systemd 248+)
        // 列出单元命令：systemctl list-units --type=<type> --all --no-pager --no-legend
        var cmd = $"{prefix}systemctl list-units --type={unitType} --all --no-pager --no-legend";
        var res = await _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = cmd,
            TimeoutSeconds = 8
        }, cancellationToken);

        if (res.ExitCode != 0 && string.IsNullOrWhiteSpace(res.StandardOutput))
        {
            _logger.LogWarning("systemctl list-units failed with code {Code}: {Err}", res.ExitCode, res.ErrorOutput);
            return [];
        }

        var list = new List<SystemdUnitItem>();
        var lines = res.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            // 典型输出格式：
            // UNIT                       LOAD   ACTIVE SUB     DESCRIPTION
            // cron.service               loaded active running Regular background program processing daemon
            // ● ufw.service              loaded failed failed  CLI Netfilter Manager
            var cleanLine = line.TrimStart('●', ' ', '\t');
            var parts = Regex.Split(cleanLine, @"\s+");
            if (parts.Length < 4) continue;

            var unit = parts[0];
            var load = parts[1];
            var active = parts[2];
            var sub = parts[3];
            var desc = parts.Length > 4 ? string.Join(' ', parts.Skip(4)) : "";

            // 过滤匹配
            if (!string.IsNullOrWhiteSpace(criteria.ActiveState) &&
                !criteria.ActiveState.Equals("all", StringComparison.OrdinalIgnoreCase) &&
                !active.Equals(criteria.ActiveState, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(criteria.FilterKeyword))
            {
                var kw = criteria.FilterKeyword.Trim();
                if (!unit.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                    !desc.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            list.Add(new SystemdUnitItem
            {
                Unit = unit,
                LoadState = load,
                ActiveState = active,
                SubState = sub,
                Description = desc
            });
        }

        return list;
    }

    public async Task<ServiceActionResult> ExecuteActionAsync(string unitName, ServiceActionType action, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitName))
        {
            return new ServiceActionResult { Success = false, Message = "服务名称不能为空" };
        }

        var safeUnit = unitName.Trim();
        if (safeUnit.IndexOfAny([';', '&', '|', '$', '`', '\n', '\r']) >= 0)
        {
            return new ServiceActionResult { Success = false, Message = "非法服务名称字符" };
        }

        if (OperatingSystem.IsWindows())
        {
            return await ExecuteWindowsServiceActionAsync(safeUnit, action, cancellationToken);
        }

        var prefix = await BuildPrefixAsync(cancellationToken);
        var verb = action switch
        {
            ServiceActionType.Start => "start",
            ServiceActionType.Stop => "stop",
            ServiceActionType.Restart => "restart",
            ServiceActionType.Reload => "reload",
            ServiceActionType.Enable => "enable",
            ServiceActionType.Disable => "disable",
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };

        var cmd = $"{prefix}systemctl {verb} {safeUnit}";
        var res = await _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = cmd,
            TimeoutSeconds = 15
        }, cancellationToken);

        return new ServiceActionResult
        {
            Success = res.ExitCode == 0,
            Message = res.ExitCode == 0 ? $"服务 [{safeUnit}] {verb} 执行成功" : $"执行失败: {res.ErrorOutput}",
            Output = res.StandardOutput
        };
    }

    public async Task<SystemdJournalResult> GetJournalAsync(SystemdJournalRequest request, CancellationToken cancellationToken = default)
    {
        var unit = request.UnitName.Trim();
        if (OperatingSystem.IsWindows())
        {
            return new SystemdJournalResult
            {
                UnitName = unit,
                Lines = [$"Windows 环境下无 journalctl。服务状态: {unit}"]
            };
        }

        var prefix = await BuildPrefixAsync(cancellationToken);
        var linesCount = Math.Clamp(request.Lines, 10, 500);
        var cmd = $"{prefix}journalctl -u {unit} -n {linesCount} --no-pager";

        var res = await _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = cmd,
            TimeoutSeconds = 10
        }, cancellationToken);

        var list = res.StandardOutput
            .Split('\n', StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();

        return new SystemdJournalResult
        {
            UnitName = unit,
            Lines = list
        };
    }

    public async Task<string> GetUnitFileContentAsync(string unitName, CancellationToken cancellationToken = default)
    {
        var unit = unitName.Trim();
        if (OperatingSystem.IsWindows())
        {
            return $"# Windows 模拟 Unit 文件\r\n[Service]\r\nName={unit}";
        }

        var prefix = await BuildPrefixAsync(cancellationToken);
        var cmd = $"{prefix}systemctl cat {unit}";
        var res = await _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = cmd,
            TimeoutSeconds = 8
        }, cancellationToken);

        return res.ExitCode == 0 ? res.StandardOutput : $"无法读取 Unit 内容: {res.ErrorOutput}";
    }

    #region Windows 降级适配
    private async Task<IReadOnlyList<SystemdUnitItem>> ListWindowsServicesAsync(SystemdQueryCriteria criteria, CancellationToken cancellationToken)
    {
        var res = await _shellExecutor.ExecuteAsync(new ShellRequest
        {
            CommandText = "powershell -NoProfile -Command \"Get-Service | Select-Object -Property Name, Status, DisplayName | ConvertTo-Json -Compress\"",
            TimeoutSeconds = 8
        }, cancellationToken);

        if (res.ExitCode != 0 || string.IsNullOrWhiteSpace(res.StandardOutput))
        {
            return [];
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(res.StandardOutput);
            var root = doc.RootElement;
            var list = new List<SystemdUnitItem>();

            var elements = new List<System.Text.Json.JsonElement>();
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                elements.AddRange(root.EnumerateArray());
            }
            else
            {
                elements.Add(root);
            }

            foreach (var el in elements)
            {
                var name = el.TryGetProperty("Name", out var pName) ? pName.GetString() ?? "" : "";
                var statusNum = el.TryGetProperty("Status", out var pStat) ? pStat.GetInt32() : 1;
                var displayName = el.TryGetProperty("DisplayName", out var pDisp) ? pDisp.GetString() ?? "" : "";

                var statusStr = statusNum == 4 ? "running" : (statusNum == 1 ? "stopped" : "paused");
                var activeStr = statusNum == 4 ? "active" : "inactive";

                if (!string.IsNullOrWhiteSpace(criteria.FilterKeyword))
                {
                    var kw = criteria.FilterKeyword.Trim();
                    if (!name.Contains(kw, StringComparison.OrdinalIgnoreCase) &&
                        !displayName.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                list.Add(new SystemdUnitItem
                {
                    Unit = name + ".service",
                    LoadState = "loaded",
                    ActiveState = activeStr,
                    SubState = statusStr,
                    Description = displayName
                });
            }

            return list;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse Windows Get-Service output");
            return [];
        }
    }

    private async Task<ServiceActionResult> ExecuteWindowsServiceActionAsync(string serviceName, ServiceActionType action, CancellationToken cancellationToken)
    {
        var rawName = serviceName.EndsWith(".service", StringComparison.OrdinalIgnoreCase)
            ? serviceName[..^8]
            : serviceName;

        var verb = action switch
        {
            ServiceActionType.Start => "Start-Service",
            ServiceActionType.Stop => "Stop-Service",
            ServiceActionType.Restart => "Restart-Service",
            _ => "Get-Service"
        };

        var cmd = $"powershell -NoProfile -Command \"{verb} -Name '{rawName}'\"";
        var res = await _shellExecutor.ExecuteAsync(new ShellRequest { CommandText = cmd, TimeoutSeconds = 12 }, cancellationToken);

        return new ServiceActionResult
        {
            Success = res.ExitCode == 0,
            Message = res.ExitCode == 0 ? $"Windows 服务 [{rawName}] 操作成功" : res.ErrorOutput,
            Output = res.StandardOutput
        };
    }
    #endregion
}
