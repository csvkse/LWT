const { execSync } = require('child_process');
try {
  execSync('systemctl start caddy || caddy start --config /etc/caddy/Caddyfile', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({
    protocolVersion: 1,
    status: 'succeeded',
    message: 'Caddy 服务启动成功'
  }));
} catch (err) {
  console.error('启动异常: ' + err.message);
  console.log(JSON.stringify({
    protocolVersion: 1,
    status: 'failed',
    error: err.message,
    message: `启动失败: ${err.message}`
  }));
  process.exit(1);
}
