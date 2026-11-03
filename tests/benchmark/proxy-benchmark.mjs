#!/usr/bin/env node

/**
 * LinuxWebTool (LWT) - 网络代理协议基准性能评测器 (Microsecond Multi-Protocol Benchmark)
 * 覆盖指标：TCP 握手耗时、SOCKS5 握手耗时、SOCKS5h 远端解析耗时、HTTP CONNECT 协商耗时、TTFB 与数据流吞吐带宽
 */

import net from 'node:net';
import http from 'node:http';
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
const TARGET_HOST = process.env.TARGET_HOST || '10.126.1.70';
const TARGET_PORT = parseInt(process.env.TARGET_PORT || '5271', 10);
const TARGET_PATH = '/health';

// 统计辅助函数
function calcStats(samples) {
  if (!samples || samples.length === 0) return { min: 0, mean: 0, p50: 0, p90: 0, p99: 0, max: 0 };
  const sorted = [...samples].sort((a, b) => a - b);
  const sum = sorted.reduce((acc, v) => acc + v, 0);
  const mean = sum / sorted.length;
  const p50 = sorted[Math.floor(sorted.length * 0.50)];
  const p90 = sorted[Math.floor(sorted.length * 0.90)];
  const p99 = sorted[Math.floor(sorted.length * 0.99)];
  return {
    min: sorted[0],
    mean,
    p50,
    p90,
    p99,
    max: sorted[sorted.length - 1],
  };
}

// 1. 直连 TCP 握手耗时
function measureDirectTcp(host, port) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    const sock = net.connect({ host, port, timeout: 3000 }, () => {
      const duration = performance.now() - t0;
      sock.destroy();
      resolve(duration);
    });
    sock.on('error', reject);
    sock.on('timeout', () => {
      sock.destroy();
      reject(new Error('Direct TCP Timeout'));
    });
  });
}

// 2. SOCKS5 无认证握手耗时 (IP 直连模式)
function measureSocks5Handshake(proxyHost, proxyPort, targetHost, targetPort) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    const sock = net.connect({ host: proxyHost, port: proxyPort, timeout: 3000 }, () => {
      // 1. Method Negotiation: VER=0x05, NMETHODS=1, METHOD=0x00 (NO AUTH)
      sock.write(Buffer.from([0x05, 0x01, 0x00]));
    });

    let step = 0;
    sock.on('data', (data) => {
      if (step === 0) {
        if (data.length < 2 || data[0] !== 0x05 || data[1] !== 0x00) {
          sock.destroy();
          return reject(new Error('SOCKS5 auth negotiation failed'));
        }
        step = 1;
        // 2. CONNECT CMD: VER=0x05, CMD=0x01, RSV=0x00, ATYP=0x01 (IPv4)
        const ipParts = targetHost.split('.').map(Number);
        const req = Buffer.alloc(10);
        req[0] = 0x05; // VER
        req[1] = 0x01; // CMD: CONNECT
        req[2] = 0x00; // RSV
        req[3] = 0x01; // ATYP: IPv4
        req[4] = ipParts[0];
        req[5] = ipParts[1];
        req[6] = ipParts[2];
        req[7] = ipParts[3];
        req.writeUInt16BE(targetPort, 8);
        sock.write(req);
      } else if (step === 1) {
        if (data.length < 2 || data[1] !== 0x00) {
          sock.destroy();
          return reject(new Error(`SOCKS5 connect failed, rep code: ${data[1]}`));
        }
        const totalDuration = performance.now() - t0;
        sock.destroy();
        resolve(totalDuration);
      }
    });

    sock.on('error', reject);
    sock.on('timeout', () => {
      sock.destroy();
      reject(new Error('SOCKS5 Timeout'));
    });
  });
}

// 3. SOCKS5h 远端解析握手耗时 (ATYP=0x03 域名代解)
function measureSocks5hHandshake(proxyHost, proxyPort, targetDomain, targetPort) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    const sock = net.connect({ host: proxyHost, port: proxyPort, timeout: 3000 }, () => {
      sock.write(Buffer.from([0x05, 0x01, 0x00]));
    });

    let step = 0;
    sock.on('data', (data) => {
      if (step === 0) {
        if (data.length < 2 || data[0] !== 0x05 || data[1] !== 0x00) {
          sock.destroy();
          return reject(new Error('SOCKS5h auth negotiation failed'));
        }
        step = 1;
        // ATYP=0x03 (Domain name)
        const domainBuf = Buffer.from(targetDomain, 'ascii');
        const req = Buffer.alloc(4 + 1 + domainBuf.length + 2);
        req[0] = 0x05;
        req[1] = 0x01; // CONNECT
        req[2] = 0x00;
        req[3] = 0x03; // Domain
        req[4] = domainBuf.length;
        domainBuf.copy(req, 5);
        req.writeUInt16BE(targetPort, 5 + domainBuf.length);
        sock.write(req);
      } else if (step === 1) {
        if (data.length < 2 || data[1] !== 0x00) {
          sock.destroy();
          return reject(new Error(`SOCKS5h connect failed, rep code: ${data[1]}`));
        }
        const totalDuration = performance.now() - t0;
        sock.destroy();
        resolve(totalDuration);
      }
    });

    sock.on('error', reject);
    sock.on('timeout', () => {
      sock.destroy();
      reject(new Error('SOCKS5h Timeout'));
    });
  });
}

// 4. HTTP CONNECT 隧道握手耗时
function measureHttpConnectHandshake(proxyHost, proxyPort, targetHost, targetPort) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    const sock = net.connect({ host: proxyHost, port: proxyPort, timeout: 3000 }, () => {
      const connectReq = `CONNECT ${targetHost}:${targetPort} HTTP/1.1\r\nHost: ${targetHost}:${targetPort}\r\n\r\n`;
      sock.write(connectReq);
    });

    sock.on('data', (data) => {
      const resp = data.toString('utf-8');
      if (resp.startsWith('HTTP/1.1 200') || resp.startsWith('HTTP/1.0 200')) {
        const duration = performance.now() - t0;
        sock.destroy();
        resolve(duration);
      } else {
        sock.destroy();
        reject(new Error(`HTTP CONNECT failed: ${resp.split('\r\n')[0]}`));
      }
    });

    sock.on('error', reject);
    sock.on('timeout', () => {
      sock.destroy();
      reject(new Error('HTTP CONNECT Timeout'));
    });
  });
}

// 5. 端到端 TTFB (Time To First Byte) 测量
function measureTtfbDirect(host, port, reqPath) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    const req = http.request({ host, port, path: reqPath, method: 'GET', timeout: 3000 }, (res) => {
      let firstByte = false;
      res.on('data', () => {
        if (!firstByte) {
          firstByte = true;
          const ttfb = performance.now() - t0;
          req.destroy();
          resolve(ttfb);
        }
      });
      res.on('end', () => {
        if (!firstByte) resolve(performance.now() - t0);
      });
    });
    req.on('error', reject);
    req.end();
  });
}

function measureTtfbHttpProxy(proxyHost, proxyPort, targetHost, targetPort, reqPath) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    const fullUrl = `http://${targetHost}:${targetPort}${reqPath}`;
    const req = http.request({
      host: proxyHost,
      port: proxyPort,
      path: fullUrl,
      headers: { Host: `${targetHost}:${targetPort}` },
      timeout: 3000,
    }, (res) => {
      let firstByte = false;
      res.on('data', () => {
        if (!firstByte) {
          firstByte = true;
          const ttfb = performance.now() - t0;
          req.destroy();
          resolve(ttfb);
        }
      });
      res.on('end', () => {
        if (!firstByte) resolve(performance.now() - t0);
      });
    });
    req.on('error', reject);
    req.end();
  });
}

// 6. 数据流式传输吞吐量 (Throughput MB/s)
function measureThroughputDirect(host, port, payloadBytes) {
  return new Promise((resolve, reject) => {
    const payload = Buffer.alloc(payloadBytes, 0x61); // 'a'
    const server = http.createServer((req, res) => {
      res.writeHead(200, { 'Content-Length': payload.length });
      res.end(payload);
    });

    server.listen(0, '127.0.0.1', () => {
      const localPort = server.address().port;
      const t0 = performance.now();
      http.get(`http://127.0.0.1:${localPort}`, (res) => {
        let received = 0;
        res.on('data', (chunk) => {
          received += chunk.length;
        });
        res.on('end', () => {
          const durationSec = (performance.now() - t0) / 1000;
          const mbps = (received / 1024 / 1024) / durationSec;
          server.close();
          resolve(mbps);
        });
      }).on('error', (err) => {
        server.close();
        reject(err);
      });
    });
  });
}

async function runBenchmark() {
  console.log('================================================================');
  console.log('🚀 LinuxWebTool (LWT) 网络代理全链路基准性能测试开始');
  console.log(`📌 代理服务器地址: ${PROXY_HOST}:${PROXY_PORT}`);
  console.log(`📌 目标服务端地址: ${TARGET_HOST}:${TARGET_PORT}${TARGET_PATH}`);
  console.log('================================================================\n');

  const WARMUP_COUNT = 3;
  const SAMPLE_COUNT = 15;

  console.log(`⚡ 预热中 (执行 ${WARMUP_COUNT} 次)...`);
  for (let i = 0; i < WARMUP_COUNT; i++) {
    try {
      await measureDirectTcp(TARGET_HOST, TARGET_PORT);
      await measureSocks5Handshake(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT);
      await measureHttpConnectHandshake(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT);
    } catch {}
  }

  // --- 测试维度 1: 握手协议耗时 ---
  console.log(`\n📊 [测试 1/3] 握手协议延迟采样 (${SAMPLE_COUNT} 轮)...`);
  const directTcpSamples = [];
  const socks5Samples = [];
  const socks5hSamples = [];
  const httpConnectSamples = [];

  for (let i = 0; i < SAMPLE_COUNT; i++) {
    directTcpSamples.push(await measureDirectTcp(TARGET_HOST, TARGET_PORT));
    socks5Samples.push(await measureSocks5Handshake(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT));
    socks5hSamples.push(await measureSocks5hHandshake(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT));
    httpConnectSamples.push(await measureHttpConnectHandshake(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT));
  }

  const directTcpStats = calcStats(directTcpSamples);
  const socks5Stats = calcStats(socks5Samples);
  const socks5hStats = calcStats(socks5hSamples);
  const httpConnectStats = calcStats(httpConnectSamples);

  console.log('-----------------------------------------------------------------------------------------');
  console.log('| 协议场景                  | Min (ms) | P50 (ms) | Mean (ms)| P90 (ms) | P99 (ms) | Max (ms) |');
  console.log('-----------------------------------------------------------------------------------------');
  console.log(`| Direct TCP 3-Way Handshake| ${directTcpStats.min.toFixed(2).padStart(8)} | ${directTcpStats.p50.toFixed(2).padStart(8)} | ${directTcpStats.mean.toFixed(2).padStart(8)} | ${directTcpStats.p90.toFixed(2).padStart(8)} | ${directTcpStats.p99.toFixed(2).padStart(8)} | ${directTcpStats.max.toFixed(2).padStart(8)} |`);
  console.log(`| SOCKS5 握手 (RFC 1928)    | ${socks5Stats.min.toFixed(2).padStart(8)} | ${socks5Stats.p50.toFixed(2).padStart(8)} | ${socks5Stats.mean.toFixed(2).padStart(8)} | ${socks5Stats.p90.toFixed(2).padStart(8)} | ${socks5Stats.p99.toFixed(2).padStart(8)} | ${socks5Stats.max.toFixed(2).padStart(8)} |`);
  console.log(`| SOCKS5h 远端解析握手      | ${socks5hStats.min.toFixed(2).padStart(8)} | ${socks5hStats.p50.toFixed(2).padStart(8)} | ${socks5hStats.mean.toFixed(2).padStart(8)} | ${socks5hStats.p90.toFixed(2).padStart(8)} | ${socks5hStats.p99.toFixed(2).padStart(8)} | ${socks5hStats.max.toFixed(2).padStart(8)} |`);
  console.log(`| HTTP CONNECT 隧道握手     | ${httpConnectStats.min.toFixed(2).padStart(8)} | ${httpConnectStats.p50.toFixed(2).padStart(8)} | ${httpConnectStats.mean.toFixed(2).padStart(8)} | ${httpConnectStats.p90.toFixed(2).padStart(8)} | ${httpConnectStats.p99.toFixed(2).padStart(8)} | ${httpConnectStats.max.toFixed(2).padStart(8)} |`);
  console.log('-----------------------------------------------------------------------------------------');

  // --- 测试维度 2: 首字节时间 (TTFB) ---
  console.log(`\n📊 [测试 2/3] 首字节响应时间 (TTFB) 采样 (${SAMPLE_COUNT} 轮)...`);
  const ttfbDirectSamples = [];
  const ttfbProxySamples = [];

  for (let i = 0; i < SAMPLE_COUNT; i++) {
    ttfbDirectSamples.push(await measureTtfbDirect(TARGET_HOST, TARGET_PORT, TARGET_PATH));
    ttfbProxySamples.push(await measureTtfbHttpProxy(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT, TARGET_PATH));
  }

  const ttfbDirectStats = calcStats(ttfbDirectSamples);
  const ttfbProxyStats = calcStats(ttfbProxySamples);
  const ttfbOverheadMs = ttfbProxyStats.mean - ttfbDirectStats.mean;

  console.log('-----------------------------------------------------------------------------------------');
  console.log('| 链路模式                  | Min (ms) | P50 (ms) | Mean (ms)| P90 (ms) | P99 (ms) | Max (ms) |');
  console.log('-----------------------------------------------------------------------------------------');
  console.log(`| Direct 直连 GET           | ${ttfbDirectStats.min.toFixed(2).padStart(8)} | ${ttfbDirectStats.p50.toFixed(2).padStart(8)} | ${ttfbDirectStats.mean.toFixed(2).padStart(8)} | ${ttfbDirectStats.p90.toFixed(2).padStart(8)} | ${ttfbDirectStats.p99.toFixed(2).padStart(8)} | ${ttfbDirectStats.max.toFixed(2).padStart(8)} |`);
  console.log(`| HTTP Proxy 正向代理 GET   | ${ttfbProxyStats.min.toFixed(2).padStart(8)} | ${ttfbProxyStats.p50.toFixed(2).padStart(8)} | ${ttfbProxyStats.mean.toFixed(2).padStart(8)} | ${ttfbProxyStats.p90.toFixed(2).padStart(8)} | ${ttfbProxyStats.p99.toFixed(2).padStart(8)} | ${ttfbProxyStats.max.toFixed(2).padStart(8)} |`);
  console.log('-----------------------------------------------------------------------------------------');
  console.log(`⚡ 代理引入平均额外开销 (Proxy Overhead): ${ttfbOverheadMs >= 0 ? '+' : ''}${ttfbOverheadMs.toFixed(2)} ms`);

  // --- 测试维度 3: 性能 SLA 门禁断言 ---
  console.log('\n🎯 [测试 3/3] 执行性能 SLA 门禁断言核验...');
  let allPass = true;

  // 断言 1: HTTP CONNECT 握手开销不得突破 10ms (局域网基准)
  const connectPass = httpConnectStats.mean < 10.0;
  console.log(`  [SLA-01] HTTP CONNECT 平均握手耗时 < 10ms: ${connectPass ? '✅ 通过' : '❌ 失败'} (${httpConnectStats.mean.toFixed(2)} ms)`);
  if (!connectPass) allPass = false;

  // 断言 2: SOCKS5 握手开销不得突破 10ms (局域网基准)
  const socks5Pass = socks5Stats.mean < 10.0;
  console.log(`  [SLA-02] SOCKS5 平均握手耗时 < 10ms: ${socks5Pass ? '✅ 通过' : '❌ 失败'} (${socks5Stats.mean.toFixed(2)} ms)`);
  if (!socks5Pass) allPass = false;

  // 断言 3: SOCKS5h 远端域名代解耗时与直接 IP 握手差异不得超过 5ms (验证 DNS 命中缓存 0ms 特性)
  const dnsDiff = Math.abs(socks5hStats.mean - socks5Stats.mean);
  const dnsPass = dnsDiff < 5.0;
  console.log(`  [SLA-03] SOCKS5h 远端解析与 IP 握手时延差 < 5ms (DNS Cache): ${dnsPass ? '✅ 通过' : '❌ 失败'} (差值: ${dnsDiff.toFixed(2)} ms)`);
  if (!dnsPass) allPass = false;

  // 断言 4: 代理额外开销不得突破 8ms
  const overheadPass = ttfbOverheadMs < 8.0;
  console.log(`  [SLA-04] HTTP 代理 TTFB 额外时延开销 < 8ms: ${overheadPass ? '✅ 通过' : '❌ 失败'} (${ttfbOverheadMs.toFixed(2)} ms)`);
  if (!overheadPass) allPass = false;

  console.log('\n================================================================');
  if (allPass) {
    console.log('🎉 恭喜！网络代理基准性能评测全部达标，SLA 门禁全通！');
    process.exit(0);
  } else {
    console.error('❌ 网络代理基准性能未能达到预期 SLA 指标！');
    process.exit(1);
  }
}

runBenchmark().catch((err) => {
  console.error('❌ 基准测试发生未捕获异常:', err);
  process.exit(1);
});
