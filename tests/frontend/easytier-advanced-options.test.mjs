import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { test } from 'node:test';
import { computed, defineComponent, reactive, ref } from '../../src/LinuxWebTool.WebHost/wwwroot/app/vendor/vue.esm-browser.prod.js';

const viewUrl = new URL('../../src/LinuxWebTool.WebHost/wwwroot/app/views/EasyTierView.js', import.meta.url);

function jsonCopy(obj) {
  return JSON.parse(JSON.stringify(obj));
}

async function loadView(config = null, edit = false) {
  const requests = [];
  const context = vm.createContext({});
  const view = new vm.SourceTextModule(await readFile(viewUrl, 'utf8'), { context });
  const exportsByModule = {
    vue: { computed, defineComponent, reactive, ref, onMounted() {}, onUnmounted() {} },
    '../api/client.js': {
      http: async (url, options = {}) => {
        requests.push({ url, ...options });
        return options.method === 'GET'
          ? { ok: true, data: { config: config || {} } }
          : { ok: true, data: { success: true } };
      },
      httpUpload() {},
    },
    '../config.js': { API: { easytier: { nodes: '/nodes', nodeItem: id => `/nodes/${id}`, availablePort: '/port' } } },
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
  if (edit) {
    await state.openEditNode({ id: 'test-node-1' });
  } else {
    await state.openCreateNode();
    if (config) Object.assign(state.nodeForm, config);
  }
  return { state, requests };
}

test('FE-ADV-01: routingMode auto sends auto mode with empty routes', async () => {
  const { state, requests } = await loadView({
    instanceName: 'test-auto',
    networkName: 'net-auto',
    routingMode: 'auto',
    routesText: '10.0.0.0/8', // should be ignored when auto
  });
  await state.saveNode();
  const post = requests.find(r => r.method === 'POST');
  assert.ok(post);
  assert.equal(post.body.routingMode, 'auto');
  assert.deepEqual(jsonCopy(post.body.routes), []);
});

test('FE-ADV-02: routingMode virtual_only sends virtual_only mode with empty routes', async () => {
  const { state, requests } = await loadView({
    instanceName: 'test-vo',
    networkName: 'net-vo',
    routingMode: 'virtual_only',
    routesText: '10.0.0.0/8', // should be ignored when virtual_only
  });
  await state.saveNode();
  const post = requests.find(r => r.method === 'POST');
  assert.ok(post);
  assert.equal(post.body.routingMode, 'virtual_only');
  assert.deepEqual(jsonCopy(post.body.routes), []);
});

test('FE-ADV-03: routingMode manual sends manual mode with specified routes', async () => {
  const { state, requests } = await loadView({
    instanceName: 'test-manual',
    networkName: 'net-manual',
    routingMode: 'manual',
    routesText: '10.0.0.0/8\n192.168.100.0/24',
  });
  await state.saveNode();
  const post = requests.find(r => r.method === 'POST');
  assert.ok(post);
  assert.equal(post.body.routingMode, 'manual');
  assert.deepEqual(jsonCopy(post.body.routes), ['10.0.0.0/8', '192.168.100.0/24']);
});

test('FE-ADV-04: subnet proxy mapping table add/remove and serialization', async () => {
  const { state, requests } = await loadView({
    instanceName: 'test-proxy',
    networkName: 'net-proxy',
  });
  assert.equal(state.nodeForm.proxyNetworkRules.length, 0);

  state.addProxyRule();
  state.addProxyRule();
  assert.equal(state.nodeForm.proxyNetworkRules.length, 2);

  state.nodeForm.proxyNetworkRules[0].cidr = '192.168.1.0/24';
  state.nodeForm.proxyNetworkRules[0].mappedCidr = '10.200.1.0/24';

  state.nodeForm.proxyNetworkRules[1].cidr = '192.168.2.0/24';
  state.nodeForm.proxyNetworkRules[1].mappedCidr = ''; // empty mapped cidr

  state.addProxyRule();
  state.nodeForm.proxyNetworkRules[2].cidr = '192.168.3.0/24';
  state.removeProxyRule(2);
  assert.equal(state.nodeForm.proxyNetworkRules.length, 2);

  await state.saveNode();
  const post = requests.find(r => r.method === 'POST');
  assert.ok(post);
  assert.deepEqual(jsonCopy(post.body.proxyNetworkRules), [
    { cidr: '192.168.1.0/24', mappedCidr: '10.200.1.0/24' },
    { cidr: '192.168.2.0/24', mappedCidr: null },
  ]);
  assert.deepEqual(jsonCopy(post.body.proxyNetworks), ['192.168.1.0/24', '192.168.2.0/24']);
});

test('FE-ADV-05: advancedConfig flags and properties serialization', async () => {
  const { state, requests } = await loadView({
    instanceName: 'test-adv',
    networkName: 'net-adv',
    hostname: 'nas-server',
    virtualIpv6: 'fd00:1::1/64',
    mappedListenersText: 'tcp://203.0.113.1:11010\nudp://203.0.113.1:11010',
    advancedConfig: {
      devName: 'et_custom0',
      mtu: 1420,
      enableIpv6: true,
      latencyFirst: true,
      bindDevice: true,
      p2pMode: 'p2p_only',
      defaultProtocol: 'udp',
      enableUpnp: false,
      enableTcpHolePunching: false,
      enableUdpHolePunching: false,
      enableSymHolePunching: false,
    },
  });

  await state.saveNode();
  const post = requests.find(r => r.method === 'POST');
  assert.ok(post);
  assert.equal(post.body.hostname, 'nas-server');
  assert.equal(post.body.virtualIpv6, 'fd00:1::1/64');
  assert.deepEqual(jsonCopy(post.body.mappedListeners), ['tcp://203.0.113.1:11010', 'udp://203.0.113.1:11010']);
  assert.deepEqual(jsonCopy(post.body.advancedConfig), {
    devName: 'et_custom0',
    mtu: 1420,
    enableIpv6: true,
    latencyFirst: true,
    bindDevice: true,
    p2pMode: 'p2p_only',
    defaultProtocol: 'udp',
    enableUpnp: false,
    enableTcpHolePunching: false,
    enableUdpHolePunching: false,
    enableSymHolePunching: false,
  });
});

test('FE-ADV-06: openEditNode backfills legacy and new structures', async () => {
  // 1. Legacy node with proxyNetworks only
  const legacyConfig = {
    id: 'legacy-node',
    instanceName: 'legacy-node',
    networkName: 'net-legacy',
    enableDhcp: true,
    proxyNetworks: ['192.168.10.0/24'],
    routingMode: 'auto',
  };
  const { state: legacyState } = await loadView(legacyConfig, true);
  assert.equal(legacyState.nodeForm.proxyNetworkRules.length, 1);
  assert.equal(legacyState.nodeForm.proxyNetworkRules[0].cidr, '192.168.10.0/24');
  assert.equal(legacyState.nodeForm.proxyNetworkRules[0].mappedCidr, '');

  // 2. Modern node with proxyNetworkRules, routingMode, hostname, advancedConfig
  const modernConfig = {
    id: 'modern-node',
    instanceName: 'modern-node',
    networkName: 'net-modern',
    enableDhcp: false,
    hostname: 'edge-gateway',
    virtualIpv6: 'fd00:2::1/64',
    routingMode: 'virtual_only',
    mappedListeners: ['tcp://1.2.3.4:11010'],
    proxyNetworkRules: [
      { cidr: '192.168.1.0/24', mappedCidr: '10.200.1.0/24' },
    ],
    advancedConfig: {
      devName: 'et_gw0',
      mtu: 1400,
      enableIpv6: true,
      latencyFirst: false,
      bindDevice: true,
      p2pMode: 'need_p2p',
      defaultProtocol: 'tcp',
      enableUpnp: true,
      enableTcpHolePunching: true,
      enableUdpHolePunching: true,
      enableSymHolePunching: false,
    },
  };
  const { state: modernState, requests: modernRequests } = await loadView(modernConfig, true);
  assert.equal(modernState.nodeForm.hostname, 'edge-gateway');
  assert.equal(modernState.nodeForm.virtualIpv6, 'fd00:2::1/64');
  assert.equal(modernState.nodeForm.routingMode, 'virtual_only');
  assert.equal(modernState.nodeForm.mappedListenersText, 'tcp://1.2.3.4:11010');
  assert.equal(modernState.nodeForm.proxyNetworkRules.length, 1);
  assert.equal(modernState.nodeForm.proxyNetworkRules[0].cidr, '192.168.1.0/24');
  assert.equal(modernState.nodeForm.proxyNetworkRules[0].mappedCidr, '10.200.1.0/24');
  assert.equal(modernState.nodeForm.advancedConfig.devName, 'et_gw0');
  assert.equal(modernState.nodeForm.advancedConfig.p2pMode, 'need_p2p');

  await modernState.saveNode();
  const put = modernRequests.find(r => r.method === 'PUT');
  assert.ok(put);
  assert.equal(put.url, '/nodes/test-node-1');
  assert.equal(put.body.hostname, 'edge-gateway');
  assert.equal(put.body.routingMode, 'virtual_only');
  assert.deepEqual(jsonCopy(put.body.proxyNetworkRules), [{ cidr: '192.168.1.0/24', mappedCidr: '10.200.1.0/24' }]);
});
