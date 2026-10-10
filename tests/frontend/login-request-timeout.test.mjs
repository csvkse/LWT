import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { test } from 'node:test';

async function loadClient(fetch) {
  const notices = [];
  const context = vm.createContext({
    fetch, AbortController, setTimeout, clearTimeout, URLSearchParams,
    localStorage: { getItem: () => '', removeItem() {}, setItem() {} },
    window: { location: { hash: '#/login' } },
  });
  const source = await readFile(new URL('../../src/LinuxWebTool.WebHost/wwwroot/app/api/client.js', import.meta.url), 'utf8');
  const client = new vm.SourceTextModule(source, { context });
  await client.link(specifier => {
    if (specifier.endsWith('config.js')) return new vm.SyntheticModule(['API_BASE', 'LS_KEYS'], function () {
      this.setExport('API_BASE', ''); this.setExport('LS_KEYS', { token: 'token', user: 'user' });
    }, { context });
    return new vm.SyntheticModule(['toast'], function () {
      this.setExport('toast', { error: message => notices.push(message) });
    }, { context });
  });
  await client.evaluate();
  return { http: client.namespace.http, notices };
}

test('login request timeout also covers a stalled response body', async () => {
  const { http } = await loadClient((_url, { signal }) => Promise.resolve({
    status: 200, ok: true, headers: { get: () => 'application/json' },
    text: () => new Promise((_resolve, reject) => signal?.addEventListener('abort', () => reject(new Error('aborted')))),
  }));
  const result = await Promise.race([
    http('/api/Auth/Login', { method: 'POST', timeoutMs: 20 }),
    new Promise(resolve => setTimeout(() => resolve({ message: 'still pending' }), 150)),
  ]);
  assert.match(result.message, /超时/);
  assert.equal(result.ok, false);
});

test('successful login response keeps its JSON result', async () => {
  const { http } = await loadClient(async () => ({
    status: 200, ok: true, headers: { get: () => 'application/json' },
    text: async () => '{"token":"test-token"}',
  }));
  const result = await http('/api/Auth/Login', { method: 'POST', timeoutMs: 100 });
  assert.equal(result.ok, true);
  assert.equal(result.data.token, 'test-token');
});

test('login request times out when the server never sends headers', async () => {
  const { http, notices } = await loadClient((_url, { signal }) => new Promise((_resolve, reject) => {
    signal?.addEventListener('abort', () => reject(new Error('aborted')));
  }));
  const result = await Promise.race([
    http('/api/Auth/Login', { method: 'POST', timeoutMs: 20 }),
    new Promise(resolve => setTimeout(() => resolve({ message: 'still pending' }), 150)),
  ]);
  assert.equal(result.ok, false);
  assert.match(result.message, /超时/);
  assert.equal(notices.length, 1);
});
