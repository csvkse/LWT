import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { test } from 'node:test';
import { computed, defineComponent, reactive, ref } from '../../src/LinuxWebTool.WebHost/wwwroot/app/vendor/vue.esm-browser.prod.js';

const viewUrl = new URL('../../src/LinuxWebTool.WebHost/wwwroot/app/views/EasyTierView.js', import.meta.url);

async function loadView(config, edit, source = null) {
  const requests = [];
  const context = vm.createContext({});
  const view = new vm.SourceTextModule(source ?? await readFile(viewUrl, 'utf8'), { context });
  const exportsByModule = {
    vue: { computed, defineComponent, reactive, ref, onMounted() {}, onUnmounted() {} },
    '../api/client.js': {
      http: async (url, options = {}) => {
        requests.push({ url, ...options });
        // 不触发保存成功后的后台刷新，聚焦提交边界。
        return options.method === 'GET'
          ? { ok: true, data: { config } }
          : { ok: false, data: { message: 'test response' } };
      },
      httpUpload() {},
    },
    '../config.js': { API: { easytier: { nodes: '/nodes', nodeItem: id => `/nodes/${id}` } } },
    '../store/toast.js': { toast: { error() {}, info() {}, success() {} } },
    '../utils/format.js': { formatBytes: value => String(value) },
  };
  await view.link(specifier => {
    const values = exportsByModule[specifier];
    assert.ok(values, `Unexpected dependency: ${specifier}`);
    return new vm.SyntheticModule(Object.keys(values), function () {
      for (const [name, value] of Object.entries(values)) this.setExport(name, value);
    }, { context });
  });
  await view.evaluate();
  const state = view.namespace.default.setup();
  if (edit) await state.openEditNode({ id: 'existing-node' });
  else Object.assign(state.nodeForm, config);
  return { state, requests };
}

async function checkSubmission({ ipv4, dhcp, expectedDhcp, edit, raw = '', source = null }) {
  const { state, requests } = await loadView({
    instanceName: 'static-ip-gate', networkName: 'gate-network', networkSecret: '',
    virtualIpv4: ipv4, enableDhcp: dhcp, rawTomlOverride: raw, autoStart: false,
  }, edit, source);
  await state.saveNode();
  const writes = requests.filter(request => ['POST', 'PUT'].includes(request.method));
  assert.equal(writes.length, 1);
  const request = writes[0];
  assert.equal(request.method, edit ? 'PUT' : 'POST');
  assert.equal(request.url, edit ? '/nodes/existing-node' : '/nodes');
  assert.equal(request.body.virtualIpv4, ipv4.trim() || null);
  assert.equal(request.body.enableDhcp, expectedDhcp, 'Static IPv4 must disable DHCP in the submitted request');
  assert.equal(request.body.rawTomlOverride, raw.trim() || null);
  assert.equal(state.saving.value, false);
}

for (const edit of [false, true]) {
  for (const [name, ipv4, dhcp, expectedDhcp] of [
    ['static address with default DHCP', '10.126.127.1/24', true, false],
    ['static address with DHCP disabled', '10.126.127.1/24', false, false],
    ['static address with surrounding spaces', ' 10.126.127.1/24 ', true, false],
    ['empty address with DHCP', '', true, true],
    ['whitespace address with DHCP', '   ', true, true],
    ['empty address without DHCP', '', false, false],
  ]) {
    test(`${edit ? 'update' : 'create'}: ${name}`, async () => {
      await checkSubmission({ ipv4, dhcp, expectedDhcp, edit });
    });
  }
  test(`${edit ? 'update' : 'create'}: raw TOML remains an explicit override`, async () => {
    await checkSubmission({ ipv4: '10.126.127.1/24', dhcp: true, expectedDhcp: false, edit,
      raw: 'ipv4 = "10.126.127.2/24"\ndhcp = true' });
  });
}

test('gate rejects the previous static-address/DHCP regression', async () => {
  const source = await readFile(viewUrl, 'utf8');
  const regressed = source.replace(
    'enableDhcp: nodeForm.enableDhcp && !nodeForm.virtualIpv4.trim(),',
    'enableDhcp: nodeForm.enableDhcp,');
  assert.notEqual(regressed, source, 'Regression fixture must actually change the submitted DHCP flag');
  await assert.rejects(
    checkSubmission({ ipv4: '10.126.127.1/24', dhcp: true, expectedDhcp: false, edit: false, source: regressed }),
    { code: 'ERR_ASSERTION', message: /Static IPv4 must disable DHCP/ });
});
