import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';

if (typeof window === 'undefined') {
  globalThis.window = {
    location: {
      origin: 'http://localhost:5000',
    },
  };
}

const { API } = await import('../../src/LinuxWebTool.WebHost/wwwroot/app/config.js');
const appDir = path.resolve('src/LinuxWebTool.WebHost/wwwroot/app');

test('NATIVE-FE-01: PluginCreateModal structure and required fields', () => {
  const filePath = path.join(appDir, 'components', 'plugins', 'PluginCreateModal.js');
  assert.ok(fs.existsSync(filePath), 'PluginCreateModal.js must exist');
  const code = fs.readFileSync(filePath, 'utf8');

  assert.ok(code.includes("name: 'PluginCreateModal'"));
  assert.ok(code.includes('创建新插件'));
  assert.ok(code.includes('form.value.runtime'));
  assert.ok(code.includes('form.value.entrypoint'));
  assert.ok(code.includes('form.supportHttp'));
  assert.ok(code.includes('form.autoRestart'));
  assert.ok(code.includes('form.enableKv'));
  assert.ok(code.includes('form.enableSqlite'));
});

test('NATIVE-FE-02: NativeCommandEditor displays command bindings tabs', () => {
  const filePath = path.join(appDir, 'components', 'services', 'NativeCommandEditor.js');
  assert.ok(fs.existsSync(filePath), 'NativeCommandEditor.js must exist');
  const code = fs.readFileSync(filePath, 'utf8');

  assert.ok(code.includes("name: 'NativeCommandEditor'"));
  assert.ok(code.includes('参数绑定'));
  assert.ok(code.includes('标准输入'));
  assert.ok(code.includes('产物规则'));
  assert.ok(code.includes('ProgramRef'));
});

test('NATIVE-FE-03: PluginOperationPanel supports status badges, progress and artifacts', () => {
  const filePath = path.join(appDir, 'components', 'services', 'PluginOperationPanel.js');
  assert.ok(fs.existsSync(filePath), 'PluginOperationPanel.js must exist');
  const code = fs.readFileSync(filePath, 'utf8');

  assert.ok(code.includes("name: 'PluginOperationPanel'"));
  assert.ok(code.includes('getStatusBadge'));
  assert.ok(code.includes('succeeded'));
  assert.ok(code.includes('running'));
  assert.ok(code.includes('queued'));
  assert.ok(code.includes('artifactRefs'));
  assert.ok(code.includes('download-artifact'));
});

test('NATIVE-FE-04: API config declares pluginOperations endpoints matching backend', () => {
  assert.ok(API.pluginOperations);
  assert.equal(API.pluginOperations.submit, '/PluginOperations/Submit');
  assert.equal(API.pluginOperations.item('op-123'), '/PluginOperations/op-123');
  assert.equal(API.pluginOperations.cancel('op-123'), '/PluginOperations/op-123/Cancel');
  assert.equal(API.pluginOperations.artifactDownload('op-123', 'art-1'), '/PluginOperations/op-123/Artifacts/art-1');
});

test('NATIVE-FE-05: PluginActionModal handles async operations and artifacts', () => {
  const filePath = path.join(appDir, 'components', 'plugins', 'PluginActionModal.js');
  assert.ok(fs.existsSync(filePath), 'PluginActionModal.js must exist');
  const code = fs.readFileSync(filePath, 'utf8');

  assert.ok(code.includes('pollOperation'));
  assert.ok(code.includes('getArtifactDownloadUrl'));
  assert.ok(code.includes('API.pluginOperations.item'));
  assert.ok(code.includes('API.pluginOperations.artifactDownload'));
});
