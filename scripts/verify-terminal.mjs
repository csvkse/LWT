import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';

const base = process.env.TERMINAL_TEST_URL || 'http://localhost:15270';
const username = process.env.TERMINAL_TEST_USERNAME || 'terminal-check';
const password = process.env.TERMINAL_TEST_PASSWORD;
if (!password) throw new Error('Set TERMINAL_TEST_PASSWORD for the temporary test service.');
let token;
async function api(path, method = 'GET', body) {
  const response = await fetch(base + '/api/Terminal/' + path, {
    method, headers: { 'Content-Type': 'application/json', ...(token ? { Authorization: 'Bearer ' + token } : {}) },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  if (!response.ok) throw new Error(`${method} ${path}: HTTP ${response.status}`);
  return response.status === 204 ? null : response.json();
}
async function waitFor(predicate, timeout = 15000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) { if (await predicate()) return; await delay(50); }
  throw new Error('Terminal verification timed out.');
}
async function attach(id, after = 0) {
  const { ticket } = await api(`Sessions/${id}/Attachment`, 'POST');
  const socket = new WebSocket(base.replace(/^http/, 'ws') + `/api/terminal/ws/${id}?v=2&ticket=${ticket}&after=${after}`);
  const connection = { socket, output: '', sequence: after, truncated: false };
  socket.addEventListener('message', event => {
    const frame = JSON.parse(event.data);
    connection.output += frame.data;
    connection.sequence = frame.sequence;
    connection.truncated ||= frame.truncated;
  });
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', () => reject(new Error('WebSocket connection failed')), { once: true });
    setTimeout(() => reject(new Error('WebSocket connection timed out')), 10000).unref();
  });
  connection.input = data => socket.send(JSON.stringify({ type: 'input', data }));
  connection.close = () => new Promise(resolve => { socket.addEventListener('close', resolve, { once: true }); socket.close(); });
  return connection;
}
const login = await fetch(base + '/api/Auth/Login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username, password }) });
assert.equal(login.status, 200);
token = (await login.json()).token;
const support = await api('Support');
assert.equal(support.platform, 'Linux');
assert.equal(support.nativePty, true, 'The container must use a real PTY.');
assert.equal(support.container, true);
const session = await api('Sessions', 'POST', { workingDirectory: '/tmp' });
let connection;
const directory = '/tmp/lwt-terminal-中文 space-' + Date.now();
try {
  connection = await attach(session.sessionId);
  await waitFor(async () => (await api('Sessions/' + session.sessionId)).workingDirectory === '/tmp');
  connection.input("printf '\\nPTY_CHECK:%s\\n' \"$(test -t 0 && echo yes || echo no)\"\r");
  await waitFor(() => connection.output.includes('\r\nPTY_CHECK:yes\r\n')).catch(error => { console.error(connection.output); throw error; });
  connection.socket.send(JSON.stringify({ type: 'resize', cols: 101, rows: 33 }));
  connection.input("printf '\\nSIZE_CHECK:%s\\n' \"$(stty size)\"\r");
  await waitFor(() => connection.output.includes('\r\nSIZE_CHECK:33 101\r\n'));
  connection.input('sleep 30\r');
  await waitFor(() => connection.output.includes('sleep 30'));
  await delay(200);
  connection.input('\x03');
  connection.input("printf '\\nINTERRUPT_CHECK\\n'\r");
  await waitFor(() => connection.output.includes('\r\nINTERRUPT_CHECK\r\n'), 5000);
  const beforeTop = connection.output.length;
  connection.input('top\r');
  await waitFor(() => /PID\s+USER/.test(connection.output.slice(beforeTop)), 8000);
  const beforeQuit = connection.output.length;
  connection.input('q');
  await waitFor(() => connection.output.slice(beforeQuit).includes('\x1b]133;A\x07'), 5000);
  connection.input("printf '\\nFULLSCREEN_CHECK\\n'\r");
  await waitFor(() => connection.output.includes('\r\nFULLSCREEN_CHECK\r\n'), 5000);
  connection.input(`mkdir -- '${directory}'; cd -- '${directory}'\r`);
  await waitFor(async () => (await api('Sessions/' + session.sessionId)).workingDirectory === directory);
  const listing = await fetch(base + '/api/Files?path=' + encodeURIComponent(directory), { headers: { Authorization: 'Bearer ' + token } });
  assert.equal(listing.status, 200);
  console.log('PASS: real PTY, resize, UTF-8 directory reports and file API.');
  connection.input("for i in $(seq 1 60); do printf '\\nBACKGROUND_TICK:%s\\n' \"$i\"; sleep 1; done; printf '\\nBACKGROUND_DONE\\n'\r");
  await waitFor(() => connection.output.includes('\r\nBACKGROUND_TICK:1\r\n'));
  const cursor = connection.sequence;
  await connection.close();
  connection = null;
  await waitFor(async () => (await api('Sessions/' + session.sessionId)).state === 'RunningDetached');
  console.log('Checking detached execution for 60 seconds…');
  await delay(61000);
  const detached = await api('Sessions/' + session.sessionId);
  assert.equal(detached.processId, session.processId);
  assert.equal(detached.hasUserInput, true);
  assert.ok(detached.lastSequence > cursor);
  connection = await attach(session.sessionId, cursor);
  await waitFor(() => connection.output.includes('\r\nBACKGROUND_DONE\r\n'));
  console.log('PASS: 60-second detach, same PID, replay and reconnect.');
  connection.input("seq 1 200000; printf '\\nOUTPUT_DRAINED\\n'\r");
  await connection.close();
  connection = null;
  await delay(3000);
  connection = await attach(session.sessionId);
  await waitFor(() => connection.output.includes('\r\nOUTPUT_DRAINED\r\n'));
  assert.ok((await api('Sessions/' + session.sessionId)).bufferTruncated);
  assert.equal(connection.truncated, true);
  console.log('PASS: continuous detached output drains and bounded replay reports truncation.');
  connection.input(`cd /tmp; rmdir -- '${directory}'\r`);
  await waitFor(async () => (await api('Sessions/' + session.sessionId)).workingDirectory === '/tmp');
} catch (error) {
  if (connection) console.error(connection.output.slice(-8192));
  throw error;
} finally {
  if (connection) await connection.close();
  await api('Sessions/' + session.sessionId, 'DELETE');
}
const unused = await api('Sessions', 'POST', { workingDirectory: '/tmp' });
await waitFor(async () => (await api('Sessions/' + unused.sessionId)).workingDirectory === '/tmp');
await waitFor(async () => !(await api('Sessions')).some(s => s.sessionId === unused.sessionId), 10000);
console.log('PASS: untouched idle terminal is reclaimed.');
