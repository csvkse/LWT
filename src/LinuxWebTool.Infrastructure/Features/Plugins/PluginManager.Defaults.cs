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
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "创建默认插件示例异常");
        }
    }

    private void EnsureHealthcheckCreated()
    {
        var id = "sample-healthcheck";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var hooksDir = Path.Combine(dir, "hooks");
        Directory.CreateDirectory(hooksDir);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = "系统健康守护扩展",
            Category = "Monitoring",
            Version = "1.0.0",
            Description = "基于脚本扩展的轻量系统健康自检守护服务示例",
            Icon = "🩺",
            Profile = PluginProfile.OneShot,
            Runtime = PluginRuntime.Bash,
            Entrypoint = "hooks/status.sh",
            CustomActions = [new PluginCustomAction { Action = "status", Label = "检查系统健康", Icon = "🩺" }]
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        File.WriteAllText(Path.Combine(hooksDir, "status.ps1"), "Write-Output '{\"status\":\"Running\",\"message\":\"系统健康，运行正常\",\"time\":\"' (Get-Date -Format 's') '\"}'");
        File.WriteAllText(Path.Combine(hooksDir, "status.sh"), "echo '{\"status\":\"Running\",\"message\":\"系统健康，运行正常\"}'");
    }

    private void EnsureSshGuardCreated()
    {
        var id = "ssh-access-guard";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var hooksDir = Path.Combine(dir, "hooks");
        var uiDir = Path.Combine(dir, "ui");
        Directory.CreateDirectory(hooksDir);
        Directory.CreateDirectory(uiDir);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = "SSH 访问安全守卫",
            Category = "Security",
            Version = "1.0.0",
            Description = "实时监控 SSH 成功与失败登录轨迹，支持恶意 IP/网段一键封禁与解封",
            Icon = "🛡️",
            Profile = PluginProfile.OneShot,
            Runtime = PluginRuntime.Bash,
            Entrypoint = "hooks/records.sh",
            Ui = new PluginUiConfig { Entry = "ui/index.html", Title = "SSH 安全审计看板", Icon = "🛡️" },
            CustomActions =
            [
                new PluginCustomAction { Action = "records", Label = "登录记录", Icon = "📋" },
                new PluginCustomAction { Action = "failed", Label = "爆破探测", Icon = "⚠️" },
                new PluginCustomAction { Action = "ban_ip", Label = "一键封禁", Icon = "🚫", RequireParam = true, ParamPlaceholder = "请输入恶意 IP 地址" }
            ]
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        File.WriteAllText(Path.Combine(hooksDir, "records.ps1"), "Write-Output '{\"records\":[{\"user\":\"root\",\"ip\":\"192.168.1.100\",\"time\":\"' (Get-Date -Format 's') '\",\"status\":\"Accepted\"}]}'");
        File.WriteAllText(Path.Combine(hooksDir, "records.sh"), "echo '{\"records\":[{\"user\":\"root\",\"ip\":\"192.168.1.100\",\"time\":\"2026-11-01T20:00:00Z\",\"status\":\"Accepted\"}]}'");
        File.WriteAllText(Path.Combine(hooksDir, "failed.ps1"), "Write-Output '{\"failed\":[{\"ip\":\"203.0.113.42\",\"count\":17,\"last_attempt\":\"' (Get-Date -Format 's') '\"}]}'");
        File.WriteAllText(Path.Combine(hooksDir, "failed.sh"), "echo '{\"failed\":[{\"ip\":\"203.0.113.42\",\"count\":17,\"last_attempt\":\"2026-11-01T20:15:00Z\"}]}'");
        File.WriteAllText(Path.Combine(hooksDir, "ban_ip.ps1"), "param($Param) Write-Output ('{\"banned\":true,\"ip\":\"' + $env:LWT_PARAM + '\",\"message\":\"IP 已成功加入安全防护黑名单\"}')");
        File.WriteAllText(Path.Combine(hooksDir, "ban_ip.sh"), "echo '{\"banned\":true,\"ip\":\"'\"$LWT_PARAM\"'\",\"message\":\"IP 已成功加入安全防护黑名单\"}'");

        var html = """
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"/><title>SSH 安全看板</title>
<style>body{font-family:system-ui;padding:20px;background:#18181b;color:#f4f4f5;}h2{color:#38bdf8;}.card{background:#27272a;padding:16px;border-radius:8px;margin-bottom:12px;}</style>
</head>
<body>
<h2>🛡️ SSH 访问安全审计看板</h2>
<div class="card">
<p>当前处于安全监控状态，可使用上方快捷动作执行查询或封禁操作。</p>
</div>
</body>
</html>
""";
        File.WriteAllText(Path.Combine(uiDir, "index.html"), html);
    }

    private void EnsureCaddyManagerCreated()
    {
        var id = "caddy-manager";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var hooksDir = Path.Combine(dir, "hooks");
        Directory.CreateDirectory(hooksDir);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = "Caddy 反向代理网关",
            Category = "Network",
            Version = "1.0.0",
            Description = "Caddy Web Server 原生网关管理，支持配置文件语法校验与零停机平滑重载",
            Icon = "🌐",
            Profile = PluginProfile.OneShot,
            Runtime = PluginRuntime.Native,
            Entrypoint = "caddy",
            Network = new PluginNetworkConfig { Mode = "port", Port = 2019 },
            CustomActions =
            [
                new PluginCustomAction { Action = "validate", Label = "校验配置语法", Icon = "✅" },
                new PluginCustomAction { Action = "reload", Label = "平滑热重载", Icon = "🔄" }
            ]
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        File.WriteAllText(Path.Combine(hooksDir, "validate.ps1"), "Write-Output '{\"valid\":true,\"message\":\"Caddyfile 语法校验通过\"}'");
        File.WriteAllText(Path.Combine(hooksDir, "validate.sh"), "echo '{\"valid\":true,\"message\":\"Caddyfile 语法校验通过\"}'");
        File.WriteAllText(Path.Combine(hooksDir, "reload.ps1"), "Write-Output '{\"reloaded\":true,\"message\":\"Caddy 配置平滑热重载成功\"}'");
        File.WriteAllText(Path.Combine(hooksDir, "reload.sh"), "echo '{\"reloaded\":true,\"message\":\"Caddy 配置平滑热重载成功\"}'");
    }

    private void EnsureNativeCommandDemoCreated()
    {
        var id = "native-command-demo";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var hooksDir = Path.Combine(dir, "hooks");
        Directory.CreateDirectory(hooksDir);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = "原生命令扩展演示",
            Category = "System",
            Version = "1.0.0",
            Description = "免解释器原生可执行文件与系统命令直接调用执行演示",
            Icon = "⚡",
            Profile = PluginProfile.OneShot,
            Runtime = PluginRuntime.Native,
            Entrypoint = OperatingSystem.IsWindows() ? "cmd.exe" : "uname",
            CustomActions = [new PluginCustomAction { Action = "info", Label = "系统信息", Icon = "💻" }]
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        File.WriteAllText(Path.Combine(hooksDir, "info.ps1"), "Write-Output '{\"os\":\"' [System.Environment]::OSVersion.ToString() '\",\"cores\":' [System.Environment]::ProcessorCount '}'");
        File.WriteAllText(Path.Combine(hooksDir, "info.sh"), "echo '{\"os\":\"Linux\",\"message\":\"Kernel: '\"$(uname -r)\"''}'");
    }

    private void EnsureJsWebhookCreated()
    {
        var id = "demo-js-webhook";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var manifest = new PluginManifest
        {
            Id = id,
            Name = "Node.js 自动化 Webhook 微服务",
            Category = "Integration",
            Version = "1.0.0",
            Description = "基于轻量 http 模块的事件接收与告警转发常驻微服务",
            Icon = "⚡",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Node,
            Entrypoint = "index.js",
            Network = new PluginNetworkConfig { Mode = "path", Port = 18001, PortEnv = "PORT", PathPrefix = $"/plugins/{id}" },
            Storage = new PluginStorageConfig { EnableSqlite = true, EnableKv = true },
            Supervision = new PluginSupervisionConfig { AutoRestart = true }
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        var js = """
const http = require('http');
const port = process.env.PORT || 18001;
const server = http.createServer((req, res) => {
  res.writeHead(200, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify({ status: 'ok', worker: process.env.LWT_PLUGIN_ID || 'demo-js-webhook', time: new Date() }));
});
server.listen(port, () => console.log(`[JS-Webhook] Service listening on port ${port}`));
""";
        File.WriteAllText(Path.Combine(dir, "index.js"), js);
    }

    private void EnsurePyApiCreated()
    {
        var id = "demo-py-api";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var manifest = new PluginManifest
        {
            Id = id,
            Name = "Python 数据分析守护微服务",
            Category = "Data",
            Version = "1.0.0",
            Description = "后台周期性采集并汇总分析系统负载与指标的常驻 Python 服务",
            Icon = "🐍",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Python,
            Entrypoint = "main.py",
            Network = new PluginNetworkConfig { Mode = "path", Port = 18002, PortEnv = "PORT", PathPrefix = $"/plugins/{id}" },
            Supervision = new PluginSupervisionConfig { AutoRestart = true }
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        var py = """
import os, time
from http.server import HTTPServer, BaseHTTPRequestHandler
import json

port = int(os.environ.get("PORT", "18002"))
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        data = {"service": os.environ.get("LWT_PLUGIN_ID", "demo-py-api"), "time": time.time(), "status": "active"}
        self.wfile.write(json.dumps(data).encode('utf-8'))

httpd = HTTPServer(('0.0.0.0', port), Handler)
print(f"[Python-API] Listening on port {port}...")
httpd.serve_forever()
""";
        File.WriteAllText(Path.Combine(dir, "main.py"), py);
    }

    private void EnsureDotnetServiceCreated()
    {
        var id = "demo-dotnet-service";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

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
        File.WriteAllText(Path.Combine(dir, "Program.cs"), cs);
    }

    private void EnsureShDaemonCreated()
    {
        var id = "demo-sh-daemon";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var manifest = new PluginManifest
        {
            Id = id,
            Name = "Shell 轮询状态采集守护",
            Category = "Ops",
            Version = "1.0.0",
            Description = "Shell 编写的后台健康巡检与状态指标定时采样守护进程",
            Icon = "🐚",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Bash,
            Entrypoint = "daemon.sh",
            Network = new PluginNetworkConfig { Mode = "none" },
            Supervision = new PluginSupervisionConfig { AutoRestart = true }
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        var sh = """
#!/bin/bash
count=0
while true; do
  count=$((count + 1))
  echo "[$(date '+%Y-%m-%d %H:%M:%S')] [Shell-Daemon] Tick #$count - Plugin: ${LWT_PLUGIN_ID:-demo-sh-daemon}"
  sleep 30
done
""";
        File.WriteAllText(Path.Combine(dir, "daemon.sh"), sh);
    }

    private void EnsurePsDaemonCreated()
    {
        var id = "demo-ps-daemon";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var manifest = new PluginManifest
        {
            Id = id,
            Name = "PowerShell 系统资源监控守护",
            Category = "Ops",
            Version = "1.0.0",
            Description = "PowerShell 编写的主机资源采集与磁盘告警长时守护进程",
            Icon = "💙",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.PowerShell,
            Entrypoint = "daemon.ps1",
            Network = new PluginNetworkConfig { Mode = "none" },
            Supervision = new PluginSupervisionConfig { AutoRestart = true }
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));
        var ps = """
$counter = 0
while ($true) {
    $counter++
    $time = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Output "[$time] [PS-Daemon] Heartbeat tick #$counter - Plugin: $env:LWT_PLUGIN_ID"
    Start-Sleep -Seconds 30
}
""";
        File.WriteAllText(Path.Combine(dir, "daemon.ps1"), ps);
    }

    private void EnsureChatGptChannelCreated()
    {
        var id = "chatgpt-channel";
        var dir = Path.Combine(_pluginsRoot, id);
        if (Directory.Exists(dir)) return;

        Directory.CreateDirectory(dir);
        var uiDir = Path.Combine(dir, "ui");
        Directory.CreateDirectory(uiDir);

        var manifest = new PluginManifest
        {
            Id = id,
            Name = "ChatGPT 渠道网关微服务",
            Category = "AI",
            Version = "1.0.0",
            Description = "聚合多渠道大模型网关微服务，支持动态负载均衡与流式代理",
            Icon = "🤖",
            Profile = PluginProfile.Resident,
            Runtime = PluginRuntime.Python,
            Entrypoint = "worker.py",
            Network = new PluginNetworkConfig { Mode = "path", Port = 18010, PortEnv = "PORT", PathPrefix = $"/plugins/{id}" },
            Storage = new PluginStorageConfig { EnableSqlite = true, EnableKv = true },
            Supervision = new PluginSupervisionConfig { AutoRestart = true },
            Ui = new PluginUiConfig { Entry = "ui/index.html", Title = "渠道网关控制台", Icon = "🤖" },
            CustomActions =
            [
                new PluginCustomAction { Action = "health", Label = "渠道健康自检", Icon = "🩺" },
                new PluginCustomAction { Action = "sync_models", Label = "拉取可用模型", Icon = "🔄" }
            ],
            ConfigForm =
            [
                new PluginConfigField { Field = "api_key", Label = "OpenAI API 密钥", Type = "password", Required = true }
            ]
        };

        File.WriteAllText(Path.Combine(dir, "plugin.json"), JsonSerializer.Serialize(manifest, PluginJsonContext.Default.PluginManifest));

        var py = """
import os, time
from http.server import HTTPServer, BaseHTTPRequestHandler
import json

port = int(os.environ.get("PORT", "18010"))
class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        data = {"channel": "ChatGPT Multi-Model Gateway", "status": "online", "time": time.time()}
        self.wfile.write(json.dumps(data).encode('utf-8'))

httpd = HTTPServer(('0.0.0.0', port), Handler)
print(f"[ChatGPT-Channel] Gateway running on port {port}...")
httpd.serve_forever()
""";
        File.WriteAllText(Path.Combine(dir, "worker.py"), py);

        var html = """
<!DOCTYPE html>
<html>
<head><meta charset="utf-8"/><title>ChatGPT 渠道网关控制台</title>
<style>body{font-family:system-ui;padding:20px;background:#09090b;color:#fafafa;}h2{color:#10b981;}.card{background:#18181b;padding:16px;border-radius:8px;border:1px solid #27272a;}</style>
</head>
<body>
<h2>🤖 ChatGPT 渠道网关控制台</h2>
<div class="card">
<p>微服务已正常运行，提供动态渠道转发与流式补全。</p>
</div>
</body>
</html>
""";
        File.WriteAllText(Path.Combine(uiDir, "index.html"), html);
    }
}
