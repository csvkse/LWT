const { execSync } = require('child_process');
try {
  execSync('systemctl reload caddy || caddy reload --config /etc/caddy/Caddyfile', { stdio: 'pipe', timeout: 5000 });
  console.log(JSON.stringify({
    protocolVersion: 1,
    status: 'succeeded',
    message: 'Caddy 配置重载并重启完成'
  }));
} catch (err) {
  console.error('重启/重载异常: ' + err.message);
  console.log(JSON.stringify({
    protocolVersion: 1,
    status: 'failed',
    error: err.message,
    message: `重载失败: ${err.message}`
  }));
  process.exit(1);
}
