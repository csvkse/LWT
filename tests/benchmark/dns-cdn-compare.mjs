#!/usr/bin/env node
import dns from 'node:dns';
import https from 'node:https';
import net from 'node:net';
import { createHash, createHmac, randomBytes } from 'node:crypto';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

export async function queryDns(server, host, timeoutMs) {
  const resolver = new dns.promises.Resolver({ timeout: timeoutMs, tries: 1 });
  resolver.setServers([server]);
  const started = performance.now();
  const records = await resolver.resolve4(host, { ttl: true });
  return { dnsMs: performance.now() - started, records };
}

export function createProbePlan(groups, limit) {
  const result = [];
  for (let i = 0; i < limit; i++) for (const group of groups) {
    if (group.records[i]) result.push({ label: group.label, address: group.records[i].address, ttl: group.records[i].ttl });
  }
  return result;
}

export function isPublicV4(ip) {
  if (net.isIP(ip) !== 4) return false;
  const [a, b, c] = ip.split('.').map(Number);
  return !(a === 0 || a === 10 || a === 127 || a >= 224 ||
    (a === 100 && b >= 64 && b <= 127) || (a === 169 && b === 254) ||
    (a === 172 && b >= 16 && b <= 31) || (a === 192 && b === 168) ||
    (a === 192 && b === 0 && (c === 0 || c === 2)) ||
    (a === 198 && (b === 18 || b === 19 || (b === 51 && c === 100))) ||
    (a === 203 && b === 0 && c === 113));
}

function probeResource(url, address, timeoutMs, maxBytes) {
  return new Promise((resolve) => {
    const start = performance.now();
    const result = { bytes: 0, complete: false };
    const hash = createHash('sha256');
    let finished = false;
    const finish = (error) => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      result.totalMs = performance.now() - start;
      if (error) result.error = error;
      if (result.complete) result.sha256 = hash.digest('hex');
      resolve(result);
    };
    const req = https.request(url, {
      agent: false,
      headers: { 'accept-encoding': 'identity', connection: 'close' },
      lookup: (_name, options, callback) => options.all ? callback(null, [{ address, family: 4 }]) : callback(null, address, 4),
    }, (response) => {
      result.status = response.statusCode;
      result.headersMs = performance.now() - start;
      response.on('data', (chunk) => {
        result.bytes += chunk.length;
        hash.update(chunk);
        if (result.bytes > maxBytes) { result.capped = true; req.destroy(); finish('byte_limit'); }
      });
      response.on('end', () => { result.complete = true; finish(); });
      response.on('error', () => finish('response_error'));
      response.on('aborted', () => finish('response_aborted'));
    });
    req.on('error', () => finish('connection_error'));
    const timer = setTimeout(() => { req.destroy(); finish('timeout'); }, timeoutMs);
    req.end();
  });
}

async function main() {
  const rawUrl = process.env.DNS_BENCH_URL_B64 ? Buffer.from(process.env.DNS_BENCH_URL_B64, 'base64').toString('utf8') : process.env.DNS_BENCH_URL;
  if (!rawUrl) throw new Error('Set DNS_BENCH_URL to an authorized HTTPS resource.');
  const url = new URL(rawUrl);
  if (url.protocol !== 'https:' || url.username || url.password || (url.port && url.port !== '443')) throw new Error('Expected HTTPS on port 443 without URL credentials.');
  const servers = (process.env.DNS_BENCH_SERVERS || dns.getServers().join(',')).split(',').map((x) => x.trim()).filter(Boolean);
  if (!servers.length || servers.length > 4) throw new Error('Provide one to four DNS servers.');
  const parseBounded = (name, fallback, maximum) => {
    const value = Number(process.env[name] || fallback);
    if (!Number.isInteger(value) || value < 1 || value > maximum) throw new Error(`Invalid ${name}.`);
    return value;
  };
  const repeats = parseBounded('DNS_BENCH_REPEATS', 2, 5);
  const candidates = parseBounded('DNS_BENCH_CANDIDATES', 2, 4);
  const timeoutMs = parseBounded('DNS_BENCH_TIMEOUT_MS', 12000, 60000);
  const maxBytes = parseBounded('DNS_BENCH_MAX_BYTES', 8 * 1024 * 1024, 32 * 1024 * 1024);
  const key = randomBytes(32);
  const id = (value) => createHmac('sha256', key).update(value).digest('hex').slice(0, 12);
  const rounds = [];
  for (let round = 0; round < repeats; round++) {
    const groups = await Promise.all(servers.map(async (server, index) => {
      const label = `resolver${index + 1}`;
      try { return { label, ...(await queryDns(server, url.hostname, Math.min(timeoutMs, 5000))) }; }
      catch (error) { return { label, records: [], error: error.code || 'dns_error' }; }
    }));
    const plan = createProbePlan(groups, candidates).filter((item) => isPublicV4(item.address));
    const probes = await Promise.all(plan.map(async (item) => ({ label: item.label, candidate: id(item.address), ttl: item.ttl, ...(await probeResource(url, item.address, timeoutMs, maxBytes)) })));
    rounds.push({ round, dns: groups.map((group) => ({ label: group.label, dnsMs: group.dnsMs ?? null, error: group.error ?? null, answerCount: group.records.length, candidates: group.records.map((item) => ({ id: id(item.address), ttl: item.ttl })) })), probes });
  }
  console.log(JSON.stringify({ method: 'dns-A-and-pinned-https-http1', repeats, candidatesPerResolver: candidates, rounds }, null, 2));
  if (rounds.some((round) => round.dns.some((item) => item.error) || !round.probes.length)) process.exitCode = 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  main().catch((error) => { console.error(error.message); process.exitCode = 1; });
}
