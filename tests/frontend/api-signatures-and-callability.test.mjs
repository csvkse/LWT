import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const appDir = path.resolve(__dirname, '../../src/LinuxWebTool.WebHost/wwwroot/app');
const configFile = path.join(appDir, 'config.js');

function loadApiConfig() {
  const source = fs.readFileSync(configFile, 'utf8').replace(/\bexport\s+const\s+/g, 'const ');
  const sandbox = {
    window: {
      location: {
        origin: 'http://localhost:5000',
        host: 'localhost:5000',
        protocol: 'http:',
      },
    },
    URLSearchParams: globalThis.URLSearchParams,
    encodeURIComponent: globalThis.encodeURIComponent,
    Boolean: globalThis.Boolean,
  };
  const result = new vm.Script(`${source}; ({ API });`).runInNewContext(sandbox);
  return result.API;
}

function walk(dir, files = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'vendor' || entry.name === 'node_modules') continue;
      walk(full, files);
    } else if (entry.name.endsWith('.js')) {
      files.push(full);
    }
  }
  return files;
}

test('API 配置文件中所有属性必须为有效路径字符串或合法 URL 构建函数', () => {
  const api = loadApiConfig();
  assert.ok(api, 'API 对象必须成功加载');

  function checkEndpoints(obj, prefix = 'API') {
    for (const [key, val] of Object.entries(obj)) {
      const currentPath = `${prefix}.${key}`;
      if (typeof val === 'function') {
        const dummyArgs = Array.from({ length: Math.max(val.length, 3) }, (_, i) => `mock_param_${i}`);
        let result;
        assert.doesNotThrow(() => {
          result = val(...dummyArgs);
        }, `${currentPath} 作为函数调用时不应抛出异常`);

        assert.equal(typeof result, 'string', `${currentPath} 调用后必须返回字符串`);
        assert.ok(
          result.startsWith('/') || result.startsWith('http://') || result.startsWith('https://') || result.startsWith('ws://') || result.startsWith('wss://'),
          `${currentPath} 返回的 URL 必须以 / 或 http/ws 开头: ${result}`
        );
        assert.ok(!result.includes('undefined'), `${currentPath} 生成的 URL 不应包含 undefined: ${result}`);
      } else if (typeof val === 'string') {
        assert.ok(val.startsWith('/'), `${currentPath} 静态路径必须以 / 开头: ${val}`);
      } else if (val && typeof val === 'object') {
        checkEndpoints(val, currentPath);
      } else {
        assert.fail(`${currentPath} 类型无效: ${typeof val}`);
      }
    }
  }

  checkEndpoints(api);
});

test('前端业务代码中对 API 的调用必须与 config.js 中的函数/字符串定义 100% 契合', () => {
  const api = loadApiConfig();
  const files = walk(appDir);
  const errors = [];

  for (const file of files) {
    if (file.endsWith('config.js') || file.endsWith('client.js')) continue;
    const content = fs.readFileSync(file, 'utf8');
    const relFile = path.relative(appDir, file).replace(/\\/g, '/');

    const apiMatches = content.matchAll(/\bAPI\.([A-Za-z0-9_$]+(?:\.[A-Za-z0-9_$]+)+)(\s*\()?/g);
    for (const [, fullPath, isCall] of apiMatches) {
      const parts = fullPath.split('.');
      let target = api;
      let valid = true;
      for (const p of parts) {
        if (target === undefined || target === null) {
          valid = false;
          break;
        }
        target = target[p];
      }

      if (!valid || target === undefined) {
        errors.push(`[未定义] ${relFile} 访问了不存在的 API.${fullPath}`);
        continue;
      }

      const isFunc = typeof target === 'function';
      if (isCall && !isFunc) {
        errors.push(`[类型错误] ${relFile} 将非函数 API.${fullPath} (类型为 ${typeof target}) 作为函数调用`);
      }
    }
  }

  assert.deepEqual(errors, [], `发现 API 契约调用违规项:\n${errors.join('\n')}`);
});
