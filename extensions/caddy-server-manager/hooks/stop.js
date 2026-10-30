const { execSync } = require('child_process');
try {
  execSync('systemctl stop caddy || caddy stop', { stdio: 'pipe', timeout: 8000 });
  console.log(JSON.stringify({ success: true, message: 'Caddy 服务已停止' }));
} catch (err) {
  console.log(JSON.stringify({ success: false, message: `停止失败: ${err.message}` }));
}
