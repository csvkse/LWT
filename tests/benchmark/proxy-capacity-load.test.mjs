import assert from 'node:assert/strict';
import net from 'node:net';
import test from 'node:test';
import { connectTunnel, getScenarioIntegrityErrors, startDuplex } from './proxy-capacity-load.mjs';

async function listen(server) {
  server.listen(0, '127.0.0.1');
  await new Promise((resolve) => server.once('listening', resolve));
  return server.address().port;
}

function createMockProxy(mode) {
  const sockets = new Set();
  const server = net.createServer((socket) => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
    let received = Buffer.alloc(0);
    let phase = mode === 'socks5' ? 'greeting' : 'request';

    socket.on('data', (chunk) => {
      received = Buffer.concat([received, chunk]);
      if (phase === 'greeting' && received.length >= 3) {
        received = received.subarray(3);
        phase = 'connect';
        socket.write(Buffer.from([0x05]));
        setImmediate(() => socket.write(Buffer.from([0x00])));
      }
      if (phase === 'connect' && received.length >= 10) {
        received = received.subarray(10);
        phase = 'tunnel';
        socket.write(Buffer.from([0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0x4a, 0x42]));
        setImmediate(() => socket.write('ready'));
      }
      if (phase === 'request') {
        const headerEnd = received.indexOf('\r\n\r\n');
        if (headerEnd >= 0) {
          received = received.subarray(headerEnd + 4);
          phase = 'tunnel';
          socket.write('HTTP/1.1 200 Connection Established\r\n\r\nready');
        }
      }
      if (phase === 'tunnel' && received.length) {
        socket.write(received);
        received = Buffer.alloc(0);
      }
    });
  });

  return {
    server,
    sockets,
    async start() {
      return listen(server);
    },
    async close() {
      for (const socket of sockets) socket.destroy();
      await new Promise((resolve) => server.close(resolve));
    },
  };
}

async function readNextData(socket, timeoutMs = 1000) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => finish(new Error('timed out waiting for socket data')), timeoutMs);
    const onData = (data) => finish(null, data);
    const onError = (error) => finish(error);
    const onClose = () => finish(new Error('socket closed before data arrived'));
    function finish(error, data) {
      clearTimeout(timer);
      socket.removeListener('data', onData);
      socket.removeListener('error', onError);
      socket.removeListener('close', onClose);
      if (error) reject(error);
      else resolve(data);
    }
    socket.once('data', onData);
    socket.once('error', onError);
    socket.once('close', onClose);
  });
}

for (const mode of ['socks5', 'http-connect']) {
  test(`${mode} tunnel handshake preserves bytes that arrive with the handshake response`, async (t) => {
    const proxy = createMockProxy(mode);
    const proxyPort = await proxy.start();
    t.after(() => proxy.close());

    const socket = await connectTunnel({
      mode,
      proxyHost: '127.0.0.1',
      proxyPort,
      targetHost: '127.0.0.1',
      targetPort: 19082,
      timeoutMs: 1000,
    });
    t.after(() => socket.destroy());

    assert.equal((await readNextData(socket)).toString(), 'ready');
    socket.write('echo-check');
    assert.equal((await readNextData(socket)).toString(), 'echo-check');
  });
}

test('tunnel handshake timeout closes the client socket and rejects', async (t) => {
  const sockets = new Set();
  const server = net.createServer((socket) => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
  });
  const proxyPort = await listen(server);
  t.after(async () => {
    for (const socket of sockets) socket.destroy();
    await new Promise((resolve) => server.close(resolve));
  });

  await assert.rejects(connectTunnel({
    mode: 'socks5',
    proxyHost: '127.0.0.1',
    proxyPort,
    targetHost: '127.0.0.1',
    targetPort: 19082,
    timeoutMs: 50,
  }), /timed out/i);
});

test('capacity result rejects echo bytes that were sent but not received', () => {
  const errors = getScenarioIntegrityErrors({
    requestedConnections: 2,
    successfulConnections: 2,
    failedConnections: 0,
    duplex: { sentBytes: 128, receivedBytes: 64, mismatchedPackets: 0, partialBytes: 0 },
  });

  assert.deepEqual(errors, ['duplex byte count differs: sent 128, received 64']);
});

test('capacity result accepts fully drained and matching echo traffic', () => {
  const errors = getScenarioIntegrityErrors({
    requestedConnections: 2,
    successfulConnections: 2,
    failedConnections: 0,
    duplex: { sentBytes: 128, receivedBytes: 128, mismatchedPackets: 0, partialBytes: 0 },
  });

  assert.deepEqual(errors, []);
});

test('duplex writer continues receiving echo data after sending stops', async () => {
  const server = net.createServer((socket) => socket.pipe(socket));
  const port = await listen(server);

  const socket = net.createConnection({ host: '127.0.0.1', port });
  socket.on('error', () => {});
  try {
    await new Promise((resolve, reject) => {
      socket.once('connect', resolve);
      socket.once('error', reject);
    });

    const writer = startDuplex(socket, 33);
    await new Promise((resolve) => setTimeout(resolve, 250));
    writer.stop();

    const deadline = Date.now() + 1000;
    while (writer.snapshot().receivedBytes < writer.snapshot().sentBytes && Date.now() < deadline) {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
    assert.equal(writer.snapshot().receivedBytes, writer.snapshot().sentBytes);
  } finally {
    socket.destroy();
    await new Promise((resolve) => server.close(resolve));
  }
});
