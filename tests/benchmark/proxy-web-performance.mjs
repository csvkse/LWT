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
const PAGE_SAMPLES = Math.max(1, Number.parseInt(process.env.BENCH_BROWSER_SAMPLES || '5', 10));
const PROXY_USERNAME = process.env.SOCKS5_USERNAME || '';
const PROXY_PASSWORD = process.env.SOCKS5_PASSWORD || '';
if (Boolean(PROXY_USERNAME) !== Boolean(PROXY_PASSWORD)) {
  throw new Error('Set both SOCKS5_USERNAME and SOCKS5_PASSWORD, or neither.');
}

function percentile(values, p) {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil(p * sorted.length) - 1)];
}

function shuffled(items) {
  const result = [...items];
  for (let i = result.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [result[i], result[j]] = [result[j], result[i]];
  }
  return result;
}

async function measurePagePerformance(modeName, proxyConfig) {
  const launchOptions = {
    channel: 'chrome',
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage'],
  };

  if (proxyConfig) {
    launchOptions.proxy = {
      server: proxyConfig,
      ...(PROXY_USERNAME ? { username: PROXY_USERNAME, password: PROXY_PASSWORD } : {}),
    };
  }

  const browser = await chromium.launch(launchOptions);
  const context = await browser.newContext();
  const page = await context.newPage();

  // 监听并记录网络请求耗时
  let requestCount = 0;
  let failedRequestCount = 0;
  const responseStatuses = {};
  page.on('requestfinished', () => { requestCount++; });
  page.on('requestfailed', () => { failedRequestCount++; });
  page.on('response', (response) => {
    const status = String(response.status());
    responseStatuses[status] = (responseStatuses[status] || 0) + 1;
  });

  try {
  const tStart = Date.now();
  await page.goto(TEST_TARGET_URL, { waitUntil: 'networkidle', timeout: 30000 });
  const totalNavMs = Date.now() - tStart;
  const browserVersion = browser.version();

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

  return {
    modeName,
    proxyConfig: proxyConfig || '(None - Direct)',
    totalNavMs,
    requestCount,
    failedRequestCount,
    responseStatuses,
    browserVersion,
    perfData,
  };
  } finally {
    await browser.close();
  }
}

async function measureRepeatedPagePerformance(modeName, proxyConfig, collectedSamples = null) {
  const samples = collectedSamples ?? [];
  if (!collectedSamples) {
    for (let i = 0; i < PAGE_SAMPLES; i++) {
      try { samples.push(await measurePagePerformance(modeName, proxyConfig)); }
      catch { console.warn(`  ${modeName} sample ${i + 1}/${PAGE_SAMPLES} failed`); }
    }
  }
  const metricNames = ['dnsTime', 'tcpConnectTime', 'sslHandshakeTime', 'ttfb', 'downloadTime', 'domReadyTime', 'loadEventTime', 'fcp', 'transferSize'];
  const validSamples = samples.filter((s) => s.perfData);
  const perfData = {};
  for (const metric of metricNames) perfData[metric] = percentile(validSamples.map((s) => s.perfData[metric]), 0.5);
  const aggregatedStatuses = {};
  for (const sample of samples) {
    for (const [status, count] of Object.entries(sample.responseStatuses || {})) {
      aggregatedStatuses[status] = (aggregatedStatuses[status] || 0) + count;
    }
  }
  return {
    modeName,
    proxyConfig: proxyConfig || '(None - Direct)',
    perfData: validSamples.length ? perfData : null,
    totalNavMs: percentile(samples.map((s) => s.totalNavMs), 0.5),
    requestCount: samples.length ? percentile(samples.map((s) => s.requestCount), 0.5) : 0,
    failedRequestCount: samples.length ? percentile(samples.map((s) => s.failedRequestCount), 0.5) : 0,
    responseStatuses: aggregatedStatuses,
    browserVersion: samples[0]?.browserVersion || 'unavailable',
    successes: validSamples.length,
    attempts: PAGE_SAMPLES,
  };
}

async function runWebPerformanceBenchmark() {
  console.log('================================================================');
  console.log('🌐 真实网页加载性能与 W3C Navigation Timing 评测器启动');
  console.log(`📌 目标网页 URL: ${TEST_TARGET_URL}`);
  console.log(`📌 HTTP 代理节点: ${PROXY_SERVER}`);
  console.log(`📌 SOCKS5 代理节点: ${SOCKS5_PROXY_SERVER}`);
  console.log(`📌 每模式浏览器采样: ${PAGE_SAMPLES} (冷启动新浏览器；结果汇总 p50)`);
  console.log(`📌 Runtime: Node ${process.version} (${process.platform}/${process.arch}); browser versions are reported per mode`);
  console.log('================================================================\n');

  const pageScenarios = [
    ['Direct 直连', null],
    ['HTTP Proxy 代理', PROXY_SERVER],
    ['SOCKS5 Proxy 代理', SOCKS5_PROXY_SERVER],
  ];
  const pageSamples = new Map(pageScenarios.map(([mode]) => [mode, []]));
  for (let round = 0; round < PAGE_SAMPLES; round++) {
    console.log(`⏳ 浏览器采样 ${round + 1}/${PAGE_SAMPLES}: 按随机顺序测量 Direct/HTTP/SOCKS5`);
    for (const [mode, proxy] of shuffled(pageScenarios)) {
      try { pageSamples.get(mode).push(await measurePagePerformance(mode, proxy)); }
      catch { console.warn(`  ${mode} sample ${round + 1}/${PAGE_SAMPLES} failed`); }
    }
  }

  const directResult = await measureRepeatedPagePerformance('Direct 直连', null, pageSamples.get('Direct 直连'));
  const httpProxyResult = await measureRepeatedPagePerformance('HTTP Proxy 代理', PROXY_SERVER, pageSamples.get('HTTP Proxy 代理'));
  const socks5ProxyResult = await measureRepeatedPagePerformance('SOCKS5 Proxy 代理', SOCKS5_PROXY_SERVER, pageSamples.get('SOCKS5 Proxy 代理'));

  const results = [directResult, httpProxyResult, socks5ProxyResult];

  console.log('\n--------------------------------------------------------------------------------------------------------------');
  console.log('| 评测模式        | 成功/计划 | DNS | TCP | TLS | TTFB | Content Download | DOMReady | PageLoad | FCP | 请求(p50) | 失败(p50) | HTTP 状态合计 | Browser |');
  console.log('--------------------------------------------------------------------------------------------------------------');

  for (const r of results) {
    const p = r.perfData;
    if (!p) {
      console.log(`| ${r.modeName.padEnd(16)} | (未能获取 PerformanceNavigationTiming 数据) |`);
      continue;
    }
    console.log(
      `| ${r.modeName.padEnd(14)} ` +
      `| ${String(r.successes).padStart(2)}/${r.attempts} ` +
      `| ${p.dnsTime.toFixed(1).padStart(8)} ` +
      `| ${p.tcpConnectTime.toFixed(1).padStart(8)} ` +
      `| ${p.sslHandshakeTime.toFixed(1).padStart(8)} ` +
      `| ${p.ttfb.toFixed(1).padStart(8)} ` +
      `| ${p.downloadTime.toFixed(1).padStart(8)} ` +
      `| ${p.domReadyTime.toFixed(1).padStart(8)} ` +
      `| ${p.loadEventTime.toFixed(1).padStart(8)} ` +
      `| ${p.fcp.toFixed(1).padStart(8)} ` +
      `| ${String(r.requestCount).padStart(8)} ` +
      `| ${String(r.failedRequestCount).padStart(8)} ` +
      `| ${JSON.stringify(r.responseStatuses)} ` +
      `| ${r.browserVersion} |`
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
    console.log(`  • HTTP 代理完成请求数中位数: ${httpProxyResult.requestCount}; 失败请求中位数: ${httpProxyResult.failedRequestCount}`);

    console.log('\n📌 此差值是每模式冷启动浏览器导航 p50 对照，不作为固定 SLA；加载时间受目标服务和网络路径影响。');
  }

  console.log('\n🎉 真实网页访问性能评测完成！');
  if (results.some((r) => r.successes < r.attempts)) process.exitCode = 1;
}

runWebPerformanceBenchmark().catch((err) => {
  console.error('❌ 网页性能评测发生异常:', err);
  process.exit(1);
});
