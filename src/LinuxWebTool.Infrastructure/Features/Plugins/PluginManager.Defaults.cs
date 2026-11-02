using System.Diagnostics;
using System.Text.Json;
using LinuxWebTool.Contracts.Features.Plugins.Contracts;
using LinuxWebTool.Infrastructure.Features.Plugins.Adapters;
using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Plugins;

public sealed partial class PluginManager
{
    private void EnsureDefaultPluginsCreated()
    {
        try
        {
            EnsureHealthcheckCreated();
            EnsureSshGuardCreated();
            EnsureCaddyManagerCreated();
            EnsureNativeCommandDemoCreated();
            EnsureJsWebhookCreated();
            EnsurePyApiCreated();
            EnsureDotnetServiceCreated();
            EnsureShDaemonCreated();
            EnsurePsDaemonCreated();
            EnsureChatGptChannelCreated();
            EnsureMcpBridgeCreated();
            EnsureSocks5ProxyCreated();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "创建默认插件示例异常");
        }
    }

    private void EnsureDotnetServiceCreated()
    {
        var id = "demo-dotnet-service";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir))
        {
            var existingProj = Path.Combine(dir, "demo-dotnet-service.csproj");
            if (!File.Exists(existingProj))
            {
                File.WriteAllText(existingProj, """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
""");
            }
            else
            {
                try
                {
                    var content = File.ReadAllText(existingProj);
                    if (content.Contains("<TargetFramework>net8.0</TargetFramework>"))
                    {
                        File.WriteAllText(existingProj, content.Replace("<TargetFramework>net8.0</TargetFramework>", "<TargetFramework>net10.0</TargetFramework>"));
                    }
                }
                catch { }
            }
            return;
        }

        Directory.CreateDirectory(dir);
        var manifest = new PluginManifest
        {
            Id = id,
            Name = ".NET 极速微服务",
            Category = "Backend",
            Version = "1.0.0",
            Description = "基于 C# 顶级语句的极速 HTTP 微服务示例",
            Icon = "🔷",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.DotNet,
            Entrypoint = "Program.cs",
            Network = new PluginNetworkConfig { Mode = "path", Port = 18003, PortEnv = "PORT", PathPrefix = $"/plugins/{id}" },
            Supervision = new PluginSupervisionConfig { AutoRestart = true }
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        var cs = """
using System;
using System.IO;
using System.Net;
using System.Text;

var portStr = Environment.GetEnvironmentVariable("PORT") ?? "18003";
int.TryParse(portStr, out var port);
if (port <= 0) port = 18003;

var listener = new HttpListener();
listener.Prefixes.Add($"http://*:{port}/");
try {
    listener.Start();
    Console.WriteLine($"[.NET Service] Listening on port {port}...");
    while (true) {
        var context = listener.GetContext();
        var resp = context.Response;
        resp.ContentType = "application/json";
        var buf = Encoding.UTF8.GetBytes("{\"status\":\"ok\",\"engine\":\".NET 10.0\"}");
        resp.OutputStream.Write(buf, 0, buf.Length);
        resp.Close();
    }
} catch (Exception ex) {
    Console.WriteLine($"Error: {ex.Message}");
}
""";
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
        File.WriteAllText(Path.Combine(dir, "demo-dotnet-service.csproj"), csproj);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), cs);
    }

    private void EnsureMcpBridgeCreated()
    {
        var id = "mcp-bridge";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir))
        {
            EnsureMcpBridgeBinaryPresent(dir);
            // 兼容性自愈迁移：若历史部署持久化卷中残留未绑定的宏占位符或缺失生态依赖，自动自愈升级
            var pJson = Path.Combine(dir, "plugin.json");
            if (File.Exists(pJson))
            {
                try
                {
                    var text = File.ReadAllText(pJson);
                    var m = JsonSerializer.Deserialize(text, PluginJsonContext.Default.PluginManifest);
                    if (m != null)
                    {
                        var needsUpdate = false;
                        var updatedArgs = m.Arguments;
                        if (text.Contains("{controlTokenFile}") || text.Contains("data/mcp-bridge") || text.Contains("{pluginDir}/data"))
                        {
                            updatedArgs = ["serve", "--state-dir", "{stateDir}"];
                            needsUpdate = true;
                        }

                        var updatedDeps = m.Dependencies;
                        if (updatedDeps == null || updatedDeps.Items.Count == 0)
                        {
                            updatedDeps = BuildDefaultMcpBridgeDependencies();
                            needsUpdate = true;
                        }

                        if (needsUpdate)
                        {
                            var updated = m with { Arguments = updatedArgs, Dependencies = updatedDeps };
                            File.WriteAllText(pJson, JsonSerializer.Serialize(updated, PluginJsonContext.Default.PluginManifest));
                        }
                    }
                }
                catch { }
            }
            return;
        }

        Directory.CreateDirectory(dir);
        var uiDir = Path.Combine(dir, "web");
        Directory.CreateDirectory(uiDir);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = "MCP 智能安全网关桥接服务",
            Category = "AI",
            Version = "1.0.0",
            Description = "Go 高性能单进程 OpenAI Secure MCP Tunnel 与第三方 MCP 工具纳管网关",
            Icon = "🌉",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Native,
            Entrypoint = OperatingSystem.IsWindows() ? "mcp-bridge.exe" : "mcp-bridge",
            Source = "package",
            Arguments =
            [
                "serve",
                "--state-dir",
                "{stateDir}"
            ],
            Network = new PluginNetworkConfig
            {
                Mode = "path",
                PortEnv = "PORT",
                PathPrefix = $"/plugins/{id}"
            },
            Supervision = new PluginSupervisionConfig
            {
                AutoRestart = false,
                FlappingThresholdSec = 10,
                MaxCrashRetries = 3
            },
            Ui = new PluginUiConfig
            {
                Entry = "web/index.html",
                Title = "MCP Bridge 控制台",
                Icon = "🌉"
            },
            Dependencies = BuildDefaultMcpBridgeDependencies()
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));

        var html = """
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"/><title>MCP Bridge 控制台</title>
<style>body{font-family:system-ui;padding:20px;background:#09090b;color:#fafafa;}h2{color:#a855f7;}.card{background:#18181b;padding:16px;border-radius:8px;border:1px solid #27272a;}</style>
</head>
<body>
<h2>🌉 MCP 智能安全网关控制台</h2>
<div class="card">
<p>MCP Tunnel 与第三方工具纳管网关已就绪。支持通过 REST / SSE 安全穿透外部大模型与客户端。</p>
</div>
</body>
</html>
""";
        File.WriteAllText(Path.Combine(uiDir, "index.html"), html);
        EnsureMcpBridgeBinaryPresent(dir);
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

    private static PluginDependencies BuildDefaultMcpBridgeDependencies()
    {
        return new PluginDependencies
        {
            Items =
            [
                new PluginDependencyConfig
                {
                    Id = "node-ecosystem",
                    Name = "Node.js & npx 工具链",
                    Required = false,
                    Recommended = true,
                    Description = "用于执行 GitHub、Filesystem、PostgreSQL 等基于 JavaScript/TypeScript 开发的标准 MCP 服务",
                    Probe = new PluginDependencyProbeConfig { Command = "npx", Args = ["--version"] },
                    InstallGuide = new Dictionary<string, string>
                    {
                        ["alpine"] = "apk add --no-cache nodejs npm",
                        ["debian"] = "apt update && apt install -y nodejs npm",
                        ["rhel"] = "dnf install -y nodejs npm",
                        ["windows"] = "winget install OpenJS.NodeJS.LTS"
                    }
                },
                new PluginDependencyConfig
                {
                    Id = "python-uv",
                    Name = "Python 3 & uv/uvx 工具链",
                    Required = false,
                    Recommended = true,
                    Description = "用于极速冷启动 Fetch、Git、Puppeteer 等基于 Python 生态开发的高性能 MCP 服务",
                    Probe = new PluginDependencyProbeConfig { Command = "uvx", Args = ["--version"] },
                    InstallGuide = new Dictionary<string, string>
                    {
                        ["alpine"] = "apk add --no-cache python3 py3-pip && pip install --no-cache-dir uv --break-system-packages",
                        ["debian"] = "apt update && apt install -y python3 python3-pip && pip3 install uv",
                        ["rhel"] = "dnf install -y python3 python3-pip && pip3 install uv",
                        ["windows"] = "winget install astral-sh.uv"
                    }
                }
            ]
        };
    }

    private void EnsureSocks5ProxyCreated()
    {
        var id = "socks5-proxy";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        // 优先从应用根目录的 plugins/socks5-proxy 模板同步
        var templateDir = Path.Combine(AppContext.BaseDirectory, "plugins", id);
        if (!Directory.Exists(templateDir))
        {
            var parentDir = Directory.GetParent(AppContext.BaseDirectory)?.FullName;
            if (parentDir != null)
            {
                var alt = Path.Combine(parentDir, "plugins", id);
                if (Directory.Exists(alt)) templateDir = alt;
            }
        }

        if (Directory.Exists(templateDir))
        {
            CopyDirectoryRecursively(templateDir, dir);
            return;
        }

        Directory.CreateDirectory(dir);
        var cmdDir = Path.Combine(dir, "cmd", "socks5-proxy");
        var uiDir = Path.Combine(dir, "ui");
        Directory.CreateDirectory(cmdDir);
        Directory.CreateDirectory(uiDir);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = "SOCKS5 安全代理微服务",
            Category = "Network",
            Version = "1.0.0",
            Description = "基于 Go 原生运行引擎的极速高并发 SOCKS5 代理服务，支持 RFC 1928 / RFC 1929 规范与身份认证",
            Icon = "🧦",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Go,
            Entrypoint = "cmd/socks5-proxy/main.go",
            Arguments = ["--listen", "0.0.0.0:{port}", "--state-dir", "{stateDir}", "--auth-mode", "none", "--username", "", "--password", "", "--acl-preset", "lan", "--allowed-subnets", "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16,127.0.0.0/8,::1/128", "--allowed-domains", "*.local,*.internal,*.lan,localhost", "--allowed-clients", ""],
            Network = new PluginNetworkConfig
            {
                Mode = "port",
                Port = 18080,
                PortEnv = "SOCKS5_PORT",
                PortConflictPolicy = "avoid"
            },
            Supervision = new PluginSupervisionConfig
            {
                AutoRestart = true,
                FlappingThresholdSec = 10,
                MaxCrashRetries = 3
            },
            Ui = new PluginUiConfig
            {
                Entry = "ui/index.html",
                Title = "SOCKS5 代理控制台",
                Icon = "🧦"
            }
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
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
}
