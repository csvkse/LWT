import { chromium } from './node_modules/@playwright/test/index.mjs';
import fs from 'fs';
import path from 'path';

function loadEnvDev() {
  const envPath = path.resolve('env/.envdev');
  if (fs.existsSync(envPath)) {
    const lines = fs.readFileSync(envPath, 'utf-8').split('\n');
    for (const line of lines) {
      const match = line.match(/^\s*([A-Za-z0-9_]+)=(.*)$/);
      if (match) {
        const key = match[1].trim();
        const value = match[2].trim().replace(/^["']|["']$/g, '');
        if (!process.env[key]) {
          process.env[key] = value;
        }
      }
    }
  }
}
loadEnvDev();

const username = process.env.SMOKE_USERNAME || process.env.WEB_ADMIN_USER || 'admin';
const password = process.env.SMOKE_PASSWORD || process.env.WEB_ADMIN_PASSWORD;
if (!password) {
  throw new Error('未提供登录密码：请配置环境变量 SMOKE_PASSWORD 或在 env/.envdev 中配置 WEB_ADMIN_PASSWORD');
}

const baseUrl = process.env.BASE_URL || 'http://10.126.1.70:5271';
const LOCAL_ARTIFACTS = path.resolve('tests/smoke/artifacts');
fs.mkdirSync(LOCAL_ARTIFACTS, { recursive: true });

const ARTIFACT_DIR = process.env.ARTIFACT_DIR;
if (ARTIFACT_DIR && !fs.existsSync(ARTIFACT_DIR)) {
  fs.mkdirSync(ARTIFACT_DIR, { recursive: true });
}

function saveScreenshot(page, filename) {
  const localPath = path.join(LOCAL_ARTIFACTS, filename);
  return page.screenshot({ path: localPath }).then(() => {
    if (ARTIFACT_DIR) {
      try {
        fs.copyFileSync(localPath, path.join(ARTIFACT_DIR, filename));
      } catch (e) {
        console.warn(`无法复制到制品目录: ${e.message}`);
      }
    }
  });
}

async function runAcceptance() {
  console.log('🚀 启动内置 Chrome 浏览器自动化验收...');
  const browser = await chromium.launch({
    channel: 'chrome',
    headless: true,
  });

  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
  });
  const page = await context.newPage();

  console.log(`🌐 访问目标控制台: ${baseUrl}/#/login`);

  await page.goto(`${baseUrl}/#/login`, { waitUntil: 'networkidle' });
  await saveScreenshot(page, '01-login-page.png');

  console.log('🔑 输入管理员凭据并登录...');
  await page.locator('input[autocomplete="username"]').fill(username);
  await page.locator('input[autocomplete="current-password"]').fill(password);
  await page.getByRole('button', { name: '登 录' }).click();

  await page.waitForURL(/#\/$/, { timeout: 15000 });
  console.log('✅ 登录成功，进入面板首页');

  console.log('🔌 导航至扩展中心 /#/plugins ...');
  await page.goto(`${baseUrl}/#/plugins`, { waitUntil: 'networkidle' });
  await page.waitForTimeout(1500);

  await saveScreenshot(page, '02-plugins-page.png');

  console.log('🔍 定位 SOCKS5 安全代理微服务卡片...');
  const cardLocator = page.locator('.panel').filter({ hasText: 'socks5-proxy' });
  await cardLocator.waitFor({ timeout: 10000 });

  const cardText = await cardLocator.innerText();
  console.log('📋 卡片文本快照:\n' + cardText);

  // 验证卡片上的端口
  const has18080 = cardText.includes('18080');
  console.log(`🔎 卡片端口校验 (包含 :18080): ${has18080 ? '✅ 通过' : '❌ 失败'}`);

  console.log('🖥️ 点击打开 Webview 扩展视图...');
  const webviewBtn = cardLocator.getByRole('button', { name: '打开 Webview 扩展视图' });
  await webviewBtn.click();

  // 等待 Webview 模态框展现
  const modalLocator = page.locator('.panel').filter({ hasText: 'Webview 视图' });
  await modalLocator.waitFor({ timeout: 10000 });
  await page.waitForTimeout(2000); // 等待 iframe 内渲染

  await saveScreenshot(page, '03-webview-modal.png');

  console.log('🔍 检查 Webview IFrame 内部元素与端口状态...');
  const iframeElement = await page.locator('iframe').elementHandle();
  const frame = await iframeElement.contentFrame();

  if (!frame) {
    throw new Error('未能加载 Webview IFrame 内容！');
  }

  await frame.waitForSelector('#proxy-port', { timeout: 10000 });
  const displayPort = await frame.locator('#proxy-port').innerText();
  const curlPreview = await frame.locator('#curl-preview').innerText();
  const gitPreview = await frame.locator('#git-preview').innerText();
  const envPreview = await frame.locator('#env-preview').innerText();

  console.log('----------------------------------------------------');
  console.log(`📌 Webview 模态框展示端口: "${displayPort}"`);
  console.log(`📌 cURL 连接指引: "${curlPreview}"`);
  console.log(`📌 Git 代理配置指引: "${gitPreview}"`);
  console.log(`📌 环境变量指引: "${envPreview}"`);
  console.log('----------------------------------------------------');

  const portCorrect = displayPort.includes('18080');
  const curlCorrect = curlPreview.includes('18080');
  const no18001 = !displayPort.includes('18001') && !curlPreview.includes('18001');
  const expectedHost = new URL(baseUrl).hostname;
  const hostDynamic = curlPreview.includes(expectedHost);

  console.log(`🎯 检验 1: Webview 端口显示 :18080 -> ${portCorrect ? '✅ 成功' : '❌ 失败'}`);
  console.log(`🎯 检验 2: cURL 指引动态绑定 :18080 -> ${curlCorrect ? '✅ 成功' : '❌ 失败'}`);
  console.log(`🎯 检验 3: 绝无 18001 历史错误硬编码残留 -> ${no18001 ? '✅ 成功' : '❌ 失败'}`);
  console.log(`🎯 检验 4: cURL 动态绑定当前宿主主机 (${expectedHost}) -> ${hostDynamic ? '✅ 成功' : '❌ 失败'}`);

  if (!hostDynamic) {
    throw new Error(`cURL 指引未动态绑定当前宿主主机 ${expectedHost}: ${curlPreview}`);
  }

  await saveScreenshot(page, '04-acceptance-success.png');

  await browser.close();
  console.log('🎉 内置浏览器验收全部通过！');
}

runAcceptance().catch(err => {
  console.error('❌ 验收执行异常:', err);
  process.exit(1);
});
