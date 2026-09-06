#!/usr/bin/env node
/**
 * LinuxWebTool 前端架构门禁（零依赖，node frontend-gate.cjs）
 *
 * 规则（详见 docs/dev/architecture-gates.md）：
 *  FE-HTML-INLINE     index.html 禁止内联脚本（importmap 除外）与 on* 内联事件
 *  FE-API-OWNERSHIP   API 路径字符串只允许出现在 config.js
 *  FE-NO-FETCH        fetch() 只允许出现在 api/client.js
 *  FE-STORAGE         localStorage / sessionStorage 只允许出现在 api/client.js 与 store/auth.js
 *  FE-IMPORT-BOUNDARY 跨层 import 限制（store→views 禁止、api→views/components 禁止、views 互引禁止）
 *  FE-TEMPLATE-REF    模板事件绑定必须使用内联调用（method()），避免运行时编译提升裸标识符导致 handler 丢失
 *
 * 基线：frontend-gate-baseline.json 冻结存量债务，只允许删除条目，不允许新增。
 */
'use strict';

const fs = require('fs');
const path = require('path');

const appDir = path.join(__dirname, 'app');
const baselineFile = path.join(__dirname, 'frontend-gate-baseline.json');

function walk(dir, files = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'vendor' || entry.name === 'node_modules') continue;
      walk(full, files);
    } else if (/\.(js|html|css)$/.test(entry.name)) {
      files.push(full);
    }
  }
  return files;
}

function readBaseline() {
  try {
    return JSON.parse(fs.readFileSync(baselineFile, 'utf8')).violations || {};
  } catch {
    return {};
  }
}

const files = walk(appDir);
const violations = [];
const add = (rule, file, detail) => violations.push({ rule, file: path.relative(appDir, file).replace(/\\/g, '/'), detail });

for (const file of files) {
  const rel = path.relative(appDir, file).replace(/\\/g, '/');
  const content = fs.readFileSync(file, 'utf8');

  if (file.endsWith('.html')) {
    // FE-HTML-INLINE：除 importmap 外禁止无 src 的 script；禁止 on* 内联事件
    const scripts = content.matchAll(/<script\b([^>]*)>/g);
    for (const [, attrs] of scripts) {
      const isImportMap = /type\s*=\s*["']importmap["']/.test(attrs);
      const hasSrc = /\bsrc\s*=/.test(attrs);
      if (!isImportMap && !hasSrc) add('FE-HTML-INLINE', file, '存在内联 <script>（仅 importmap 允许）');
      if (/\bon[a-z]+\s*=/i.test(attrs)) add('FE-HTML-INLINE', file, '存在内联事件属性');
    }
    if (/\s(on[a-z]+)\s*=\s*["'][^"']*["']/i.test(content.replace(/<script[\s\S]*?<\/script>/g, ''))) {
      add('FE-HTML-INLINE', file, 'HTML 元素存在 on* 内联事件');
    }
    if (rel === 'index.html' && !/<base\s+href="\/app\/"/.test(content)) {
      add('FE-HTML-INLINE', file, 'index.html 缺少 <base href="/app/">');
    }
  }

  if (file.endsWith('.js')) {
    // FE-API-OWNERSHIP
    if (!rel.endsWith('config.js') && /["'`]\/api\//.test(content)) {
      add('FE-API-OWNERSHIP', file, 'API 路径字符串只能出现在 config.js');
    }
    // FE-NO-FETCH
    if (!rel.endsWith('client.js') && /\bfetch\s*\(/.test(content)) {
      add('FE-NO-FETCH', file, 'fetch() 只允许在 api/client.js 中调用');
    }
    // FE-STORAGE
    if (!['client.js', 'auth.js'].includes(path.basename(rel)) && /\blocalStorage\b|\bsessionStorage\b/.test(content)) {
      add('FE-STORAGE', file, 'Web Storage 只允许在 api/client.js 与 store/auth.js 中访问');
    }
    // FE-IMPORT-BOUNDARY
    const imports = [...content.matchAll(/import\s+(?:[\s\S]*?from\s+)?["'](\.[^"']+)["']/g)].map((m) => m[1]);
    for (const spec of imports) {
      const target = path.normalize(path.join(path.dirname(file), spec)).replace(/\\/g, '/');
      const targetRel = path.relative(appDir, target).replace(/\\/g, '/');
      if (rel.startsWith('store/') && /\/(views|components)\//.test(`/${targetRel}`)) {
        add('FE-IMPORT-BOUNDARY', file, `store 禁止 import ${targetRel}`);
      }
      if (rel.startsWith('api/') && /\/(views|components)\//.test(`/${targetRel}`)) {
        add('FE-IMPORT-BOUNDARY', file, `api 禁止 import ${targetRel}`);
      }
      if (rel.startsWith('views/') && targetRel.startsWith('views/') && targetRel !== rel) {
        add('FE-IMPORT-BOUNDARY', file, `views 禁止互相 import（${targetRel}）`);
      }
      if (rel.startsWith('components/') && /\/views\//.test(`/${targetRel}`)) {
        add('FE-IMPORT-BOUNDARY', file, `components 禁止 import views（${targetRel}）`);
      }
    }
    // FE-TEMPLATE-REF：模板事件绑定禁止裸标识符（运行时编译会错误提升导致 handler 丢失）
    const bare = content.matchAll(/@(?:click|blur|keyup(?:\.[a-z]+)*|submit(?:\.[a-z]+)*|change)\s*=\s*"([A-Za-z_$][\w$]*)"/g);
    for (const [, name] of bare) {
      add('FE-TEMPLATE-REF', file, `事件绑定使用了裸标识符 "${name}"，必须写成 "${name}()"`);
    }
  }
}

// 基线比对
const baseline = readBaseline();
const baselineSet = new Set();
for (const [rule, entries] of Object.entries(baseline)) {
  for (const entry of entries) baselineSet.add(`${rule}|${entry}`);
}
const fresh = violations.filter((v) => !baselineSet.has(`${v.rule}|${v.file}:${v.detail}`));
const unusedBaseline = [...baselineSet].filter((key) => {
  const [rule, rest] = key.split('|');
  const idx = rest.lastIndexOf(':');
  return !violations.some((v) => v.rule === rule && `${v.file}:${v.detail}` === rest);
});

console.log(`前端架构门禁：扫描 ${files.length} 个文件`);
for (const v of fresh) {
  console.error(`  [${v.rule}] ${v.file} — ${v.detail}`);
}
if (fresh.length > 0) {
  console.error(`\n❌ 前端门禁失败：${fresh.length} 个新违规。修复依赖，不得扩充基线。`);
  process.exit(1);
}
if (unusedBaseline.length > 0) {
  console.warn(`⚠ 基线中 ${unusedBaseline.length} 条已失效，请从 frontend-gate-baseline.json 删除（基线只减不增）：`);
  for (const key of unusedBaseline) console.warn(`   - ${key}`);
}
console.log('✅ 前端架构门禁通过');
