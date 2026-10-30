using Microsoft.Extensions.Logging;

namespace LinuxWebTool.Infrastructure.Features.Services.Adapters.Extensions;

public sealed partial class ScriptExtensionManager
{
    private void EnsureDefaultExtensionsCreated()
    {
        EnsureSampleExtensionCreated();
        EnsureSshAccessGuardCreated();
        EnsureCaddyManagerCreated();
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
            File.WriteAllText(Path.Combine(hooksDir, "status.ps1"), "Write-Output '{\"status\":\"Running\",\"message\":\"Active & healthy\",\"pid\":9999}'");
            File.WriteAllText(Path.Combine(hooksDir, "status.sh"), "echo '{\"status\":\"Running\",\"message\":\"Active & healthy\",\"pid\":9999}'");
            File.WriteAllText(Path.Combine(hooksDir, "start.ps1"), "Write-Output 'Started'");
            File.WriteAllText(Path.Combine(hooksDir, "start.sh"), "echo 'Started'");
            File.WriteAllText(Path.Combine(hooksDir, "stop.ps1"), "Write-Output 'Stopped'");
            File.WriteAllText(Path.Combine(hooksDir, "stop.sh"), "echo 'Stopped'");
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
  "id": "ssh-access-guard",
  "name": "SSH 访问安全卫士",
  "category": "security",
  "version": "1.0.0",
  "description": "实时监控 SSH 成功与失败登录轨迹，支持恶意 IP/网段一键封禁与解封",
  "icon": "🛡️",
  "supportedPlatforms": ["linux", "windows"],
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
echo "{\"status\":\"Running\",\"message\":\"在线会话: ${ACTIVE_COUNT} 个 | 封禁黑名单: ${BAN_COUNT} 条\",\"pid\":null}"
""";
            File.WriteAllText(Path.Combine(hooksDir, "status.sh"), statusSh);
            File.WriteAllText(Path.Combine(hooksDir, "status.ps1"), "Write-Output '{\"status\":\"Running\",\"message\":\"SSH 守卫待命 (Windows 模拟态)\",\"pid\":null}'");

            const string recordsSh = """
#!/bin/bash
echo "=== 最近成功登录记录 (TOP 15) ==="
last -n 15 2>/dev/null || echo "无法读取 last 历史"
echo ""
echo "=== 最近登录失败与密码错误尝试 (TOP 15) ==="
lastb -n 15 2>/dev/null || grep "Failed password" /var/log/auth.log 2>/dev/null | tail -n 15 || echo "无失败记录或无权限读取"
echo ""
echo "=== 当前活跃在线会话 ==="
who -u 2>/dev/null || echo "无在线用户"
""";
            File.WriteAllText(Path.Combine(hooksDir, "records.sh"), recordsSh);
            File.WriteAllText(Path.Combine(hooksDir, "records.ps1"), "Write-Output 'Windows 环境: 暂无 Linux SSH 审计记录'");

            const string bannedSh = """
#!/bin/bash
if [ ! -f /etc/hosts.deny ]; then
    echo "未配置 /etc/hosts.deny (封禁黑名单为空)"
    exit 0
fi
echo "=== /etc/hosts.deny SSH 封禁名单 ==="
grep "^sshd:" /etc/hosts.deny 2>/dev/null || echo "暂无已封禁 IP"
""";
            File.WriteAllText(Path.Combine(hooksDir, "banned_list.sh"), bannedSh);
            File.WriteAllText(Path.Combine(hooksDir, "banned_list.ps1"), "Write-Output 'Windows 环境: 封禁名单为空'");

            const string banSh = """
#!/bin/bash
TARGET_IP="$1"
if [ -z "$TARGET_IP" ]; then
    echo '{"success":false,"message":"缺少目标 IP 参数"}'
    exit 1
fi
if [ "$TARGET_IP" = "127.0.0.1" ] || [ "$TARGET_IP" = "::1" ] || [ "$TARGET_IP" = "localhost" ]; then
    echo '{"success":false,"message":"安全保护：禁止封禁本地回环地址"}'
    exit 1
fi

touch /etc/hosts.deny 2>/dev/null
if grep -q "^sshd:[[:space:]]*${TARGET_IP}" /etc/hosts.deny 2>/dev/null; then
    echo "{\"success\":true,\"message\":\"IP ${TARGET_IP} 已经在封禁黑名单中\"}"
    exit 0
fi

echo "sshd: ${TARGET_IP}" >> /etc/hosts.deny 2>/dev/null
if [ $? -ne 0 ]; then
    echo '{"success":false,"message":"写入 /etc/hosts.deny 失败 (权限不足)"}'
    exit 1
fi

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw insert 1 deny from "${TARGET_IP}" to any port 22 comment 'LWT SSH Guard' >/dev/null 2>&1
fi

echo "{\"success\":true,\"message\":\"成功将 ${TARGET_IP} 加入 SSH 封禁黑名单\"}"
""";
            File.WriteAllText(Path.Combine(hooksDir, "ban_ip.sh"), banSh);
            File.WriteAllText(Path.Combine(hooksDir, "ban_ip.ps1"), "param($ip) Write-Output \"{\\\"success\\\":true,\\\"message\\\":\\\"(Windows模拟) 成功封禁 $ip\\\"}\"");

            const string unbanSh = """
#!/bin/bash
TARGET_IP="$1"
if [ -z "$TARGET_IP" ]; then
    echo '{"success":false,"message":"缺少目标 IP 参数"}'
    exit 1
fi

if [ -f /etc/hosts.deny ]; then
    sed -i "/^sshd:[[:space:]]*${TARGET_IP}/d" /etc/hosts.deny 2>/dev/null
fi

if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q "Status: active"; then
    ufw delete deny from "${TARGET_IP}" to any port 22 >/dev/null 2>&1
fi

echo "{\"success\":true,\"message\":\"成功解封 IP ${TARGET_IP}\"}"
""";
            File.WriteAllText(Path.Combine(hooksDir, "unban_ip.sh"), unbanSh);
            File.WriteAllText(Path.Combine(hooksDir, "unban_ip.ps1"), "param($ip) Write-Output \"{\\\"success\\\":true,\\\"message\\\":\\\"(Windows模拟) 成功解封 $ip\\\"}\"");
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
    "debian": "curl -fsSL https://deb.nodesource.com/setup_20.x | bash - && apt install -y nodejs && apt install -y debian-keyring debian-archive-keyring apt-transport-https && curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/gpg.key' | gpg --dearmor -o /usr/share/keyrings/caddy-stable-archive-keyring.gpg && curl -1sLf 'https://dl.cloudsmith.io/public/caddy/stable/debian.deb.txt' | tee /etc/apt/sources.list.d/caddy-stable.list && apt update && apt install -y caddy",
    "rhel": "dnf module install -y nodejs:20 && dnf copr enable @caddy/caddy && dnf install -y caddy",
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
    console.log(JSON.stringify({ status: 'Running', message: 'Caddy 运行中 (Admin API 就绪: 2019)', pid: null }));
    return;
  }
  try {
    const ver = execSync('caddy version', { encoding: 'utf-8', timeout: 2000 }).trim();
    console.log(JSON.stringify({ status: 'Stopped', message: `未运行 (已安装: ${ver})`, pid: null }));
  } catch {
    console.log(JSON.stringify({ status: 'Stopped', message: '服务未启动或未安装 caddy', pid: null }));
  }
}
main();
""";
            File.WriteAllText(Path.Combine(hooksDir, "status.js"), statusJs);

            const string startJs = """
const { execSync } = require('child_process');
try {
  execSync('systemctl start caddy || caddy start --config /etc/caddy/Caddyfile', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({ success: true, message: 'Caddy 服务启动成功' }));
} catch (err) {
  console.log(JSON.stringify({ success: false, message: `启动失败: ${err.message}` }));
}
""";
            File.WriteAllText(Path.Combine(hooksDir, "start.js"), startJs);

            const string stopJs = """
const { execSync } = require('child_process');
try {
  execSync('systemctl stop caddy || caddy stop', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({ success: true, message: 'Caddy 服务已停止' }));
} catch (err) {
  console.log(JSON.stringify({ success: false, message: `停止失败: ${err.message}` }));
}
""";
            File.WriteAllText(Path.Combine(hooksDir, "stop.js"), stopJs);

            const string restartJs = """
const { execSync } = require('child_process');
try {
  execSync('caddy reload --config /etc/caddy/Caddyfile || systemctl restart caddy', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({ success: true, message: 'Caddy 服务已重载/重启' }));
} catch (err) {
  console.log(JSON.stringify({ success: false, message: `重载失败: ${err.message}` }));
}
""";
            File.WriteAllText(Path.Combine(hooksDir, "restart.js"), restartJs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to create caddy-server-manager extension");
        }
    }
}
