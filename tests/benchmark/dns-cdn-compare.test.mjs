import test from 'node:test';
import assert from 'node:assert/strict';
import dgram from 'node:dgram';
import { once } from 'node:events';
import { queryDns, createProbePlan, isPublicV4 } from './dns-cdn-compare.mjs';

async function fakeDns(t, octet) {
  const server = dgram.createSocket('udp4');
  t.after(() => server.close());
  server.on('message', (query, peer) => {
    let end = 12;
    while (query[end] !== 0) end += query[end] + 1;
    end += 5;
    const header = Buffer.from([query[0], query[1], 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0]);
    const answer = Buffer.from([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 203, 0, 113, octet]);
    server.send(Buffer.concat([header, query.subarray(12, end), answer]), peer.port, peer.address);
  });
  server.bind(0, '127.0.0.1');
  await once(server, 'listening');
  return `127.0.0.1:${server.address().port}`;
}

test('independent DNS servers can return distinct answers for the same name', async (t) => {
  const first = await fakeDns(t, 10);
  const second = await fakeDns(t, 20);
  const [a, b] = await Promise.all([queryDns(first, 'cdn.example.test', 1000), queryDns(second, 'cdn.example.test', 1000)]);
  assert.deepEqual(a.records, [{ address: '203.0.113.10', ttl: 30 }]);
  assert.deepEqual(b.records, [{ address: '203.0.113.20', ttl: 30 }]);
});

test('probe plan interleaves DNS views and limits candidates', () => {
  const plan = createProbePlan([{ label: 'a', records: [{ address: '192.0.2.1' }, { address: '192.0.2.2' }] }, { label: 'b', records: [{ address: '198.51.100.1' }] }], 2);
  assert.deepEqual(plan.map((item) => item.label), ['a', 'b', 'a']);
});

test('public CDN probing rejects loopback and private IPv4 destinations', () => {
  for (const ip of ['127.0.0.1', '10.0.0.1', '172.16.1.1', '192.168.1.1', '169.254.1.1', '100.64.0.1', '203.0.113.1']) assert.equal(isPublicV4(ip), false, ip);
  assert.equal(isPublicV4('8.8.8.8'), true);
});
