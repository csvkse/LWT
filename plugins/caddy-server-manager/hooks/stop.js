const fs = require('fs');
const { execSync } = require('child_process');

function isSystemd() {
  return fs.existsSync('/run/systemd/system');
}

function findCaddyCmd() {
  if (fs.existsSync('/usr/sbin/caddy')) return '/usr/sbin/caddy';
  if (fs.existsSync('/usr/bin/caddy')) return '/usr/bin/caddy';
  if (fs.existsSync('/usr/local/bin/caddy')) return '/usr/local/bin/caddy';
  return 'caddy';
}

try {
  const caddyCmd = findCaddyCmd();
  let cmd = '';
  if (isSystemd()) {
    cmd = `systemctl stop caddy || ${caddyCmd} stop >/dev/null 2>&1`;
  } else {
    cmd = `${caddyCmd} stop >/dev/null 2>&1`;
  }
  execSync(cmd, { stdio: 'ignore', timeout: 5000 });
  console.log(JSON.stringify({
    protocolVersion: 1,
    status: 'succeeded',
    message: 'Caddy 服务停止成功'
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
