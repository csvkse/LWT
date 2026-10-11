import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { test } from 'node:test';
import vm from 'node:vm';

const appDir = path.resolve('src/LinuxWebTool.WebHost/wwwroot/app');
const viewsDir = path.join(appDir, 'views');
const vuePath = 'file:///' + path.join(appDir, 'vendor', 'vue.esm-browser.prod.js').replace(/\\/g, '/');

// Mock DOM environment for Vue 3 browser template compiler entity decoding
let inner = '';
globalThis.document = {
  createElement: () => ({
    get innerHTML() { return inner; },
    set innerHTML(v) { inner = v; },
    get textContent() { return inner; },
    get children() {
      return [{
        getAttribute: (attr) => {
          const match = inner.match(/foo="([^"]*)"/);
          return match ? match[1] : '';
        }
      }];
    }
  })
};

const vue = await import(vuePath);

test('FE-E2E-01: all 16 views compile templates to render functions with zero syntax errors', () => {
  const viewFiles = fs.readdirSync(viewsDir).filter(f => f.endsWith('.js'));
  assert.ok(viewFiles.length >= 16, `Expected at least 16 views, found ${viewFiles.length}`);

  const compilationErrors = [];

  for (const file of viewFiles) {
    const filePath = path.join(viewsDir, file);
    const content = fs.readFileSync(filePath, 'utf8');
    const templateMatch = content.match(/template:\s*(`[\s\S]*?`)\s*,\s*\n*\}\);/);
    if (!templateMatch) continue;

    let template;
    try {
      template = eval(templateMatch[1]);
    } catch (evalErr) {
      compilationErrors.push({ file, stage: 'evalTemplate', error: evalErr.message });
      continue;
    }

    let generatedCode = '';
    const origFunction = globalThis.Function;
    globalThis.Function = function (...args) {
      generatedCode = args[args.length - 1];
      return origFunction.apply(this, args);
    };

    try {
      vue.compile(template);
    } catch (compileErr) {
      compilationErrors.push({ file, stage: 'vueCompile', error: compileErr.message });
    } finally {
      globalThis.Function = origFunction;
    }
  }

  assert.deepEqual(compilationErrors, [], `Vue template compilation failed in: ${JSON.stringify(compilationErrors, null, 2)}`);
});

test('FE-E2E-02: SmbMountsView SFTP privateKey template contains no unescaped newlines and compiles cleanly', () => {
  const filePath = path.join(viewsDir, 'SmbMountsView.js');
  const content = fs.readFileSync(filePath, 'utf8');

  // Must not have multi-line raw unescaped newlines in placeholder attribute
  assert.equal(
    /:placeholder="[^"]*\\n[^"]*"/.test(content),
    false,
    'SmbMountsView.js must not contain raw unescaped \\n inside template attribute bindings'
  );

  const template = eval(content.match(/template:\s*(`[\s\S]*?`)\s*,\s*\n*\}\);/)[1]);
  assert.doesNotThrow(() => {
    vue.compile(template);
  });
});

test('FE-E2E-03: BackupSyncView conforms strictly to Cyber Dark theme design tokens', () => {
  const filePath = path.join(viewsDir, 'BackupSyncView.js');
  const content = fs.readFileSync(filePath, 'utf8');

  // No raw bg-white classes (excluding opacity modifiers like bg-white/5)
  const rawBgWhiteMatches = [...content.matchAll(/\bclass="[^"]*\bbg-white\b(?!\/)[^"]*"/g)];
  assert.equal(
    rawBgWhiteMatches.length,
    0,
    `BackupSyncView contains raw bg-white classes: ${rawBgWhiteMatches.map(m => m[0]).join(', ')}`
  );

  // No gray border classes from default Tailwind light palette
  const borderGrayMatches = [...content.matchAll(/\bclass="[^"]*\bborder-gray-(?:200|300|600|700|800)\b[^"]*"/g)];
  assert.equal(
    borderGrayMatches.length,
    0,
    `BackupSyncView contains legacy border-gray classes: ${borderGrayMatches.map(m => m[0]).join(', ')}`
  );

  // Must use Cyber Dark design classes: .panel, .btn, .btn-primary, .data-table, .input
  assert.ok(content.includes('panel p-4'), 'BackupSyncView must use panel class for metric cards');
  assert.ok(content.includes('btn btn-primary'), 'BackupSyncView must use btn-primary class for primary actions');
  assert.ok(content.includes('data-table'), 'BackupSyncView must use data-table class for tables');
  assert.ok(content.includes('border-cyber-line'), 'BackupSyncView must use border-cyber-line class');
});

test('FE-E2E-04: BackupSyncView FilePicker event contracts are correctly wired', () => {
  const filePath = path.join(viewsDir, 'BackupSyncView.js');
  const content = fs.readFileSync(filePath, 'utf8');

  // FilePicker emits 'select' and 'close'
  assert.ok(content.includes('@select="onPickerConfirm($event)"'), 'FilePicker must listen to @select');
  assert.ok(content.includes('@close="picker.show = false"'), 'FilePicker must listen to @close');

  // Dead events must not be used
  assert.equal(content.includes('@confirm="onPickerConfirm($event)"'), false, 'Dead @confirm event must not be used');
  assert.equal(content.includes('@cancel="picker.show = false"'), false, 'Dead @cancel event must not be used');
});

test('FE-E2E-05: all router views define valid component definitions with setup and template', async () => {
  const routerPath = path.join(appDir, 'router', 'index.js');
  const routerContent = fs.readFileSync(routerPath, 'utf8');

  // Extract all component dynamic imports from router/index.js
  const routeImports = [...routerContent.matchAll(/import\('(\.\.\/views\/[^']+)'\)/g)].map(m => m[1]);
  assert.ok(routeImports.length >= 15, `Expected at least 15 routes, found ${routeImports.length}`);

  for (const relImport of routeImports) {
    const fullPath = path.resolve(path.join(appDir, 'router'), relImport);
    assert.ok(fs.existsSync(fullPath), `Route target file does not exist: ${fullPath}`);

    const fileContent = fs.readFileSync(fullPath, 'utf8');
    assert.ok(fileContent.includes('export default defineComponent'), `${relImport} must export default defineComponent`);
    assert.ok(fileContent.includes('setup(') || fileContent.includes('setup()'), `${relImport} must define setup function`);
    assert.ok(fileContent.includes('template: `'), `${relImport} must define non-empty template string`);
  }
});
