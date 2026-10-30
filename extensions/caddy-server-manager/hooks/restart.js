const { execSync } = require('child_process');
try {
  execSync('caddy reload --config /etc/caddy/Caddyfile || systemctl restart caddy', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({ success: true, message: 'Caddy 服务已重载/重启' }));
} catch (err) {
  console.log(JSON.stringify({ success: false, message: `重载失败: ${err.message}` }));
}
