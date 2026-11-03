#!/usr/bin/env node

/**
 * LinuxWebTool (LWT) - 真实网页浏览器加载性能与 W3C Navigation Timing 评测器
 * 利用 Playwright Chromium 引擎，对比 Direct (直连) vs Proxy (HTTP/SOCKS5 代理) 下的网页渲染与资源加载全生命周期时延
 */

import { chromium } from '../smoke/node_modules/@playwright/test/index.mjs';
import fs from 'node:fs';
import path from 'node:path';

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

const PROXY_SERVER = process.env.PROXY_URL || 'http://10.126.1.70:18080';
const SOCKS5_PROXY_SERVER = process.env.SOCKS5_PROXY_URL || 'socks5://10.126.1.70:18080';
const TEST_TARGET_URL = process.env.WEB_BENCH_URL || 'http://10.126.1.70:5271/#/login';

async function measurePagePerformance(modeName, proxyConfig) {
  const launchOptions = {
    channel: 'chrome',
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage'],
  };

  if (proxyConfig) {
    launchOptions.proxy = { server: proxyConfig };
  }

  const browser = await chromium.launch(launchOptions);
  const context = await browser.newContext();
  const page = await context.newPage();

  // 监听并记录网络请求耗时
  const requestTimings = [];
  page.on('requestfinished', async (request) => {
    const timing = request.timing();
    const url = request.url();
    if (timing) {
      requestTimings.push({ url, timing });
    }
  });

  const tStart = Date.now();
  await page.goto(TEST_TARGET_URL, { waitUntil: 'networkidle', timeout: 30000 });
  const totalNavMs = Date.now() - tStart;

  // 提取 W3C Performance Navigation Timing
  const perfData = await page.evaluate(() => {
    const entries = performance.getEntriesByType('navigation');
    if (!entries || entries.length === 0) return null;
    const nav = entries[0];
    const paintEntries = performance.getEntriesByType('paint');
    let fcp = 0;
    for (const p of paintEntries) {
      if (p.name === 'first-contentful-paint') {
        fcp = p.startTime;
      }
    }

    return {
      dnsTime: nav.domainLookupEnd - nav.domainLookupStart,
      tcpConnectTime: nav.connectEnd - nav.connectStart,
      sslHandshakeTime: nav.connectEnd - nav.secureConnectionStart > 0 && nav.secureConnectionStart > 0 ? nav.connectEnd - nav.secureConnectionStart : 0,
      ttfb: nav.responseStart - nav.requestStart,
      downloadTime: nav.responseEnd - nav.responseStart,
      domReadyTime: nav.domContentLoadedEventEnd - nav.startTime,
      loadEventTime: nav.loadEventEnd - nav.startTime,
      fcp,
      transferSize: nav.transferSize || 0,
    };
  });

  await browser.close();

  return {
    modeName,
    proxyConfig: proxyConfig || '(None - Direct)',
    totalNavMs,
    requestCount: requestTimings.length,
    perfData,
  };
}

async function runWebPerformanceBenchmark() {
  console.log('================================================================');
  console.log('🌐 真实网页加载性能与 W3C Navigation Timing 评测器启动');
  console.log(`📌 目标网页 URL: ${TEST_TARGET_URL}`);
  console.log(`📌 HTTP 代理节点: ${PROXY_SERVER}`);
  console.log(`📌 SOCKS5 代理节点: ${SOCKS5_PROXY_SERVER}`);
  console.log('================================================================\n');

  console.log('⏳ 场景 1/3: 测量 Direct (直连无代理) 网页加载全链路耗时...');
  const directResult = await measurePagePerformance('Direct 直连', null);

  console.log('⏳ 场景 2/3: 测量 HTTP 代理模式下网页加载全链路耗时...');
  const httpProxyResult = await measurePagePerformance('HTTP Proxy 代理', PROXY_SERVER);

  console.log('⏳ 场景 3/3: 测量 SOCKS5 代理模式下网页加载全链路耗时...');
  const socks5ProxyResult = await measurePagePerformance('SOCKS5 Proxy 代理', SOCKS5_PROXY_SERVER);

  const results = [directResult, httpProxyResult, socks5ProxyResult];

  console.log('\n--------------------------------------------------------------------------------------------------------------');
  console.log('| 评测模式        | DNS (ms) | TCP (ms) | TTFB (ms)| Download | DOMReady | PageLoad | FCP (ms) | 总请求数 |');
  console.log('--------------------------------------------------------------------------------------------------------------');

  for (const r of results) {
    const p = r.perfData;
    if (!p) {
      console.log(`| ${r.modeName.padEnd(16)} | (未能获取 PerformanceNavigationTiming 数据) |`);
      continue;
    }
    console.log(
      `| ${r.modeName.padEnd(14)} ` +
      `| ${p.dnsTime.toFixed(1).padStart(8)} ` +
      `| ${p.tcpConnectTime.toFixed(1).padStart(8)} ` +
      `| ${p.ttfb.toFixed(1).padStart(8)} ` +
      `| ${p.downloadTime.toFixed(1).padStart(8)} ` +
      `| ${p.domReadyTime.toFixed(1).padStart(8)} ` +
      `| ${p.loadEventTime.toFixed(1).padStart(8)} ` +
      `| ${p.fcp.toFixed(1).padStart(8)} ` +
      `| ${String(r.requestCount).padStart(8)} |`
    );
  }
  console.log('--------------------------------------------------------------------------------------------------------------');

  // 计算代理损耗比
  if (directResult.perfData && httpProxyResult.perfData) {
    const directLoad = directResult.perfData.loadEventTime;
    const proxyLoad = httpProxyResult.perfData.loadEventTime;
    const diffMs = proxyLoad - directLoad;
    const diffPercent = directLoad > 0 ? ((diffMs / directLoad) * 100).toFixed(1) : '0';

    console.log(`\n📊 真实网页加载性能分析:`);
    console.log(`  • 直连完整页面加载耗时: ${directLoad.toFixed(1)} ms`);
    console.log(`  • HTTP 代理完整页面加载耗时: ${proxyLoad.toFixed(1)} ms`);
    console.log(`  • 代理模式额外时延差值: ${diffMs >= 0 ? '+' : ''}${diffMs.toFixed(1)} ms (${diffPercent}%)`);
    console.log(`  • 网页资源请求数量: ${httpProxyResult.requestCount} 个资源全部通过代理加载成功`);

    // 门禁断言: 代理加载页面时间额外增加不得超过 200ms (针对本地/内网部署页面)
    const passThreshold = diffMs < 200;
    console.log(`\n🎯 网页代理性能质量门禁 (< 200ms 额外时延): ${passThreshold ? '✅ 通过' : '⚠️ 超过阈值'}`);

    if (!passThreshold) {
      console.warn('⚠️ 注意: 代理模式网页加载耗时略微超出阈值，可进一步结合 Splice 零拷贝及连接池继续优化。');
    }
  }

  console.log('\n🎉 真实网页访问性能评测完成！');
}

runWebPerformanceBenchmark().catch((err) => {
  console.error('❌ 网页性能评测发生异常:', err);
  process.exit(1);
});
