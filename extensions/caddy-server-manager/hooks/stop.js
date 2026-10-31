const { execSync } = require('child_process');
try {
  execSync('systemctl stop caddy || caddy stop', { stdio: 'pipe', timeout: 5000 });
  console.log(JSON.stringify({
    protocolVersion: 1,
    status: 'succeeded',
    message: 'Caddy 服务已停止'
  }));
} catch (err) {
  console.error('停止异常: ' + err.message);
  console.log(JSON.stringify({
    protocolVersion: 1,
    status: 'failed',
    error: err.message,
    message: `停止失败: ${err.message}`
  }));
  process.exit(1);
}
