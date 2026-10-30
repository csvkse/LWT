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
