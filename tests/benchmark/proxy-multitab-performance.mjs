#!/usr/bin/env node

import { chromium } from '../smoke/node_modules/@playwright/test/index.mjs';
import fs from 'node:fs';
import path from 'node:path';
import { randomBytes } from 'node:crypto';
import { rotateModes, requestTimelineEntry, failedRequestTotal, sanitizeProxyStatus, originToken, resourceToken, navigationMetrics, finalizeRequestCounts } from './proxy-multitab-diagnostics.mjs';

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
const statusURL = process.env.MULTITAB_STATUS_URL || '';
const traceKey = randomBytes(32);
const originNames = new Map();
const resourceNames = new Map();
function traceOrigin(url) {
  const token = originToken(url, traceKey);
  if (token && process.env.MULTITAB_ORIGIN_MAP_FILE) originNames.set(token, new URL(url).hostname);
  return token;
}
function traceResource(url, type) {
  if (!process.env.MULTITAB_RESOURCE_MAP_FILE || type !== 'Script') return null;
  try {
    const parsed = new URL(url);
    if (parsed.hostname !== process.env.MULTITAB_RESOURCE_HOST) return null;
    const token = resourceToken(url, traceKey);
    if (token) resourceNames.set(token, parsed.origin + parsed.pathname);
    return token;
  } catch { return null; }
}

if (!tabCounts.length) throw new Error('MULTITAB_COUNTS must contain a positive tab count.');
if (Boolean(username) !== Boolean(password)) throw new Error('Set both SOCKS5_USERNAME and SOCKS5_PASSWORD, or neither.');
if (Boolean(process.env.MULTITAB_RESOURCE_MAP_FILE) !== Boolean(process.env.MULTITAB_RESOURCE_HOST)) throw new Error('Set both MULTITAB_RESOURCE_MAP_FILE and MULTITAB_RESOURCE_HOST, or neither.');
if (statusURL && !['127.0.0.1', 'localhost', '[::1]'].includes(new URL(statusURL).hostname)) throw new Error('MULTITAB_STATUS_URL must use a loopback address.');

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

  const launchStartUnixMs = Date.now();
  const statusSamples = [];
  let pending = Promise.resolve();
  let inFlight = false;
  const sampleStatus = () => {
    if (inFlight) return;
    inFlight = true;
    const unixMs = Date.now();
    pending = fetch(statusURL, { signal: AbortSignal.timeout(1000), redirect: 'error' })
      .then((response) => { if (!response.ok) throw new Error('status HTTP error'); return response.json(); })
      .then((value) => statusSamples.push({ unixMs, status: sanitizeProxyStatus(value) }))
      .catch(() => statusSamples.push({ unixMs, error: 'status_unavailable' }))
      .finally(() => { inFlight = false; });
  };
  let timer;
  if (statusURL) { sampleStatus(); timer = setInterval(sampleStatus, 200); }
  let browser;
  try {
    browser = await chromium.launch(launchOptions);
    const context = await browser.newContext();
    const pages = await Promise.all(Array.from({ length: tabCount }, () => context.newPage()));
    const timeline = [];
    const pendingByPage = [];
    await Promise.all(pages.map(async (page, index) => {
      const cdp = await context.newCDPSession(page);
      await cdp.send('Network.enable');
      const starts = new Map();
      pendingByPage.push(starts);
      cdp.on('Network.requestWillBeSent', (event) => {
        const prior = starts.get(event.requestId);
        if (prior && event.redirectResponse) timeline.push({ ...requestTimelineEntry({ wallTime: prior.wallTime, type: prior.type, response: event.redirectResponse }, index), origin: prior.origin, resource: prior.resource, outcome: 'redirect' });
        starts.set(event.requestId, { wallTime: event.wallTime, timestamp: event.timestamp, type: event.type, origin: traceOrigin(event.request.url), resource: traceResource(event.request.url, event.type), tab: index });
      });
      cdp.on('Network.responseReceived', (event) => {
        const start = starts.get(event.requestId);
        if (start) start.entry = { ...requestTimelineEntry({ ...event, wallTime: start.wallTime }, index), origin: start.origin, resource: start.resource };
      });
      for (const [eventName, outcome] of [['Network.loadingFinished', 'finished'], ['Network.loadingFailed', 'failed']]) cdp.on(eventName, (event) => {
        const start = starts.get(event.requestId);
        if (!start) return;
        timeline.push({ ...(start.entry ?? { startUnixMs: start.wallTime * 1000, tab: index, resourceType: start.type ?? null, status: null, protocol: null, phases: {}, origin: start.origin, resource: start.resource }), outcome, durationMs: Math.max(0, (event.timestamp - start.timestamp) * 1000) });
        starts.delete(event.requestId);
      });
    }));
    const requestCounts = new Array(tabCount).fill(0);
    const failedRequestCounts = new Array(tabCount).fill(0);
    const mainStatuses = new Array(tabCount).fill(null);
    pages.forEach((page, index) => {
      page.on('requestfinished', () => { requestCounts[index]++; });
      page.on('requestfailed', () => { failedRequestCounts[index]++; });
      page.on('response', (response) => {
        if (response.request().isNavigationRequest() && response.frame() === page.mainFrame()) mainStatuses[index] = response.status();
      });
    });

    const startUnixMs = Date.now();
    const batchStarted = performance.now();
    const outcomes = await Promise.all(pages.map(async (page, index) => {
      const pageStarted = performance.now();
      let response = null;
      let errorKind = null;
      try {
        response = await page.goto(targetURL, { waitUntil: 'load', timeout: timeoutMs });
      } catch (error) {
        errorKind = error?.name === 'TimeoutError' ? 'timeout' : 'navigation_error';
      }
      let timing = null;
      try {
        const nav = await page.evaluate(() => {
          const nav = performance.getEntriesByType('navigation')[0];
          return nav ? {
            startTime: nav.startTime,
            requestStart: nav.requestStart,
            responseStart: nav.responseStart,
            domContentLoadedEventEnd: nav.domContentLoadedEventEnd,
            loadEventEnd: nav.loadEventEnd,
            firstContentfulPaint: performance.getEntriesByName('first-contentful-paint')[0]?.startTime ?? 0,
          } : null;
        });
        timing = navigationMetrics(nav);
      } catch { /* A page can disappear while its timing is being collected. */ }
      return {
        ok: Boolean(response && response.status() < 400),
        status: response?.status() ?? mainStatuses[index],
        errorKind,
        elapsedMs: performance.now() - pageStarted,
        timing,
        requests: requestCounts[index],
        failedRequests: failedRequestCounts[index],
      };
    }));
    const batchElapsedMs = performance.now() - batchStarted;
    finalizeRequestCounts(outcomes, requestCounts, failedRequestCounts);
    for (const starts of pendingByPage) for (const start of starts.values()) {
      timeline.push({ ...(start.entry ?? { startUnixMs: start.wallTime * 1000, tab: start.tab, resourceType: start.type ?? null, status: null, protocol: null, phases: {}, origin: start.origin, resource: start.resource }), outcome: 'pending', durationMs: Math.max(0, Date.now() - start.wallTime * 1000) });
    }
    await context.close();
    return { launchStartUnixMs, startUnixMs, endUnixMs: Date.now(), batchElapsedMs, outcomes, timeline, statusSamples };
  } finally {
    if (browser) await browser.close();
    if (timer) clearInterval(timer);
    await pending;
  }
}

const results = [];
const traces = [];
for (const tabCount of tabCounts) {
  const samplesByMode = new Map(modes.map((mode) => [mode.name, []]));
  for (let sample = 0; sample < samples; sample++) {
    for (const mode of rotateModes(modes, sample)) {
      const result = await runSample(mode, tabCount);
      samplesByMode.get(mode.name).push(result);
      traces.push({ mode: mode.name, tabs: tabCount, sample, launchStartUnixMs: result.launchStartUnixMs, startUnixMs: result.startUnixMs, endUnixMs: result.endUnixMs, pages: result.outcomes, requests: result.timeline, statusSamples: result.statusSamples });
    }
  }
  for (const mode of modes) {
    const samplesForScenario = samplesByMode.get(mode.name);
    const pages = samplesForScenario.flatMap((sample) => sample.outcomes);
    const okPages = pages.filter((page) => page.ok);
    const phases = samplesForScenario.flatMap((sample) => sample.timeline.map((entry) => entry.phases));
    const phaseSummary = Object.fromEntries(['proxyMs', 'dnsMs', 'connectMs', 'waitMs'].map((name) => {
      const values = phases.map((phase) => phase[name]).filter(Number.isFinite);
      return [name, { count: values.length, p50: percentile(values, 0.5), p95: percentile(values, 0.95) }];
    }));
    results.push({
      mode: mode.name,
      tabs: tabCount,
      samples,
      pages: pages.length,
      successes: okPages.length,
      failures: pages.length - okPages.length,
      timedOutPages: pages.filter((page) => page.errorKind === 'timeout').length,
      mainResponsesOk: pages.filter((page) => page.status >= 200 && page.status < 400).length,
      ttfbSamples: pages.filter((page) => Number.isFinite(page.timing?.ttfbMs)).length,
      domReadySamples: pages.filter((page) => Number.isFinite(page.timing?.domReadyMs)).length,
      firstPaintSamples: pages.filter((page) => Number.isFinite(page.timing?.firstPaintMs)).length,
      batchWallMsP50: percentile(samplesForScenario.map((sample) => sample.batchElapsedMs), 0.5),
      pageLoadMsP50: percentile(okPages.map((page) => page.timing?.loadEventMs).filter(Number.isFinite), 0.5),
      pageLoadMsP95: percentile(okPages.map((page) => page.timing?.loadEventMs).filter(Number.isFinite), 0.95),
      ttfbMsP50: percentile(pages.map((page) => page.timing?.ttfbMs).filter(Number.isFinite), 0.5),
      domReadyMsP50: percentile(pages.map((page) => page.timing?.domReadyMs).filter(Number.isFinite), 0.5),
      firstPaintMsP50: percentile(pages.map((page) => page.timing?.firstPaintMs).filter(Number.isFinite), 0.5),
      requestsPerPageP50: percentile(pages.map((page) => page.requests), 0.5),
      failedRequestsPerPageP50: percentile(pages.map((page) => page.failedRequests), 0.5),
      failedRequests: failedRequestTotal(pages),
      phases: phaseSummary,
    });
  }
}

if (process.env.MULTITAB_TRACE_FILE) fs.writeFileSync(process.env.MULTITAB_TRACE_FILE, JSON.stringify({ targetOrigin: originToken(targetURL, traceKey), traces }, null, 2));
if (process.env.MULTITAB_ORIGIN_MAP_FILE) fs.writeFileSync(process.env.MULTITAB_ORIGIN_MAP_FILE, JSON.stringify(Object.fromEntries(originNames), null, 2));
if (process.env.MULTITAB_RESOURCE_MAP_FILE) fs.writeFileSync(process.env.MULTITAB_RESOURCE_MAP_FILE, JSON.stringify(Object.fromEntries(resourceNames), null, 2));
console.log(JSON.stringify({ samples, tabCounts, results }, null, 2));
if (results.some((result) => result.failures > 0 || result.failedRequests > 0)) process.exitCode = 1;
