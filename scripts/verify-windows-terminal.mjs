import assert from 'node:assert/strict';
import { setTimeout as delay } from 'node:timers/promises';
import path from 'node:path';

const base = process.env.TERMINAL_TEST_URL || 'http://127.0.0.1:15274';
const password = process.env.TERMINAL_TEST_PASSWORD;
assert.ok(password, 'Set TERMINAL_TEST_PASSWORD for the temporary test service.');
const cwd = path.resolve(process.env.TERMINAL_TEST_DIRECTORY || process.env.TEMP);
const login = await fetch(base + '/api/Auth/Login', {
  method: 'POST', headers: { 'Content-Type': 'application/json' },
  body: JSON.stringify({ username: process.env.TERMINAL_TEST_USERNAME || 'terminal-check', password }),
});
assert.equal(login.status, 200);
const { token } = await login.json();
const headers = { Authorization: 'Bearer ' + token, 'Content-Type': 'application/json' };
async function api(route, method = 'GET', body) {
  const response = await fetch(base + '/api/Terminal/' + route, {
    method, headers, ...(body ? { body: JSON.stringify(body) } : {}),
  });
  assert.ok(response.ok, `${route}: HTTP ${response.status}`);
  return response.status === 204 ? null : response.json();
}
async function wait(predicate) {
  const end = Date.now() + 15000;
  while (Date.now() < end) { if (await predicate()) return; await delay(50); }
  throw new Error('Windows terminal verification timed out.');
}
const session = await api('Sessions', 'POST', { workingDirectory: cwd });
let socket;
let output = '';
async function attach() {
  const { ticket } = await api(`Sessions/${session.sessionId}/Attachment`, 'POST');
  socket = new WebSocket(base.replace(/^http/, 'ws') + `/api/terminal/ws/${session.sessionId}?v=2&ticket=${ticket}`);
  socket.addEventListener('message', event => { output += JSON.parse(event.data).data; });
  await new Promise((resolve, reject) => {
    socket.addEventListener('open', resolve, { once: true });
    socket.addEventListener('error', reject, { once: true });
  });
}
const input = data => socket.send(JSON.stringify({ type: 'input', data }));
const details = () => api(`Sessions/${session.sessionId}`);
try {
  await attach();
  await wait(async () => (await details()).workingDirectory === cwd);
  assert.equal((await details()).nativePty, true);
  input('\x1b[I'); input('\x1b[O');
  await delay(200);
  assert.equal((await details()).hasUserInput, false);
  assert.equal((await details()).workingDirectory, cwd);
  input("Write-Output ('AOT_CHECK:' + '中文')\r");
  // PowerShell inserts color and cursor visibility sequences before the result line.
  await wait(() => output.replace(/\x1b\[[0-?]*[ -/]*[@-~]/g, '').includes('\r\nAOT_CHECK:中文\r\n'));
  await wait(async () => (await details()).workingDirectory === cwd);
  const closed = new Promise(resolve => socket.addEventListener('close', resolve, { once: true }));
  socket.close(); await closed;
  await attach();
  assert.equal((await details()).processId, session.processId);
  const files = await fetch(base + '/api/Files?path=' + encodeURIComponent(cwd), { headers });
  assert.equal(files.status, 200);
  console.log('PASS: Windows Native AOT ConPTY, Chinese output, focus/unused state, directory report, same PID reattach and file API.');
} finally {
  socket?.close();
  await api(`Sessions/${session.sessionId}`, 'DELETE');
}
