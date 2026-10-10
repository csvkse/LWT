import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { request } from '../../scripts/smoke-http.mjs';

test('success contract rejects 401, 404, 405 and 500', async () => {
  const server = createServer((req, res) => { res.writeHead(Number(req.url.slice(1))); res.end('{}'); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    for (const status of [401, 404, 405, 500]) await assert.rejects(request(base, '/' + status), /expected HTTP 200/);
    assert.deepEqual(await request(base, '/200'), {});
    assert.deepEqual(await request(base, '/401', { status: 401 }), {});
  } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});

test('timeout covers a response body that never completes', async () => {
  const server = createServer((req, res) => { res.writeHead(200); res.write('{'); });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  try {
    await assert.rejects(request(`http://127.0.0.1:${server.address().port}`, '/', { timeout: 100 }), /abort|timeout/i);
  } finally { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
});
