#!/usr/bin/env node

import { execFileSync } from 'node:child_process';
import net from 'node:net';
import process from 'node:process';
import { setTimeout as delay } from 'node:timers/promises';

const DEFAULT_TIMEOUT_MS = 5000;
const RESOURCE_COLLECTOR = String.raw`
import json, os, time, urllib.request
status = json.load(urllib.request.urlopen('http://127.0.0.1:19080/api/status', timeout=2))
runtime = status.get('runtime') or {}
pid = runtime.get('pid')
if not pid:
    listen_port = b'18080'
    for path in __import__('glob').glob('/proc/[0-9]*/cmdline'):
        try:
            args = open(path, 'rb').read().split(bytes([0]))
            if any(arg in (b'--listen', b'-listen') and i + 1 < len(args) and args[i + 1].rsplit(b':', 1)[-1] == listen_port for i, arg in enumerate(args)):
                pid = int(path.split('/')[2])
                break
        except OSError:
            pass
proc = {}
if pid:
    base = '/proc/' + str(pid)
    try:
        with open(base + '/status', encoding='ascii') as f:
            for line in f:
                if line.startswith(('VmRSS:', 'Threads:')):
                    parts = line.split()
                    proc[parts[0].rstrip(':')] = int(parts[1]) * (1024 if parts[0] == 'VmRSS:' else 1)
        proc['fdCount'] = len(os.listdir(base + '/fd'))
        with open(base + '/limits', encoding='ascii') as f:
            for line in f:
                if line.startswith('Max open files'):
                    parts = line.split()
                    proc['nofileSoft'] = parts[3]
                    proc['nofileHard'] = parts[4]
                    break
    except (OSError, ValueError, IndexError) as e:
        proc['readError'] = type(e).__name__
cg = {}
for name in ('cpu.max', 'cpu.stat', 'memory.current', 'memory.max', 'memory.events', 'pids.current', 'pids.max'):
    try:
        with open('/sys/fs/cgroup/' + name, encoding='ascii') as f:
            cg[name] = f.read().strip()
    except OSError:
        pass
keys = ('activeConns', 'totalConns', 'peakActiveConns', 'rejectedConns', 'maxActiveSessions', 'httpActiveRequests', 'httpPendingRequests', 'httpRejectedRequests', 'httpQueueTimeouts')
print(json.dumps({'timeUnixMs': int(time.time() * 1000), 'status': {k: status.get(k) for k in keys}, 'runtime': runtime, 'process': proc, 'cgroup': cg}, separators=(',', ':')))
`;

const ECHO_SERVER = String.raw`
import os, signal, socketserver, sys
port = int(sys.argv[1])
pid_file = sys.argv[2]
class Handler(socketserver.BaseRequestHandler):
    def handle(self):
        while True:
            data = self.request.recv(65536)
            if not data:
                return
            self.request.sendall(data)
class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True
server = Server(('127.0.0.1', port), Handler)
with open(pid_file, 'w', encoding='ascii') as f:
    f.write(str(os.getpid()))
server.serve_forever(poll_interval=0.25)
`;

function parsePositiveInt(value, name, fallback) {
  const parsed = Number.parseInt(value ?? '', 10);
  if (value == null || value === '') return fallback;
  if (!Number.isInteger(parsed) || parsed < 1) throw new Error(`${name} must be a positive integer`);
  return parsed;
}

function parseList(value, name, fallback) {
  const list = (value || fallback).split(',').map((item) => item.trim()).filter(Boolean);
  if (!list.length) throw new Error(`${name} must contain at least one item`);
  return list;
}

function readOptions(env = process.env) {
  const modes = parseList(env.CAPACITY_MODES, 'CAPACITY_MODES', 'socks5,http-connect');
  const scenarios = parseList(env.CAPACITY_SCENARIOS, 'CAPACITY_SCENARIOS', 'idle,duplex');
  const counts = parseList(env.CAPACITY_COUNTS, 'CAPACITY_COUNTS', '50,100,500').map((item) => parsePositiveInt(item, 'connection count'));
  for (const mode of modes) if (!['socks5', 'http-connect'].includes(mode)) throw new Error(`unsupported mode: ${mode}`);
  for (const scenario of scenarios) if (!['idle', 'duplex'].includes(scenario)) throw new Error(`unsupported scenario: ${scenario}`);
  return {
    modes,
    scenarios,
    counts,
    repeats: parsePositiveInt(env.CAPACITY_REPEATS, 'CAPACITY_REPEATS', 1),
    holdMs: parsePositiveInt(env.CAPACITY_HOLD_MS, 'CAPACITY_HOLD_MS', 120_000),
    rampMs: parsePositiveInt(env.CAPACITY_RAMP_MS, 'CAPACITY_RAMP_MS', 10),
    sampleMs: parsePositiveInt(env.CAPACITY_SAMPLE_MS, 'CAPACITY_SAMPLE_MS', 5000),
    drainMs: parsePositiveInt(env.CAPACITY_DRAIN_MS, 'CAPACITY_DRAIN_MS', 10_000),
    duplexDrainMs: parsePositiveInt(env.CAPACITY_DUPLEX_DRAIN_MS, 'CAPACITY_DUPLEX_DRAIN_MS', 5000),
    timeoutMs: parsePositiveInt(env.CAPACITY_TIMEOUT_MS, 'CAPACITY_TIMEOUT_MS', DEFAULT_TIMEOUT_MS),
    proxyHost: env.CAPACITY_PROXY_HOST || '127.0.0.1',
    proxyPort: parsePositiveInt(env.CAPACITY_PROXY_PORT, 'CAPACITY_PROXY_PORT', 18080),
    targetHost: env.CAPACITY_TARGET_HOST || '127.0.0.1',
    targetPort: parsePositiveInt(env.CAPACITY_TARGET_PORT, 'CAPACITY_TARGET_PORT', 23991),
    container: env.CAPACITY_CONTAINER || 'linuxwebtool-dev',
    manageEcho: env.CAPACITY_MANAGE_ECHO !== '0',
    collectResources: env.CAPACITY_COLLECT_RESOURCES !== '0',
    runId: `${process.pid}-${Date.now()}`,
  };
}

function connectSocket(host, port, timeoutMs) {
  const socket = net.createConnection({ host, port });
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => socket.destroy(new Error('timed out connecting to proxy')), timeoutMs);
    socket.once('connect', () => {
      clearTimeout(timer);
      socket.setTimeout(0);
      resolve(socket);
    });
    socket.once('error', (error) => {
      clearTimeout(timer);
      reject(error);
    });
  });
}

class SocketReader {
  constructor(socket) {
    this.socket = socket;
    this.buffer = Buffer.alloc(0);
    this.waiter = null;
    this.error = null;
    this.closed = false;
    this.onData = (chunk) => {
      this.buffer = this.buffer.length ? Buffer.concat([this.buffer, chunk]) : chunk;
      this.wake();
    };
    this.onError = (error) => {
      this.error = error;
      this.wake();
    };
    this.onClose = () => {
      this.closed = true;
      this.wake();
    };
    socket.on('data', this.onData);
    socket.on('error', this.onError);
    socket.on('close', this.onClose);
  }

  wake() {
    if (!this.waiter) return;
    const waiter = this.waiter;
    this.waiter = null;
    clearTimeout(waiter.timer);
    waiter.resolve();
  }

  waitForData(timeoutMs, label) {
    return new Promise((resolve, reject) => {
      const waiter = {
        resolve,
        timer: setTimeout(() => {
          if (this.waiter === waiter) this.waiter = null;
          reject(new Error(`timed out waiting for ${label}`));
        }, timeoutMs),
      };
      this.waiter = waiter;
    });
  }

  async read(findEnd, maxBytes, timeoutMs, label) {
    while (true) {
      const end = findEnd(this.buffer);
      if (end >= 0) {
        const result = this.buffer.subarray(0, end);
        this.buffer = this.buffer.subarray(end);
        return result;
      }
      if (this.buffer.length > maxBytes) throw new Error(`${label} exceeded ${maxBytes} bytes`);
      if (this.error) throw this.error;
      if (this.closed) throw new Error(`${label} closed before a complete response`);
      await this.waitForData(timeoutMs, label);
    }
  }

  readExactly(length, timeoutMs, label) {
    return this.read((buffer) => buffer.length >= length ? length : -1, length + 64, timeoutMs, label);
  }

  readUntil(delimiter, maxBytes, timeoutMs, label) {
    return this.read((buffer) => {
      const offset = buffer.indexOf(delimiter);
      return offset < 0 ? -1 : offset + delimiter.length;
    }, maxBytes, timeoutMs, label);
  }

  detach() {
    this.socket.removeListener('data', this.onData);
    this.socket.removeListener('error', this.onError);
    this.socket.removeListener('close', this.onClose);
    if (this.waiter) {
      clearTimeout(this.waiter.timer);
      this.waiter = null;
    }
    if (this.buffer.length) this.socket.unshift(this.buffer);
    this.buffer = Buffer.alloc(0);
  }
}

function socksAddress(host, port) {
  const ipVersion = net.isIP(host);
  let address;
  let addressType;
  if (ipVersion === 4) {
    address = Buffer.from(host.split('.').map((part) => Number(part)));
    addressType = 0x01;
  } else if (ipVersion === 6) {
    const halves = host.split('::');
    const left = halves[0] ? halves[0].split(':') : [];
    const right = halves[1] ? halves[1].split(':') : [];
    const groups = [...left, ...Array(8 - left.length - right.length).fill('0'), ...right];
    if (groups.length !== 8) throw new Error(`invalid IPv6 target: ${host}`);
    address = Buffer.concat(groups.map((group) => {
      const bytes = Buffer.alloc(2);
      bytes.writeUInt16BE(Number.parseInt(group || '0', 16));
      return bytes;
    }));
    addressType = 0x04;
  } else {
    address = Buffer.from(host, 'utf8');
    if (!address.length || address.length > 255) throw new Error('SOCKS5 domain must be 1-255 bytes');
    addressType = 0x03;
    address = Buffer.concat([Buffer.from([address.length]), address]);
  }
  const portBytes = Buffer.alloc(2);
  portBytes.writeUInt16BE(port);
  return Buffer.concat([Buffer.from([0x05, 0x01, 0x00, addressType]), address, portBytes]);
}

export async function connectTunnel({ mode, proxyHost, proxyPort, targetHost, targetPort, timeoutMs = DEFAULT_TIMEOUT_MS }) {
  const socket = await connectSocket(proxyHost, proxyPort, timeoutMs);
  const reader = new SocketReader(socket);
  try {
    if (mode === 'socks5') {
      socket.write(Buffer.from([0x05, 0x01, 0x00]));
      const method = await reader.readExactly(2, timeoutMs, 'SOCKS5 method selection');
      if (method[0] !== 0x05 || method[1] !== 0x00) throw new Error('proxy did not select SOCKS5 no-auth method');
      socket.write(socksAddress(targetHost, targetPort));
      const reply = await reader.readExactly(4, timeoutMs, 'SOCKS5 CONNECT reply');
      if (reply[0] !== 0x05 || reply[1] !== 0x00) throw new Error(`SOCKS5 CONNECT failed with reply ${reply[1]}`);
      const addressBytes = reply[3] === 0x01 ? 4 : reply[3] === 0x04 ? 16 : reply[3] === 0x03 ? (await reader.readExactly(1, timeoutMs, 'SOCKS5 domain reply length'))[0] + 1 : -1;
      if (addressBytes < 0) throw new Error(`unsupported SOCKS5 reply address type ${reply[3]}`);
      await reader.readExactly(addressBytes + 2, timeoutMs, 'SOCKS5 bound address');
    } else if (mode === 'http-connect') {
      if (/[\r\n]/.test(targetHost)) throw new Error('invalid HTTP CONNECT target host');
      const authorityHost = net.isIP(targetHost) === 6 ? `[${targetHost}]` : targetHost;
      socket.write(`CONNECT ${authorityHost}:${targetPort} HTTP/1.1\r\nHost: ${authorityHost}:${targetPort}\r\nProxy-Connection: keep-alive\r\n\r\n`);
      const header = (await reader.readUntil(Buffer.from('\r\n\r\n'), 64 * 1024, timeoutMs, 'HTTP CONNECT response')).toString('latin1');
      const statusLine = header.split('\r\n', 1)[0];
      const status = Number.parseInt(statusLine.split(' ')[1], 10);
      if (!/^HTTP\/1\.[01] /.test(statusLine) || status < 200 || status >= 300) throw new Error(`HTTP CONNECT failed: ${statusLine}`);
    } else {
      throw new Error(`unsupported tunnel mode: ${mode}`);
    }
    socket.on('error', () => {});
    return socket;
  } catch (error) {
    socket.destroy();
    throw error;
  } finally {
    reader.detach();
  }
}

export function getScenarioIntegrityErrors(result) {
  const errors = [];
  if (result.failedConnections > 0) errors.push(`${result.failedConnections} connections failed`);
  if (result.successfulConnections !== result.requestedConnections) {
    errors.push(`successful connections ${result.successfulConnections} did not match requested ${result.requestedConnections}`);
  }
  const duplex = result.duplex || {};
  if (duplex.sentBytes !== duplex.receivedBytes) {
    errors.push(`duplex byte count differs: sent ${duplex.sentBytes ?? 0}, received ${duplex.receivedBytes ?? 0}`);
  }
  if ((duplex.mismatchedPackets || 0) > 0) errors.push(`${duplex.mismatchedPackets} duplex packets did not match`);
  if ((duplex.partialBytes || 0) > 0) errors.push(`${duplex.partialBytes} trailing duplex bytes were incomplete`);
  return errors;
}

function connectEchoClient(targetHost, targetPort, timeoutMs) {
  return connectSocket(targetHost, targetPort, timeoutMs);
}

export function startDuplex(socket, connectionId) {
  const payload = Buffer.alloc(64, connectionId % 251);
  payload.writeUInt32BE(connectionId >>> 0, 0);
  let receivedBuffer = Buffer.alloc(0);
  let sentBytes = 0;
  let receivedBytes = 0;
  let mismatchedPackets = 0;
  const onData = (chunk) => {
    receivedBuffer = receivedBuffer.length ? Buffer.concat([receivedBuffer, chunk]) : chunk;
    while (receivedBuffer.length >= payload.length) {
      const packet = receivedBuffer.subarray(0, payload.length);
      if (!packet.equals(payload)) mismatchedPackets++;
      receivedBytes += packet.length;
      receivedBuffer = receivedBuffer.subarray(payload.length);
    }
  };
  socket.on('data', onData);
  const timer = setInterval(() => {
    if (socket.destroyed) return;
    socket.write(payload);
    sentBytes += payload.length;
  }, 100);
  return {
    snapshot: () => ({ sentBytes, receivedBytes, mismatchedPackets, partialBytes: receivedBuffer.length }),
    stop: () => {
      clearInterval(timer);
    },
  };
}

function percentile(values, fraction) {
  if (!values.length) return null;
  const sorted = [...values].sort((a, b) => a - b);
  return sorted[Math.min(sorted.length - 1, Math.ceil(fraction * sorted.length) - 1)];
}

function collectLinuxSnapshot(container, metricsPort) {
  const collector = RESOURCE_COLLECTOR
    .replace('19080', String(metricsPort))
    .replace("listen_port = b'18080'", `listen_port = b'${String(metricsPort >= 1000 ? metricsPort - 1000 : metricsPort)}'`);
  try {
    const raw = execFileSync('docker', ['exec', container, 'python3', '-c', collector], {
      encoding: 'utf8',
      timeout: 5000,
      windowsHide: true,
    });
    return JSON.parse(raw);
  } catch (error) {
    return { sampleError: error.message.split('\n')[0] };
  }
}

function manageEchoServer(container, port, pidFile, action) {
  const stopProgram = String.raw`
import os, signal, sys
path = sys.argv[1]
try:
    with open(path, encoding='ascii') as f:
        pid = int(f.read().strip())
    os.kill(pid, signal.SIGTERM)
    os.remove(path)
except FileNotFoundError:
    pass
`;
  if (action === 'stop') {
    execFileSync('docker', ['exec', container, 'python3', '-c', stopProgram, pidFile], { encoding: 'utf8', timeout: 5000, windowsHide: true });
    return;
  }
  execFileSync('docker', ['exec', '-d', container, 'python3', '-c', ECHO_SERVER, String(port), pidFile], { encoding: 'utf8', timeout: 5000, windowsHide: true });
}

async function waitForEchoServer(container, targetHost, targetPort, timeoutMs) {
  const deadline = Date.now() + timeoutMs;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const probe = await connectEchoClient(targetHost, targetPort, 500);
      probe.destroy();
      return;
    } catch (error) {
      lastError = error;
      await delay(100);
    }
  }
  throw new Error(`echo target did not start: ${lastError?.message || 'timeout'}`);
}

async function runScenario(options, mode, scenario, connectionCount, repeat) {
  const sockets = [];
  const connectTimesMs = [];
  const failures = [];
  const resourceSamples = [];
  const duplexWriters = [];
  const startedAt = Date.now();
  const metricsPort = options.proxyPort >= 64536 ? options.proxyPort - 1000 : options.proxyPort + 1000;

  if (options.collectResources) resourceSamples.push(collectLinuxSnapshot(options.container, metricsPort));

  const connectionResults = await Promise.all(Array.from({ length: connectionCount }, async (_, index) => {
    if (index) await delay(index * options.rampMs);
    const connectStartedAt = performance.now();
    try {
      const socket = await connectTunnel({
        mode,
        proxyHost: options.proxyHost,
        proxyPort: options.proxyPort,
        targetHost: options.targetHost,
        targetPort: options.targetPort,
        timeoutMs: options.timeoutMs,
      });
      socket.on('error', () => {});
      sockets.push(socket);
      connectTimesMs.push(performance.now() - connectStartedAt);
      if (scenario === 'duplex') duplexWriters.push(startDuplex(socket, index + 1));
      return null;
    } catch (error) {
      failures.push({ index, error: error.message });
      return error;
    }
  }));

  const steadyStartedAt = Date.now();
  const sampleTimer = options.collectResources ? setInterval(() => {
    resourceSamples.push(collectLinuxSnapshot(options.container, metricsPort));
  }, options.sampleMs) : null;
  await delay(options.holdMs);
  const holdElapsedMs = Date.now() - steadyStartedAt;
  if (sampleTimer) clearInterval(sampleTimer);
  if (options.collectResources) resourceSamples.push(collectLinuxSnapshot(options.container, metricsPort));

  for (const writer of duplexWriters) writer.stop();
  const duplexDrainDeadline = Date.now() + options.duplexDrainMs;
  while (duplexWriters.some((writer) => {
    const snapshot = writer.snapshot();
    return snapshot.receivedBytes < snapshot.sentBytes || snapshot.partialBytes > 0;
  }) && Date.now() < duplexDrainDeadline) {
    await delay(10);
  }
  const duplexTotals = duplexWriters.map((writer) => writer.snapshot());
  for (const socket of sockets) socket.destroy();

  const drainStartedAt = Date.now();
  const drainDeadline = Date.now() + options.drainMs;
  let finalSnapshot = null;
  do {
    if (options.collectResources) finalSnapshot = collectLinuxSnapshot(options.container, metricsPort);
    if (finalSnapshot?.status?.activeConns === 0) break;
    await delay(500);
  } while (Date.now() < drainDeadline);

  const successfulConnections = connectionResults.filter((result) => result === null).length;
  return {
    mode,
    scenario,
    startedAtUnixMs: startedAt,
    requestedConnections: connectionCount,
    successfulConnections,
    failedConnections: failures.length,
    failures: failures.slice(0, 10),
    repeat,
    holdMs: options.holdMs,
    rampMs: options.rampMs,
    connectMsP50: percentile(connectTimesMs, 0.5),
    connectMsP95: percentile(connectTimesMs, 0.95),
    connectMsP99: percentile(connectTimesMs, 0.99),
    holdElapsedMs,
    drainElapsedMs: Date.now() - drainStartedAt,
    totalElapsedMs: Date.now() - startedAt,
    duplex: {
      sentBytes: duplexTotals.reduce((sum, item) => sum + item.sentBytes, 0),
      receivedBytes: duplexTotals.reduce((sum, item) => sum + item.receivedBytes, 0),
      mismatchedPackets: duplexTotals.reduce((sum, item) => sum + item.mismatchedPackets, 0),
      partialBytes: duplexTotals.reduce((sum, item) => sum + item.partialBytes, 0),
    },
    resourceSamples,
    drainSnapshot: finalSnapshot,
    finishedAtUnixMs: Date.now(),
  };
}

export async function runCapacityBenchmark(options) {
  const pidFile = `/tmp/socks5-capacity-echo-${options.runId}.pid`;
  let echoStarted = false;
  const results = [];
  try {
    if (options.manageEcho) {
      manageEchoServer(options.container, options.targetPort, pidFile, 'start');
      echoStarted = true;
      await waitForEchoServer(options.container, options.targetHost, options.targetPort, options.timeoutMs);
    }
    for (const mode of options.modes) {
      for (const scenario of options.scenarios) {
        for (const connectionCount of options.counts) {
          for (let repeat = 1; repeat <= options.repeats; repeat++) {
            results.push(await runScenario(options, mode, scenario, connectionCount, repeat));
          }
        }
      }
    }
  } finally {
    if (echoStarted) {
      try { manageEchoServer(options.container, options.targetPort, pidFile, 'stop'); } catch {}
    }
  }
  return { runId: options.runId, options: { ...options, runId: undefined }, results };
}

async function main() {
  const options = readOptions();
  const result = await runCapacityBenchmark(options);
  console.log(JSON.stringify(result, null, 2));
  const invalidResults = result.results.flatMap((item) => getScenarioIntegrityErrors(item).map((error) => ({
    mode: item.mode,
    scenario: item.scenario,
    requestedConnections: item.requestedConnections,
    error,
  })));
  if (invalidResults.length) {
    console.error(JSON.stringify({ integrityErrors: invalidResults }, null, 2));
    process.exitCode = 1;
  }
}

if (process.env.CAPACITY_RUN === '1') {
  main().catch((error) => {
    console.error(error.stack || error.message);
    process.exitCode = 1;
  });
}
