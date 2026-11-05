#!/usr/bin/env node

import { chromium } from '../smoke/node_modules/@playwright/test/index.mjs';
import fs from 'node:fs';
import path from 'node:path';

function loadEnvDev() {
  const envPath = path.resolve('env/.envdev');
  if (!fs.existsSync(envPath)) return;
  for (const line of fs.readFileSync(envPath, 'utf8').split(/\r?\n/)) {
    const match = line.match(/^\s*([A-Za-z0-9_]+)=(.*)$/);
    if (!match) continue;
    const [, key, raw] = match;
    if (!process.env[key]) process.env[key] = raw.trim().replace(/^['"]|['"]$/g, '');
  }
}

loadEnvDev();

const targetURL = process.env.WEB_BENCH_URL || 'http://10.126.1.70:5271/app/#/login';
const modes = [
  { name: 'direct', proxy: null },
  { name: 'http-proxy', proxy: process.env.PROXY_URL || 'http://10.126.1.70:18080' },
  { name: 'socks5-proxy', proxy: process.env.SOCKS5_PROXY_URL || 'socks5://10.126.1.70:18080' },
];
const tabCounts = (process.env.MULTITAB_COUNTS || '1,5,20')
  .split(',')
  .map((value) => Number.parseInt(value.trim(), 10))
  .filter((value) => Number.isInteger(value) && value > 0 && value <= 128);
const samples = Math.max(1, Number.parseInt(process.env.MULTITAB_SAMPLES || '1', 10));
const timeoutMs = Math.max(1000, Number.parseInt(process.env.MULTITAB_TIMEOUT_MS || '30000', 10));
const username = process.env.SOCKS5_USERNAME || '';
const password = process.env.SOCKS5_PASSWORD || '';

if (!tabCounts.length) throw new Error('MULTITAB_COUNTS must contain a positive tab count.');
if (Boolean(username) !== Boolean(password)) throw new Error('Set both SOCKS5_USERNAME and SOCKS5_PASSWORD, or neither.');

function percentile(values, fraction) {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil(fraction * sorted.length) - 1)];
}

async function runSample(mode, tabCount) {
  const launchOptions = {
    channel: 'chrome',
    headless: true,
    args: ['--no-sandbox', '--disable-setuid-sandbox', '--disable-dev-shm-usage'],
  };
  if (mode.proxy) {
    launchOptions.proxy = {
      server: mode.proxy,
      ...(username ? { username, password } : {}),
    };
  }

  const browser = await chromium.launch(launchOptions);
  try {
    const context = await browser.newContext();
    const pages = await Promise.all(Array.from({ length: tabCount }, () => context.newPage()));
    const requestCounts = new Array(tabCount).fill(0);
    const failedRequestCounts = new Array(tabCount).fill(0);
    pages.forEach((page, index) => {
      page.on('requestfinished', () => { requestCounts[index]++; });
      page.on('requestfailed', () => { failedRequestCounts[index]++; });
    });

    const batchStarted = performance.now();
    const outcomes = await Promise.all(pages.map(async (page, index) => {
      const pageStarted = performance.now();
      try {
        const response = await page.goto(targetURL, { waitUntil: 'load', timeout: timeoutMs });
        const timing = await page.evaluate(() => {
          const nav = performance.getEntriesByType('navigation')[0];
          return nav ? {
            ttfbMs: nav.responseStart - nav.requestStart,
            loadEventMs: nav.loadEventEnd - nav.startTime,
          } : null;
        });
        return {
          ok: Boolean(response && response.status() < 400),
          status: response?.status() ?? null,
          elapsedMs: performance.now() - pageStarted,
          timing,
          requests: requestCounts[index],
          failedRequests: failedRequestCounts[index],
        };
      } catch {
        return {
          ok: false,
          status: null,
          elapsedMs: performance.now() - pageStarted,
          timing: null,
          requests: requestCounts[index],
          failedRequests: failedRequestCounts[index],
        };
      }
    }));
    const batchElapsedMs = performance.now() - batchStarted;
    await context.close();
    return { batchElapsedMs, outcomes };
  } finally {
    await browser.close();
  }
}

const results = [];
for (const mode of modes) {
  for (const tabCount of tabCounts) {
    const samplesForScenario = [];
    for (let sample = 0; sample < samples; sample++) {
      samplesForScenario.push(await runSample(mode, tabCount));
    }
    const pages = samplesForScenario.flatMap((sample) => sample.outcomes);
    const okPages = pages.filter((page) => page.ok);
    results.push({
      mode: mode.name,
      tabs: tabCount,
      samples,
      pages: pages.length,
      successes: okPages.length,
      failures: pages.length - okPages.length,
      batchWallMsP50: percentile(samplesForScenario.map((sample) => sample.batchElapsedMs), 0.5),
      pageLoadMsP50: percentile(okPages.map((page) => page.timing?.loadEventMs ?? page.elapsedMs), 0.5),
      pageLoadMsP95: percentile(okPages.map((page) => page.timing?.loadEventMs ?? page.elapsedMs), 0.95),
      ttfbMsP50: percentile(okPages.map((page) => page.timing?.ttfbMs ?? page.elapsedMs), 0.5),
      requestsPerPageP50: percentile(pages.map((page) => page.requests), 0.5),
      failedRequestsPerPageP50: percentile(pages.map((page) => page.failedRequests), 0.5),
    });
  }
}

console.log(JSON.stringify({ samples, tabCounts, results }, null, 2));
if (results.some((result) => result.failures > 0)) process.exitCode = 1;
