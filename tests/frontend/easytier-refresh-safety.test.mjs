import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';
import { test } from 'node:test';
import { computed, defineComponent, reactive, ref } from '../../src/LinuxWebTool.WebHost/wwwroot/app/vendor/vue.esm-browser.prod.js';

test('FE-EASYTIER-REFRESH: pending polling never creates overlapping node requests', async () => {
  let mount, tick;
  const requests = [];
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const context = vm.createContext({ setInterval: callback => { tick = callback; return 1; }, clearInterval() {} });
  const source = await readFile(new URL('../../src/LinuxWebTool.WebHost/wwwroot/app/views/EasyTierView.js', import.meta.url), 'utf8');
  const view = new vm.SourceTextModule(source, { context });
  const dependencies = {
    vue: { computed, defineComponent, reactive, ref, onMounted: callback => { mount = callback; }, onUnmounted() {} },
    '../api/client.js': { http: async (url, options) => { requests.push({ url, options }); return pending; }, httpUpload() {} },
    '../config.js': { API: { easytier: { nodes: '/nodes', engineStatus: '/engine' } } },
    '../store/toast.js': { toast: { error() {}, info() {}, success() {} } },
    '../utils/format.js': { formatBytes: String },
  };
  await view.link(name => {
    const values = dependencies[name];
    return new vm.SyntheticModule(Object.keys(values), function () {
      for (const [key, value] of Object.entries(values)) this.setExport(key, value);
    }, { context });
  });
  await view.evaluate();
  const state = view.namespace.default.setup();
  mount();
  tick();
  tick();
  assert.equal(requests.filter(r => r.url === '/nodes').length, 1);
  assert.ok(requests.find(r => r.url === '/nodes').options.timeoutMs > 0);
  release({ ok: false });
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(state.loading.value, false);
  await tick();
  assert.equal(requests.filter(r => r.url === '/nodes').length, 2);
});
