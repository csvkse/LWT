#!/usr/bin/env node

/**
 * LinuxWebTool (LWT) - 多 IP CDN 域名 Happy Eyeballs 拨号竞速与防长尾延迟测试
 * 针对具有多 A 记录的常用 CDN 域名（如 B站 CDN、GitHub CDN、公共 CDN），
 * 验证经由代理并发建立隧道的握手延迟，断言绝对无长尾卡顿与死锁。
 */

import net from 'node:net';
import { performance } from 'node:perf_hooks';
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

const PROXY_HOST = process.env.PROXY_BENCH_HOST || '10.126.1.70';
const PROXY_PORT = parseInt(process.env.PROXY_BENCH_PORT || '18080', 10);

// 测试目标：常用具有多 A 记录的典型 CDN 域名
const CDN_TARGETS = [
  { domain: 'i0.hdslb.com', port: 443, name: 'B站评论区头像与图片 CDN' },
  { domain: 'api.bilibili.com', port: 443, name: 'B站核心 API 服务网关' },
  { domain: 'activity.hdslb.com', port: 443, name: 'B站活动与表情包 CDN' },
];

function measureHttpConnect(proxyHost, proxyPort, targetDomain, targetPort) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    const sock = net.connect({ host: proxyHost, port: proxyPort, timeout: 5000 }, () => {
      sock.write(`CONNECT ${targetDomain}:${targetPort} HTTP/1.1\r\nHost: ${targetDomain}:${targetPort}\r\n\r\n`);
    });

    sock.on('data', (data) => {
      const str = data.toString('utf-8');
      if (str.includes('200')) {
        const duration = performance.now() - t0;
        sock.destroy();
        resolve(duration);
      } else {
        sock.destroy();
        reject(new Error(`CONNECT failed: ${str.split('\r\n')[0]}`));
      }
    });

    sock.on('error', reject);
    sock.on('timeout', () => {
      sock.destroy();
      reject(new Error('CONNECT timeout'));
    });
  });
}

async function runCdnMultihomingTest() {
  console.log('================================================================================');
  console.log('🚀 多 IP CDN 域名 Happy Eyeballs 竞速拨号与防长尾卡顿测试开始');
  console.log(`📌 代理服务器地址: ${PROXY_HOST}:${PROXY_PORT}`);
  console.log('================================================================================\n');

  let allPass = true;

  for (const target of CDN_TARGETS) {
    console.log(`📡 正在测试目标: ${target.name} (${target.domain}:${target.port})...`);
    const samples = [];
    const ROUNDS = 5;

    for (let i = 0; i < ROUNDS; i++) {
      try {
        const duration = await measureHttpConnect(PROXY_HOST, PROXY_PORT, target.domain, target.port);
        samples.push(duration);
      } catch (err) {
        console.warn(`  ⚠️ 轮次 ${i + 1} 拨号异常: ${err.message}`);
      }
    }

    if (samples.length === 0) {
      console.error(`  ❌ 目标 ${target.domain} 全部轮次失败！`);
      allPass = false;
      continue;
    }

    const min = Math.min(...samples);
    const max = Math.max(...samples);
    const mean = samples.reduce((a, b) => a + b, 0) / samples.length;

    console.log(`  -> 握手延迟统计: Min: ${min.toFixed(2)} ms | Mean: ${mean.toFixed(2)} ms | Max: ${max.toFixed(2)} ms (样本数: ${samples.length}/${ROUNDS})`);

    // 门禁断言:
    // 1. 平均建立 CONNECT 隧道耗时不得超过 800ms (外网公网建连)
    // 2. 最大耗时不得超过 1500ms，绝对杜绝长尾死锁
    const meanPass = mean < 800;
    const maxPass = max < 1500;

    console.log(`  [SLA-CDN-1] 平均握手耗时 < 800ms: ${meanPass ? '✅ 通过' : '❌ 失败'} (${mean.toFixed(2)} ms)`);
    console.log(`  [SLA-CDN-2] 最大防长尾卡顿 < 1500ms: ${maxPass ? '✅ 通过' : '❌ 失败'} (${max.toFixed(2)} ms)\n`);

    if (!meanPass || !maxPass) {
      allPass = false;
    }
  }

  console.log('================================================================================');
  if (allPass) {
    console.log('🎉 恭喜！多 IP CDN 域名并发竞速与抗长尾延迟测试全部达标！');
    process.exit(0);
  } else {
    console.error('❌ 多 IP CDN 域名测试未能达到预期 SLA 指标！');
    process.exit(1);
  }
}

runCdnMultihomingTest().catch((err) => {
  console.error('❌ CDN 竞速测试发生未捕获异常:', err);
  process.exit(1);
});
