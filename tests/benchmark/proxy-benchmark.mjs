#!/usr/bin/env node

/**
 * LinuxWebTool (LWT) - 网络代理协议基准性能评测器 (Microsecond Multi-Protocol Benchmark)
 * 覆盖指标：TCP 建连耗时、SOCKS5 CONNECT、可选 SOCKS5h DNS、HTTP CONNECT 与 HTTP GET TTFB
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
const configuredSocks5hTarget = (process.env.SOCKS5H_TARGET_HOST || TARGET_HOST).trim();
const SOCKS5H_TARGET_HOST = net.isIP(configuredSocks5hTarget) === 0 ? configuredSocks5hTarget : '';
const TARGET_PORT = parseInt(process.env.TARGET_PORT || '5271', 10);
const TARGET_PATH = '/health';
const SAMPLE_COUNT = Math.max(5, Number.parseInt(process.env.BENCH_SAMPLES || '30', 10));
const WARMUP_COUNT = Math.max(0, Number.parseInt(process.env.BENCH_WARMUPS || '3', 10));
const SOCKS5_USERNAME = process.env.SOCKS5_USERNAME || '';
const SOCKS5_PASSWORD = process.env.SOCKS5_PASSWORD || '';
if (Boolean(SOCKS5_USERNAME) !== Boolean(SOCKS5_PASSWORD)) {
  throw new Error('Set both SOCKS5_USERNAME and SOCKS5_PASSWORD, or neither.');
}

// 统计辅助函数
function calcStats(samples) {
  if (!samples || samples.length === 0) return { min: 0, mean: 0, p50: 0, p90: 0, p99: 0, max: 0 };
  const sorted = [...samples].sort((a, b) => a - b);
  const sum = sorted.reduce((acc, v) => acc + v, 0);
  const mean = sum / sorted.length;
  const percentile = (p) => sorted[Math.min(sorted.length - 1, Math.ceil(p * sorted.length) - 1)];
  const p50 = percentile(0.50);
  const p90 = percentile(0.90);
  const p99 = percentile(0.99);
  return {
    min: sorted[0],
    mean,
    p50,
    p90,
    p99,
    max: sorted[sorted.length - 1],
  };
}

function shuffled(items) {
  const result = [...items];
  for (let i = result.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [result[i], result[j]] = [result[j], result[i]];
  }
  return result;
}

const pendingSocketBytes = new WeakMap();

function waitForConnect(sock, timeoutMs = 5000) {
  return new Promise((resolve, reject) => {
    const cleanup = () => { clearTimeout(timer); sock.off('connect', onConnect); sock.off('error', onError); };
    const onConnect = () => { cleanup(); resolve(); };
    const onError = (err) => { cleanup(); reject(err); };
    const timer = setTimeout(() => {
      cleanup();
      sock.destroy();
      reject(new Error('proxy TCP connect timeout'));
    }, timeoutMs);
    sock.once('connect', onConnect);
    sock.once('error', onError);
  });
}

function readExactly(sock, length) {
  return new Promise((resolve, reject) => {
    let data = pendingSocketBytes.get(sock) ?? Buffer.alloc(0);
    pendingSocketBytes.delete(sock);
    const cleanup = () => { clearTimeout(timer); sock.off('data', onData); sock.off('error', onError); sock.off('close', onClose); };
    const finish = () => {
      cleanup();
      if (data.length > length) pendingSocketBytes.set(sock, data.subarray(length));
      resolve(data.subarray(0, length));
    };
    const onData = (chunk) => {
      data = Buffer.concat([data, chunk]);
      if (data.length >= length) finish();
    };
    const onError = (err) => { cleanup(); reject(err); };
    const onClose = () => { cleanup(); reject(new Error('connection closed before complete reply')); };
    const timer = setTimeout(() => { cleanup(); reject(new Error('protocol reply timeout')); }, 5000);
    if (data.length >= length) { finish(); return; }
    sock.on('data', onData); sock.once('error', onError); sock.once('close', onClose);
  });
}

async function measureSocksConnect(proxyHost, proxyPort, targetHost, targetPort, remoteDns) {
  const started = performance.now();
  const sock = net.connect({ host: proxyHost, port: proxyPort, timeout: 5000 });
  try {
    await waitForConnect(sock);
    sock.write(Buffer.from(SOCKS5_USERNAME
      ? [0x05, 0x02, 0x00, 0x02]
      : [0x05, 0x01, 0x00]));
    const greeting = await readExactly(sock, 2);
    if (greeting[0] !== 5) throw new Error('invalid SOCKS5 method negotiation response');
    if (greeting[1] === 0x02) {
      const username = Buffer.from(SOCKS5_USERNAME, 'utf8');
      const password = Buffer.from(SOCKS5_PASSWORD, 'utf8');
      if (!username.length || username.length > 255 || !password.length || password.length > 255) {
        throw new Error('SOCKS5 credentials must each contain 1..255 UTF-8 bytes');
      }
      sock.write(Buffer.concat([Buffer.from([0x01, username.length]), username, Buffer.from([password.length]), password]));
      const authReply = await readExactly(sock, 2);
      if (authReply[0] !== 0x01 || authReply[1] !== 0x00) throw new Error('SOCKS5 username/password authentication rejected');
    } else if (greeting[1] !== 0x00) {
      throw new Error(`SOCKS5 authentication method ${greeting[1]} does not match supplied credentials`);
    }
    let address;
    if (remoteDns) {
      const domain = Buffer.from(targetHost, 'ascii');
      if (domain.length < 1 || domain.length > 255) throw new Error('SOCKS5 domain length must be 1..255 bytes');
      address = Buffer.concat([Buffer.from([3, domain.length]), domain]);
    } else {
      if (net.isIP(targetHost) !== 4) throw new Error('IP-mode target must be IPv4');
      address = Buffer.from([1, ...targetHost.split('.').map(Number)]);
    }
    const port = Buffer.alloc(2); port.writeUInt16BE(targetPort);
    sock.write(Buffer.concat([Buffer.from([5, 1, 0]), address, port]));
    const reply = await readExactly(sock, 4);
    if (reply[0] !== 5 || reply[2] !== 0) throw new Error('invalid SOCKS5 CONNECT response');
    if (reply[1] !== 0) throw new Error(`SOCKS5 CONNECT rejected (REP=${reply[1]})`);
    const boundLength = reply[3] === 1 ? 4 : reply[3] === 4 ? 16 : reply[3] === 3 ? (await readExactly(sock, 1))[0] : -1;
    if (boundLength < 0) throw new Error(`unsupported SOCKS5 bound ATYP ${reply[3]}`);
    await readExactly(sock, boundLength + 2);
    return performance.now() - started;
  } finally { sock.destroy(); }
}

async function measureHttpConnect(proxyHost, proxyPort, targetHost, targetPort) {
  const started = performance.now();
  const sock = net.connect({ host: proxyHost, port: proxyPort, timeout: 5000 });
  try {
    await waitForConnect(sock);
    const proxyAuthorization = SOCKS5_USERNAME
      ? `Proxy-Authorization: Basic ${Buffer.from(`${SOCKS5_USERNAME}:${SOCKS5_PASSWORD}`).toString('base64')}\r\n`
      : '';
    sock.write(`CONNECT ${targetHost}:${targetPort} HTTP/1.1\r\nHost: ${targetHost}:${targetPort}\r\n${proxyAuthorization}Connection: close\r\n\r\n`);
    const status = await new Promise((resolve, reject) => {
      let data = Buffer.alloc(0);
      const cleanup = () => { clearTimeout(timer); sock.off('data', onData); sock.off('error', onError); };
      const onData = (chunk) => { data = Buffer.concat([data, chunk]); const end = data.indexOf('\r\n\r\n'); if (end >= 0) { cleanup(); resolve(data.subarray(0, end).toString('latin1').split('\r\n', 1)[0]); } };
      const onError = (err) => { cleanup(); reject(err); };
      const timer = setTimeout(() => { cleanup(); reject(new Error('HTTP CONNECT response timeout')); }, 5000);
      sock.on('data', onData); sock.once('error', onError);
    });
    if (!/^HTTP\/1\.[01] 200(?: |$)/.test(status)) throw new Error(`HTTP CONNECT rejected (${status})`);
    return performance.now() - started;
  } finally { sock.destroy(); }
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
function measureSocks5HandshakeLegacy(proxyHost, proxyPort, targetHost, targetPort) {
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
function measureSocks5hHandshakeLegacy(proxyHost, proxyPort, targetDomain, targetPort) {
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
function measureHttpConnectHandshakeLegacy(proxyHost, proxyPort, targetHost, targetPort) {
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
function measureSocks5Handshake(proxyHost, proxyPort, targetHost, targetPort) {
  return measureSocksConnect(proxyHost, proxyPort, targetHost, targetPort, false);
}

function measureSocks5hHandshake(proxyHost, proxyPort, targetDomain, targetPort) {
  return measureSocksConnect(proxyHost, proxyPort, targetDomain, targetPort, true);
}

function measureHttpConnectHandshake(proxyHost, proxyPort, targetHost, targetPort) {
  return measureHttpConnect(proxyHost, proxyPort, targetHost, targetPort);
}

function measureTtfbDirect(host, port, reqPath) {
  return new Promise((resolve, reject) => {
    const t0 = performance.now();
    const req = http.request({ host, port, path: reqPath, method: 'GET', timeout: 3000 }, (res) => {
      if (res.statusCode < 200 || res.statusCode >= 300) {
        req.destroy();
        return reject(new Error(`Direct GET returned HTTP ${res.statusCode}`));
      }
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
    req.setTimeout(3000, () => req.destroy(new Error('Direct GET timeout')));
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
      headers: {
        Host: `${targetHost}:${targetPort}`,
        ...(SOCKS5_USERNAME ? { 'Proxy-Authorization': `Basic ${Buffer.from(`${SOCKS5_USERNAME}:${SOCKS5_PASSWORD}`).toString('base64')}` } : {}),
      },
      timeout: 3000,
    }, (res) => {
      if (res.statusCode < 200 || res.statusCode >= 300) {
        req.destroy();
        return reject(new Error(`HTTP proxy GET returned HTTP ${res.statusCode}`));
      }
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
    req.setTimeout(3000, () => req.destroy(new Error('HTTP proxy GET timeout')));
    req.on('error', reject);
    req.end();
  });
}

async function runBenchmark() {
  console.log('================================================================');
  console.log('🚀 LinuxWebTool (LWT) 网络代理全链路基准性能测试开始');
  console.log(`📌 代理服务器地址: ${PROXY_HOST}:${PROXY_PORT}`);
  console.log(`📌 目标服务端地址: ${TARGET_HOST}:${TARGET_PORT}${TARGET_PATH}`);
  console.log(`📌 SOCKS5h DNS 目标: ${SOCKS5H_TARGET_HOST || '跳过（请设置 SOCKS5H_TARGET_HOST 为域名）'}`);
  console.log('================================================================\n');

  console.log(`⚡ 预热中 (执行 ${WARMUP_COUNT} 次)...`);
  for (let i = 0; i < WARMUP_COUNT; i++) {
    try {
      await measureDirectTcp(TARGET_HOST, TARGET_PORT);
      await measureSocks5Handshake(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT);
      if (SOCKS5H_TARGET_HOST) {
        await measureSocks5hHandshake(PROXY_HOST, PROXY_PORT, SOCKS5H_TARGET_HOST, TARGET_PORT);
      }
      await measureHttpConnectHandshake(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT);
    } catch {}
  }

  // End-to-end connect/tunnel timing includes client-proxy RTT and proxy upstream connect.
  console.log(`\n📊 [测试 1/3] 端到端建连/代理隧道耗时采样 (${SAMPLE_COUNT} 轮)...`);
  const directTcpSamples = [];
  const socks5Samples = [];
  const socks5hSamples = [];
  const httpConnectSamples = [];

  const scenarios = [
    ['direct', () => measureDirectTcp(TARGET_HOST, TARGET_PORT), directTcpSamples],
    ['socks5', () => measureSocks5Handshake(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT), socks5Samples],
    ['http-connect', () => measureHttpConnectHandshake(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT), httpConnectSamples],
  ];
  if (SOCKS5H_TARGET_HOST) {
    scenarios.push(['socks5h', () => measureSocks5hHandshake(PROXY_HOST, PROXY_PORT, SOCKS5H_TARGET_HOST, TARGET_PORT), socks5hSamples]);
  }
  for (let i = 0; i < SAMPLE_COUNT; i++) {
    for (const [name, measure, samples] of shuffled(scenarios)) {
      try { samples.push(await measure()); }
      catch (err) { console.warn(`  ${name} sample ${i + 1}/${SAMPLE_COUNT} failed: ${err.message}`); }
    }
  }

  const directTcpStats = calcStats(directTcpSamples);
  const socks5Stats = calcStats(socks5Samples);
  const socks5hStats = calcStats(socks5hSamples);
  const httpConnectStats = calcStats(httpConnectSamples);

  console.log('-----------------------------------------------------------------------------------------');
  console.log('| 场景 (含网络往返/上游建连) | 成功/计划 | P50 (ms) | P90 (ms) | P99 (ms) |');
  console.log('-----------------------------------------------------------------------------------------');
  console.log(`| Direct TCP connect         | ${directTcpSamples.length}/${SAMPLE_COUNT} | ${directTcpStats.p50.toFixed(2)} | ${directTcpStats.p90.toFixed(2)} | ${directTcpStats.p99.toFixed(2)} |`);
  console.log(`| SOCKS5 CONNECT (IP)        | ${socks5Samples.length}/${SAMPLE_COUNT} | ${socks5Stats.p50.toFixed(2)} | ${socks5Stats.p90.toFixed(2)} | ${socks5Stats.p99.toFixed(2)} |`);
  if (SOCKS5H_TARGET_HOST) {
    console.log(`| SOCKS5 CONNECT (DNS domain) | ${socks5hSamples.length}/${SAMPLE_COUNT} | ${socks5hStats.p50.toFixed(2)} | ${socks5hStats.p90.toFixed(2)} | ${socks5hStats.p99.toFixed(2)} |`);
  } else {
    console.log('| SOCKS5 CONNECT (DNS domain) | SKIPPED: configure SOCKS5H_TARGET_HOST |');
  }
  console.log(`| HTTP CONNECT               | ${httpConnectSamples.length}/${SAMPLE_COUNT} | ${httpConnectStats.p50.toFixed(2)} | ${httpConnectStats.p90.toFixed(2)} | ${httpConnectStats.p99.toFixed(2)} |`);
  console.log('-----------------------------------------------------------------------------------------');

  // --- 测试维度 2: 首字节时间 (TTFB) ---
  console.log(`\n📊 [测试 2/3] 首字节响应时间 (TTFB) 采样 (${SAMPLE_COUNT} 轮)...`);
  const ttfbDirectSamples = [];
  const ttfbProxySamples = [];

  const ttfbScenarios = [
    ['direct HTTP', () => measureTtfbDirect(TARGET_HOST, TARGET_PORT, TARGET_PATH), ttfbDirectSamples],
    ['HTTP proxy', () => measureTtfbHttpProxy(PROXY_HOST, PROXY_PORT, TARGET_HOST, TARGET_PORT, TARGET_PATH), ttfbProxySamples],
  ];
  for (let i = 0; i < SAMPLE_COUNT; i++) {
    for (const [name, measure, samples] of shuffled(ttfbScenarios)) {
      try { samples.push(await measure()); }
      catch (err) { console.warn(`  ${name} TTFB sample ${i + 1}/${SAMPLE_COUNT} failed: ${err.message}`); }
    }
  }

  const ttfbDirectStats = calcStats(ttfbDirectSamples);
  const ttfbProxyStats = calcStats(ttfbProxySamples);
  const ttfbOverheadMs = ttfbProxyStats.mean - ttfbDirectStats.mean;

  console.log('-----------------------------------------------------------------------------------------');
  console.log('| 链路模式 (成功/计划)      | Min (ms) | P50 (ms) | Mean (ms)| P90 (ms) | P99 (ms) | Max (ms) |');
  console.log('-----------------------------------------------------------------------------------------');
  console.log(`| Direct 直连 GET (${ttfbDirectSamples.length}/${SAMPLE_COUNT})   | ${ttfbDirectStats.min.toFixed(2).padStart(8)} | ${ttfbDirectStats.p50.toFixed(2).padStart(8)} | ${ttfbDirectStats.mean.toFixed(2).padStart(8)} | ${ttfbDirectStats.p90.toFixed(2).padStart(8)} | ${ttfbDirectStats.p99.toFixed(2).padStart(8)} | ${ttfbDirectStats.max.toFixed(2).padStart(8)} |`);
  console.log(`| HTTP Proxy GET (${ttfbProxySamples.length}/${SAMPLE_COUNT})     | ${ttfbProxyStats.min.toFixed(2).padStart(8)} | ${ttfbProxyStats.p50.toFixed(2).padStart(8)} | ${ttfbProxyStats.mean.toFixed(2).padStart(8)} | ${ttfbProxyStats.p90.toFixed(2).padStart(8)} | ${ttfbProxyStats.p99.toFixed(2).padStart(8)} | ${ttfbProxyStats.max.toFixed(2).padStart(8)} |`);
  console.log('-----------------------------------------------------------------------------------------');
  console.log(`⚡ 代理引入平均额外开销 (Proxy Overhead): ${ttfbOverheadMs >= 0 ? '+' : ''}${ttfbOverheadMs.toFixed(2)} ms`);

  console.log('\n📌 本次结果为诊断基线，不套用未经同环境重复验证的固定性能 SLA。');
  console.log('📌 SOCKS5h 与 SOCKS5 的总建隧道时间差包含 DNS、地址/路径差异及调度开销，不能解释为纯 DNS 耗时。');
  const incomplete = [directTcpSamples, socks5Samples, httpConnectSamples, ttfbDirectSamples, ttfbProxySamples]
    .some((samples) => samples.length < SAMPLE_COUNT) ||
    (SOCKS5H_TARGET_HOST !== '' && socks5hSamples.length < SAMPLE_COUNT);
  console.log(incomplete ? '⚠️ 部分样本失败；查看上方错误并结合成功数解读。' : '✅ 所有计划样本均成功。');
  console.log('\n================================================================');
  if (incomplete) process.exitCode = 1;
}

runBenchmark().catch((err) => {
  console.error('❌ 基准测试发生未捕获异常:', err);
  process.exit(1);
});
