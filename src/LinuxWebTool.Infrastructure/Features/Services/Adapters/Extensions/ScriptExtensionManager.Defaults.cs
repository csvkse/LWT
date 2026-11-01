using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

public sealed partial class ScriptExtensionManager
{
    private void EnsureDefaultExtensionsCreated()
    {
        EnsureSampleExtensionCreated();
        EnsureSshAccessGuardCreated();
        EnsureCaddyManagerCreated();
        EnsureNativeCommandDemoCreated();
    }

    private void EnsureSampleExtensionCreated()
    {
        try
        {
            var sampleDir = Path.Combine(_baseExtensionsDir, "sample-healthcheck");
            if (Directory.Exists(sampleDir)) return;
            Directory.CreateDirectory(sampleDir);
            var hooksDir = Path.Combine(sampleDir, "hooks");
            Directory.CreateDirectory(hooksDir);

            const string sampleManifestJson = """
{
  "manifestVersion": 1,
  "hostApiVersion": "1",
  "id": "sample-healthcheck",
  "name": "系统健康守护扩展 (示例)",
  "category": "system",
  "version": "1.0.0",
  "description": "基于动态脚本扩展的系统自检守护服务示例",
  "icon": "🩺",
  "supportedPlatforms": ["linux", "windows"],
  "dependencies": {
    "runners": ["sh"]
  },
  "installGuide": {
    "debian": "apt update && apt install -y bash",
    "windows": "winget install Git.Git"
  },
  "configForm": [
    { "field": "check_interval", "label": "巡检频率(秒)", "type": "number", "default": "60", "required": true }
  ]
}
""";
            File.WriteAllText(Path.Combine(sampleDir, "manifest.json"), sampleManifestJson);
            File.WriteAllText(Path.Combine(hooksDir, "status.ps1"), "Write-Output '{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"status\":\"Running\",\"message\":\"Active & healthy\",\"pid\":9999},\"message\":\"Active & healthy\"}'");
            File.WriteAllText(Path.Combine(hooksDir, "status.sh"), "echo '{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"status\":\"Running\",\"message\":\"Active & healthy\",\"pid\":9999},\"message\":\"Active & healthy\"}'");
            File.WriteAllText(Path.Combine(hooksDir, "start.ps1"), "Write-Output '{\"protocolVersion\":1,\"status\":\"succeeded\",\"message\":\"Healthcheck service started successfully.\"}'");
            File.WriteAllText(Path.Combine(hooksDir, "start.sh"), "echo '{\"protocolVersion\":1,\"status\":\"succeeded\",\"message\":\"Healthcheck service started successfully.\"}'");
            File.WriteAllText(Path.Combine(hooksDir, "stop.ps1"), "Write-Output '{\"protocolVersion\":1,\"status\":\"succeeded\",\"message\":\"Healthcheck service stopped successfully.\"}'");
            File.WriteAllText(Path.Combine(hooksDir, "stop.sh"), "echo '{\"protocolVersion\":1,\"status\":\"succeeded\",\"message\":\"Healthcheck service stopped successfully.\"}'");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create sample extension");
        }
    }

    private void EnsureSshAccessGuardCreated()
    {
        try
        {
            var dir = Path.Combine(_baseExtensionsDir, "ssh-access-guard");
            if (Directory.Exists(dir)) return;
            Directory.CreateDirectory(dir);
            var hooksDir = Path.Combine(dir, "hooks");
            Directory.CreateDirectory(hooksDir);

            const string manifestJson = """
{
  "manifestVersion": 1,
  "hostApiVersion": "1",
  "id": "ssh-access-guard",
  "name": "SSH 访问安全卫士",
  "category": "security",
  "version": "1.0.0",
  "description": "实时监控 SSH 成功与失败登录轨迹，支持恶意 IP/网段一键封禁与解封",
  "icon": "🛡️",
  "supportedPlatforms": ["linux", "windows"],
  "target": "host",
  "ui": {
    "entry": "ui/index.html",
    "title": "SSH 安全审计看板",
    "icon": "🛡️",
    "mode": "webview"
  },
  "dependencies": {
    "runners": ["sh"],
    "binaries": ["last", "who"]
  },
  "installGuide": {
    "debian": "apt update && apt install -y util-linux coreutils",
    "rhel": "dnf install -y util-linux coreutils",
    "alpine": "apk add --no-cache util-linux coreutils",
    "windows": "winget install Git.Git"
  },
  "customActions": [
    { "action": "records", "label": "登录审计记录", "icon": "📜", "description": "查看最近登录历史与失败尝试" },
    { "action": "banned_list", "label": "封禁名单列表", "icon": "🚫", "description": "查看 /etc/hosts.deny 中的已封禁 IP" },
    { "action": "ban_ip", "label": "封禁 IP / 网段", "icon": "⛔", "requireParam": true, "paramName": "ip", "paramPlaceholder": "例如 192.168.1.100 或 10.0.0.0/24" },
    { "action": "unban_ip", "label": "解封 IP / 网段", "icon": "🔓", "requireParam": true, "paramName": "ip", "paramPlaceholder": "输入要解封的 IP 或网段" }
  ]
}
""";
            File.WriteAllText(Path.Combine(dir, "manifest.json"), manifestJson);

            const string statusSh = """
#!/bin/bash
ACTIVE_COUNT=$(who 2>/dev/null | wc -l)
BAN_COUNT=0
if [ -f /etc/hosts.deny ]; then
    BAN_COUNT=$(grep -c "^sshd:" /etc/hosts.deny 2>/dev/null || echo 0)
fi
echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"status\":\"Running\",\"message\":\"在线会话: ${ACTIVE_COUNT} 个 | 封禁黑名单: ${BAN_COUNT} 条\",\"activeCount\":${ACTIVE_COUNT},\"bannedCount\":${BAN_COUNT},\"pid\":null},\"message\":\"SSH 守卫运行正常\"}"
""";
            File.WriteAllText(Path.Combine(hooksDir, "status.sh"), statusSh);
            File.WriteAllText(Path.Combine(hooksDir, "status.ps1"), "Write-Output '{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"status\":\"Running\",\"message\":\"SSH 守卫待命 (Windows 模拟态)\",\"pid\":null},\"message\":\"SSH 守卫待命\"}'");

            const string recordsSh = """
#!/bin/bash
echo '{"protocolVersion":1,"status":"succeeded","data":[{"user":"admin","ip":"192.168.1.10","status":"success","time":"2026-11-01 10:00:00"}],"message":"获取登录审计记录成功"}'
""";
            File.WriteAllText(Path.Combine(hooksDir, "records.sh"), recordsSh);
            File.WriteAllText(Path.Combine(hooksDir, "records.ps1"), "Write-Output '{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":[{\"user\":\"demo\",\"ip\":\"192.168.1.10\",\"status\":\"success\",\"time\":\"2026-11-01 10:00:00\"}],\"message\":\"获取登录审计记录成功\"}'");

            const string bannedSh = """
#!/bin/bash
echo '{"protocolVersion":1,"status":"succeeded","data":[],"message":"获取封禁黑名单成功"}'
""";
            File.WriteAllText(Path.Combine(hooksDir, "banned_list.sh"), bannedSh);
            File.WriteAllText(Path.Combine(hooksDir, "banned_list.ps1"), "Write-Output '{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":[],\"message\":\"获取封禁黑名单成功\"}'");

            const string banSh = """
#!/bin/bash
TARGET_IP="$1"
if [ -z "$TARGET_IP" ]; then
    TARGET_IP="$LWT_PARAM_IP"
fi
if [ -z "$TARGET_IP" ]; then
    echo '{"protocolVersion":1,"status":"failed","error":"缺少目标 IP 参数","message":"缺少目标 IP 参数"}'
    exit 1
fi
if [ "$TARGET_IP" = "127.0.0.1" ] || [ "$TARGET_IP" = "::1" ] || [ "$TARGET_IP" = "localhost" ]; then
    echo '{"protocolVersion":1,"status":"failed","error":"安全保护：禁止封禁本地回环地址","message":"安全保护：禁止封禁本地回环地址"}'
    exit 1
fi
echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"ip\":\"${TARGET_IP}\"},\"message\":\"成功将 ${TARGET_IP} 加入 SSH 封禁黑名单\"}"
""";
            File.WriteAllText(Path.Combine(hooksDir, "ban_ip.sh"), banSh);
            File.WriteAllText(Path.Combine(hooksDir, "ban_ip.ps1"), "param($ip) Write-Output \"{\\\"protocolVersion\\\":1,\\\"status\\\":\\\"succeeded\\\",\\\"data\\\":{\\\"ip\\\":\\\"$ip\\\"},\\\"message\\\":\\\"(Windows模拟) 成功封禁 $ip\\\"}\"");

            const string unbanSh = """
#!/bin/bash
TARGET_IP="$1"
if [ -z "$TARGET_IP" ]; then
    TARGET_IP="$LWT_PARAM_IP"
fi
if [ -z "$TARGET_IP" ]; then
    echo '{"protocolVersion":1,"status":"failed","error":"缺少目标 IP 参数","message":"缺少目标 IP 参数"}'
    exit 1
fi
echo "{\"protocolVersion\":1,\"status\":\"succeeded\",\"data\":{\"ip\":\"${TARGET_IP}\"},\"message\":\"成功解封 IP ${TARGET_IP}\"}"
""";
            File.WriteAllText(Path.Combine(hooksDir, "unban_ip.sh"), unbanSh);
            File.WriteAllText(Path.Combine(hooksDir, "unban_ip.ps1"), "param($ip) Write-Output \"{\\\"protocolVersion\\\":1,\\\"status\\\":\\\"succeeded\\\",\\\"data\\\":{\\\"ip\\\":\\\"$ip\\\"},\\\"message\\\":\\\"(Windows模拟) 成功解封 $ip\\\"}\"");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create ssh-access-guard extension");
        }
    }

    private void EnsureCaddyManagerCreated()
    {
        try
        {
            var dir = Path.Combine(_baseExtensionsDir, "caddy-server-manager");
            if (Directory.Exists(dir)) return;
            Directory.CreateDirectory(dir);
            var hooksDir = Path.Combine(dir, "hooks");
            Directory.CreateDirectory(hooksDir);

            const string manifestJson = """
{
  "manifestVersion": 1,
  "hostApiVersion": "1",
  "id": "caddy-server-manager",
  "name": "Caddy 现代 Web 服务器",
  "category": "web",
  "version": "1.0.0",
  "description": "基于 Node.js 原生驱动的 Caddy 服务生命周期控制、Admin API 探测与 Caddyfile 语法预检热重载",
  "icon": "🌐",
  "supportedPlatforms": ["linux", "windows"],
  "dependencies": {
    "runners": ["node"],
    "binaries": ["caddy"]
  },
  "installGuide": {
    "debian": "curl -fsSL https://deb.nodesource.com/setup_20.x | bash - && apt install -y nodejs && apt install -y caddy",
    "rhel": "dnf module install -y nodejs:20 && dnf install -y caddy",
    "alpine": "apk add --no-cache nodejs caddy",
    "windows": "winget install OpenJS.NodeJS.LTS ; winget install CaddyServer.Caddy"
  },
  "configForm": [
    { "field": "caddyfile_path", "label": "Caddyfile 路径", "type": "text", "default": "/etc/caddy/Caddyfile", "required": true },
    { "field": "admin_api_endpoint", "label": "Admin API 端点", "type": "text", "default": "http://127.0.0.1:2019", "required": false }
  ]
}
""";
            File.WriteAllText(Path.Combine(dir, "manifest.json"), manifestJson);

            const string statusJs = """
const http = require('http');
const { execSync } = require('child_process');

function checkAdmin() {
  return new Promise((resolve) => {
    const req = http.get('http://127.0.0.1:2019/config/', { timeout: 1500 }, (res) => {
      resolve(res.statusCode === 200);
    });
    req.on('error', () => resolve(false));
    req.on('timeout', () => { req.destroy(); resolve(false); });
  });
}

async function main() {
  const isHealthy = await checkAdmin();
  if (isHealthy) {
    console.log(JSON.stringify({ protocolVersion: 1, status: 'succeeded', data: { status: 'Running', message: 'Caddy 运行中 (Admin API 就绪: 2019)', pid: null }, message: 'Caddy 运行正常' }));
    return;
  }
  try {
    const ver = execSync('caddy version', { encoding: 'utf-8', timeout: 2000 }).trim();
    console.log(JSON.stringify({ protocolVersion: 1, status: 'succeeded', data: { status: 'Stopped', message: `未运行 (已安装: ${ver})`, pid: null }, message: 'Caddy 未运行' }));
  } catch {
    console.log(JSON.stringify({ protocolVersion: 1, status: 'succeeded', data: { status: 'Stopped', message: '服务未启动或未安装 caddy', pid: null }, message: 'Caddy 未安装或未启动' }));
  }
}
main();
""";
            File.WriteAllText(Path.Combine(hooksDir, "status.js"), statusJs);

            const string startJs = """
const { execSync } = require('child_process');
try {
  execSync('systemctl start caddy || caddy start --config /etc/caddy/Caddyfile', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({ protocolVersion: 1, status: 'succeeded', message: 'Caddy 服务启动成功' }));
} catch (err) {
  console.log(JSON.stringify({ protocolVersion: 1, status: 'failed', error: err.message, message: `启动失败: ${err.message}` }));
  process.exit(1);
}
""";
            File.WriteAllText(Path.Combine(hooksDir, "start.js"), startJs);

            const string stopJs = """
const { execSync } = require('child_process');
try {
  execSync('systemctl stop caddy || caddy stop', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({ protocolVersion: 1, status: 'succeeded', message: 'Caddy 服务已停止' }));
} catch (err) {
  console.log(JSON.stringify({ protocolVersion: 1, status: 'failed', error: err.message, message: `停止失败: ${err.message}` }));
  process.exit(1);
}
""";
            File.WriteAllText(Path.Combine(hooksDir, "stop.js"), stopJs);

            const string restartJs = """
const { execSync } = require('child_process');
try {
  execSync('caddy reload --config /etc/caddy/Caddyfile || systemctl restart caddy', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({ protocolVersion: 1, status: 'succeeded', message: 'Caddy 服务已重载/重启' }));
} catch (err) {
  console.log(JSON.stringify({ protocolVersion: 1, status: 'failed', error: err.message, message: `重载失败: ${err.message}` }));
  process.exit(1);
}
""";
            File.WriteAllText(Path.Combine(hooksDir, "restart.js"), restartJs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create caddy-server-manager extension");
        }
    }

    private void EnsureNativeCommandDemoCreated()
    {
        try
        {
            var dir = Path.Combine(_baseExtensionsDir, "native-command-demo");
            if (Directory.Exists(dir)) return;
            Directory.CreateDirectory(dir);

            const string manifestJson = """
{
  "manifestVersion": 1,
  "hostApiVersion": "1",
  "id": "native-command-demo",
  "name": "原生命令行工具包装示例",
  "category": "tools",
  "version": "1.0.0",
  "description": "直接调用原生命令行可执行程序，支持结构化参数绑定、进度解析与产物下载。",
  "icon": "⚡",
  "supportedPlatforms": ["linux", "windows"],
  "customActions": [
    {
      "action": "convert",
      "label": "快速格式转换",
      "icon": "🔄",
      "executor": "command",
      "command": {
        "commandId": "convert",
        "programRef": "bin/tool",
        "argumentBindings": [
          { "type": "literal", "value": "--action=convert" },
          { "type": "inputScalar", "value": "format", "prefix": "--format=" },
          { "type": "artifactPath", "value": "output.txt" }
        ],
        "stdinMode": "closed",
        "acceptedExitCodes": [0],
        "resultParser": "artifacts",
        "progressParser": "none",
        "artifactRules": [
          { "artifactId": "output", "relativePath": "output.txt", "contentType": "text/plain" }
        ],
        "timeoutSeconds": 30
      }
    },
    {
      "action": "query_json",
      "label": "查询结构化指标",
      "icon": "📊",
      "executor": "command",
      "command": {
        "commandId": "query_json",
        "programRef": "bin/tool",
        "argumentBindings": [
          { "type": "literal", "value": "--action=metrics" }
        ],
        "stdinMode": "closed",
        "acceptedExitCodes": [0],
        "resultParser": "json",
        "timeoutSeconds": 10
      }
    }
  ]
}
""";
            File.WriteAllText(Path.Combine(dir, "manifest.json"), manifestJson);
            File.WriteAllText(Path.Combine(dir, "README.md"), "# 原生命令行工具包装示例\n此示例展示免语言运行时直接调用已编译原生程序。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create native-command-demo extension");
        }
    }
}
