import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

test('resource discovery populates preview and sends typed configuration', async () => {
  const nodes = new Map();
  function element(id = '') {
    return { id, children: [], disabled: false, textContent: '', value: '', handlers: {},
      get options() { return this.children; },
      replaceChildren() { this.children = []; }, append(child) { this.children.push(child); },
      addEventListener(type, fn) { this.handlers[type] = fn; } };
  }
  for (const id of ['refresh', 'status', 'error', 'instances', 'images', 'pools', 'networks', 'preflight', 'check', 'preflight-result', 'preflight-reasons', 'sample', 'sample-result', 'create', 'reconcile', 'task-result', 'managed', 'managed-error', 'server-tasks', 'server-tasks-error']) nodes.set(id, element(id));
  const fields = Object.fromEntries(['imageFingerprint', 'storagePool', 'network'].map(x => [x, element(x)]));
  const form = nodes.get('preflight');
  form.elements = { ...fields, namedItem(name) { return this[name]; } };
  form.reportValidity = () => true;
  const calls = [];
  let allowed = false;
  const saved = new Map();
  const context = {
    location: { search: '?token=example' }, URLSearchParams,
    crypto: { getRandomValues(a) { a.fill(1); return a; } },
    navigator: { locks: { request(_name, callback) { return callback(); } } },
    localStorage: { getItem(k) { return saved.get(k) || null; }, setItem(k, v) { saved.set(k, v); }, removeItem(k) { saved.delete(k); } },
    document: { getElementById: id => nodes.get(id), createElement: () => element() },
    FormData: class { entries() { return Object.entries({ name:'demo', imageFingerprint:'abcdef123456', cpu:'2', memoryMiB:'512', diskGiB:'8', storagePool:'pool1', network:'br0' }); } },
    fetch: async (url, options) => {
      calls.push({ url, options });
      const body = url.endsWith('capabilities') ? {status:'Ready', version:'6'} : url.endsWith('/managed') || url.endsWith('/tasks') ? [] : url.includes('/resources?') ? {cpuThreads:8,memoryTotalBytes:8589934592,memoryUsedBytes:2147483648,poolTotalBytes:107374182400,poolUsedBytes:21474836480} : url.endsWith('discovery') ? {
        instances:[], images:[{fingerprint:'abcdef123456', description:'Debian', architecture:'x86_64'}], pools:[{name:'pool1', driver:'zfs', status:'Created'}], networks:[{name:'br0'}]
      } : url.endsWith('/preflight') ? {allowed, reasons: allowed ? [] : ['hard storage quota unverified']} : url.endsWith('/reconcile') ? {state:'Completed'} : {state:'Submitted', operation:'/1.0/operations/test'};
      return {ok:true, json:async () => body};
    },
  };
  vm.runInNewContext(await readFile(new URL('../../plugins/lxc-manager/ui/app.js', import.meta.url), 'utf8'), context);
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(fields.imageFingerprint.options.length, 1);
  assert.equal(nodes.get('check').disabled, false);
  fields.storagePool.value = 'pool1';
  await nodes.get('sample').handlers.click();
  assert.match(nodes.get('sample-result').textContent, /8/);
  await form.handlers.submit({ preventDefault() {} });
  const sent = JSON.parse(calls.at(-1).options.body);
  assert.equal(sent.cpu, 2);
  assert.equal(sent.memoryMiB, 512);
  assert.equal(sent.storagePool, 'pool1');
  assert.equal(nodes.get('preflight-result').textContent, '预检未通过：');
  assert.equal(nodes.get('preflight-reasons').children[0].textContent, '尚未验证存储池硬配额');
  assert.equal(nodes.get('create').disabled, true);
  allowed = true;
  await form.handlers.submit({ preventDefault() {} });
  assert.equal(nodes.get('create').disabled, false);
  await nodes.get('create').handlers.click();
  assert.equal(saved.size, 1);
  assert.equal(JSON.parse(calls.at(-1).options.body).key, '01010101010101010101010101010101');
  await nodes.get('reconcile').handlers.click();
  assert.equal(saved.size, 0);
  assert.match(nodes.get('task-result').textContent, /完成/);
  await form.handlers.submit({ preventDefault() {} });
  const foreign = JSON.stringify({key:'22222222222222222222222222222222', spec:{name:'other'}});
  saved.set('lwt.lxc-manager.pending-create.v1', foreign);
  const before = calls.length;
  await nodes.get('create').handlers.click();
  assert.equal(calls.length, before);
  assert.equal(saved.get('lwt.lxc-manager.pending-create.v1'), foreign);
  assert.match(nodes.get('task-result').textContent, /另一个标签页/);
});
