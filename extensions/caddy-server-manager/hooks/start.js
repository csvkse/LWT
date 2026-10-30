const { execSync } = require('child_process');
try {
  execSync('systemctl start caddy || caddy start --config /etc/caddy/Caddyfile', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({ success: true, message: 'Caddy 服务启动成功' }));
} catch (err) {
  console.log(JSON.stringify({ success: false, message: `启动失败: ${err.message}` }));
}
