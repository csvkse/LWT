import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import { pathToFileURL } from 'node:url';
import { createReport, measure, saveReport } from './smoke-performance.mjs';

// Status AND body are contracts: an authentication error cannot pass a success case.
export async function request(base, path, { method = 'GET', body, token, status = 200, timeout = 10000 } = {}) {
  const response = await fetch(base + path, {
    method, signal: AbortSignal.timeout(timeout),
    headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: `Bearer ${token}` } : {}) },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  assert.equal(response.status, status, `${method} ${path}: expected HTTP ${status}`);
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

export async function smoke(base, username, password, report) {
  assert.ok(password, 'SMOKE_PASSWORD is required');
  const deadline = Date.now() + 60000;
  const launchedAt = Number(process.env.SMOKE_LAUNCH_EPOCH_MS) || Date.now();
  while (true) {
    try {
      if (report) report.startup.probes++;
      const health = await fetch(base + '/health', { signal: AbortSignal.timeout(2000) });
      assert.equal(health.status, 200);
      if (report) { report.startup.readyMs = Date.now() - launchedAt; report.startup.ready = true; }
      break;
    } catch (error) {
      if (Date.now() >= deadline) throw error;
      await delay(500);
    }
  }
  await request(base, '/api/Commands', { status: 401 });
  await request(base, '/api/Auth/Login', { method: 'POST', body: { username, password: password + '-wrong' }, status: 401 });
  const login = await request(base, '/api/Auth/Login', { method: 'POST', body: { username, password } });
  assert.ok(typeof login.token === 'string' && login.token.length > 20, 'Login must return a token');
  const token = login.token;
  const api = (path, options = {}) => request(base, path, { token, ...options });
  await api('/api/Auth/Check');
  for (const path of ['/api/Commands', '/api/Groups', '/api/Schedules', '/api/SmbMounts', '/api/EasyTier/Nodes']) {
    assert.ok(Array.isArray(await api(path)), `${path} must return an array`);
  }
  for (const path of ['/api/Overview', '/api/SystemStatus', '/api/History?page=1&pageSize=20', '/api/Logs/Operations?page=1&pageSize=20']) {
    assert.ok(await api(path), `${path} must return JSON`);
  }
  const name = 'ci-smoke-' + crypto.randomUUID();
  let id;
  try {
    const created = await api('/api/Commands', { method: 'POST', body: { name, commandText: 'echo ci-smoke-marker', timeoutSeconds: 5 } });
    id = created.id;
    assert.match(id, /^[0-9a-f-]{36}$/i);
    assert.ok((await api('/api/Commands')).some(item => item.id === id && item.name === name));
    const execution = await api(`/api/Commands/${id}/Execute`, { method: 'POST', body: {} });
    assert.equal(execution.exitCode, 0);
    assert.match(execution.standardOutput, /ci-smoke-marker/);
    const updated = await api(`/api/Commands/${id}`, { method: 'PUT', body: { name: name + '-updated', commandText: 'echo updated', timeoutSeconds: 5 } });
    assert.equal(updated.name, name + '-updated');
  } finally {
    if (id) await api(`/api/Commands/${id}`, { method: 'DELETE' });
  }
  assert.ok(!(await api('/api/Commands')).some(item => item.id === id), 'Delete must remove the command');
  const directory = process.env.SMOKE_FILE_DIRECTORY || '/tmp';
  const file = directory.replace(/[\\/]$/, '') + '/' + name + '.txt';
  const fileQuery = '/api/Files?path=' + encodeURIComponent(file);
  let written = false;
  try {
    await api('/api/Files/Content', { method: 'POST', body: { path: file, content: name } });
    written = true;
    assert.equal((await api('/api/Files/Content?path=' + encodeURIComponent(file))).content, name);
  } finally {
    if (written) await api(fileQuery, { method: 'DELETE' });
  }
  await api('/api/Files/Content?path=' + encodeURIComponent(file), { status: 404 });
  console.log('HTTP smoke passed: readiness, authentication, core reads, command CRUD/execution and file write/read/delete');
  return token;
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const report = createReport();
  const base = process.env.SMOKE_URL || 'http://127.0.0.1:15270';
  const username = process.env.SMOKE_USERNAME || 'admin';
  const password = process.env.SMOKE_PASSWORD;
  try {
    const token = await smoke(base, username, password, report);
    await measure(report, endpoint => endpoint === 'login'
      ? request(base, '/api/Auth/Login', { method: 'POST', body: { username, password } })
      : request(base, { authCheck: '/api/Auth/Check', commands: '/api/Commands', easyTierNodes: '/api/EasyTier/Nodes' }[endpoint], { token }));
    report.status = 'passed';
    console.log('Performance smoke recorded: 20 samples per endpoint');
  } catch (error) {
    report.status = 'failed';
    // Do not persist URLs, payloads, passwords, tokens or arbitrary exception messages.
    report.failureType = error.name;
    throw error;
  } finally {
    await saveReport(report, process.env.SMOKE_PERF_OUTPUT || 'artifacts/smoke/performance.json');
  }
}
