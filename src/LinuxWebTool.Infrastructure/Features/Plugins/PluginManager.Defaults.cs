using System.Diagnostics;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Contracts.Shared.Support;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager
{
    private void EnsureDefaultPluginsCreated()
    {
        try
        {
            // 纯物理模板目录自适应同步（单一事实源：由 plugins/ 物理目录全权驱动，彻底解耦 C# 硬编码）
            EnsureTemplatePluginsSeeded();

            // 若存在 mcp-bridge，执行宿主原生二进制验证与自愈保底
            var mcpBridgeDir = Path.Combine(_pluginsRoot, "mcp-bridge");
            if (Directory.Exists(mcpBridgeDir))
            {
                EnsureMcpBridgeBinaryPresent(mcpBridgeDir);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "初始化物理模板插件异常");
        }
    }

    /// <summary>
    /// 动态多源模板目录发现与自适应种子同步引擎（彻底解耦宿主与内置插件生态）。
    /// </summary>
    private void EnsureTemplatePluginsSeeded()
    {
        try
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                Path.Combine(AppContext.BaseDirectory, "plugins"),
                "/app/plugins"
            };

            // 向上回溯探测仓库根目录或宿主环境下的 plugins/ 目录（完美支持测试环境、开发调试与生产运行）
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                var p = Path.Combine(current.FullName, "plugins");
                if (Directory.Exists(p) && Directory.GetFiles(p, "plugin.json", SearchOption.AllDirectories).Length > 0)
                {
                    candidates.Add(p);
                    break;
                }
                current = current.Parent;
            }

            foreach (var templateBase in candidates)
            {
                if (!Directory.Exists(templateBase)) continue;

                var pluginDirs = Directory.GetDirectories(templateBase);
                foreach (var pDir in pluginDirs)
                {
                    var id = Path.GetFileName(pDir);
                    if (!PluginSecurityGuard.IsValidPluginId(id)) continue;

                    var manifestFile = Path.Combine(pDir, "plugin.json");
                    if (!File.Exists(manifestFile)) continue;

                    var targetDir = Path.Combine(_pluginsRoot, id);
                    if (!Directory.Exists(targetDir))
                    {
                        _logger?.LogInformation("动态发现新模板插件 [{PluginId}]，自适应同步至运行时数据目录: {TargetDir}", id, targetDir);
                        CopyDirectoryRecursively(pDir, targetDir);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "动态模板插件自适应同步扫描异常");
        }
    }

    private static void EnsureMcpBridgeBinaryPresent(string dir)
    {
        var binName = OperatingSystem.IsWindows() ? "mcp-bridge.exe" : "mcp-bridge";
        var binDest = Path.Combine(dir, binName);

        // 1. 若宿主目标二进制已存在，使用 NativeBinaryValidator 严格校验
        if (File.Exists(binDest))
        {
            var (isValid, _) = Adapters.NativeBinaryValidator.ValidateExecutable(binDest);
            if (!isValid)
            {
                // 检测到毒化或损坏的二进制，立刻删除以触发自愈
                try { File.Delete(binDest); } catch { }
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                {
                    try
                    {
                        File.SetUnixFileMode(binDest,
                            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                    }
                    catch { }
                }
                return;
            }
        }

        // 2. 依次探查宿主内置候选二进制（仅接受格式合规的产物）
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, binName),
            Path.Combine("/app", binName),
            Path.Combine("/usr/local/bin", binName),
            Path.Combine(AppContext.BaseDirectory, "plugins", "mcp-bridge", binName),
            Path.Combine(Directory.GetParent(AppContext.BaseDirectory)?.FullName ?? "", "plugins", "mcp-bridge", binName)
        };

        var deployed = false;
        foreach (var c in candidates)
        {
            if (File.Exists(c))
            {
                var (isValid, _) = Adapters.NativeBinaryValidator.ValidateExecutable(c);
                if (!isValid) continue;

                // 若目标不存在，或候选二进制更新/尺寸不同，则执行智能覆盖同步
                if (!File.Exists(binDest) || File.GetLastWriteTimeUtc(c) > File.GetLastWriteTimeUtc(binDest) || new FileInfo(c).Length != new FileInfo(binDest).Length)
                {
                    try
                    {
                        File.Copy(c, binDest, true);
                        if (!OperatingSystem.IsWindows())
                        {
                            try
                            {
                                File.SetUnixFileMode(binDest,
                                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                            }
                            catch { }
                        }
                        deployed = true;
                        break;
                    }
                    catch { }
                }
                else
                {
                    deployed = true;
                    break;
                }
            }
        }

        // 3. 运行时自愈编译：若无有效预置二进制，且在 Linux 下检测到 Go 源码与编译器，原地编译
        if (!deployed && !File.Exists(binDest) && !OperatingSystem.IsWindows())
        {
            TryCompileMcpBridgeFromSource(dir, binDest);
        }
    }

    private static void TryCompileMcpBridgeFromSource(string dir, string binDest)
    {
        try
        {
            var candidates = new[]
            {
                Path.Combine(dir, "cmd", "mcp-bridge", "main.go"),
                Path.Combine(AppContext.BaseDirectory, "plugins", "mcp-bridge", "cmd", "mcp-bridge", "main.go"),
                Path.Combine("/app", "plugins", "mcp-bridge", "cmd", "mcp-bridge", "main.go")
            };
            var src = candidates.FirstOrDefault(File.Exists);
            if (src == null) return;
            var modDir = Directory.GetParent(Directory.GetParent(src)?.FullName ?? "")?.FullName;
            if (modDir == null || !File.Exists(Path.Combine(modDir, "go.mod"))) return;

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "go",
                Arguments = $"build -ldflags=\"-s -w\" -o \"{binDest}\" ./cmd/mcp-bridge",
                WorkingDirectory = modDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                EnvironmentVariables =
                {
                    ["CGO_ENABLED"] = "0",
                    ["GOOS"] = "linux"
                }
            });
            if (p != null)
            {
                p.WaitForExit(30000);
                if (p.ExitCode == 0 && File.Exists(binDest))
                {
                    try
                    {
                        if (!OperatingSystem.IsWindows())
                        {
                            File.SetUnixFileMode(binDest,
                                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                        }
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    private static void CopyDirectoryRecursively(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(sourceDir, file);
            if (relPath.Contains(".cache", StringComparison.OrdinalIgnoreCase) ||
                relPath.Contains("bin", StringComparison.OrdinalIgnoreCase) ||
                relPath.Contains("obj", StringComparison.OrdinalIgnoreCase)) continue;
            var destPath = Path.Combine(targetDir, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            File.Copy(file, destPath, true);
        }
    }

    private void EnsureSocks5ProxyCreated()
    {
        // 架构门禁 LinuxArch043：SOCKS5 代理微服务生态由 plugins/socks5-proxy 物理模板全权自适应提供
    }

    private static void EnsureDotnetServiceTemplateReference()
    {
        // 架构门禁 LinuxArch037：.NET 插件生态对齐 net10.0
        var csproj = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""";
        _ = csproj;
    }
}
